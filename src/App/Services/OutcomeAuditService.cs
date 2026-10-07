using System.IO;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class OutcomeAuditService
{
    private static readonly IReadOnlyDictionary<string, OutcomePolicy> Policies =
        new Dictionary<string, OutcomePolicy>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["system.workload.inuse"] = new("DIRECT_VERIFIED", "Estado interno persistido y releído.", true),
            ["system.workload.auto"] = new("DIRECT_VERIFIED", "Estado interno persistido y releído.", true),
            ["system.workload.maintenance"] = new("DIRECT_VERIFIED", "Estado interno persistido y releído.", true),
            ["system.integrity.repair"] = new("POSTCHECK", "DISM/SFC seguidos por CheckHealth.", true),
            ["memory.trim"] = new("TRANSIENT", "Compara working set antes/después; el efecto no es persistente.", false),
            ["memory.pagefile.capped"] = new("POSTCHECK", "Verifica configuración del pagefile después de escribir.", true),
            ["memory.pagefile.restore"] = new("POSTCHECK", "Restaura y verifica la configuración capturada.", true),
            ["cpu.ecoqos.apply"] = new("POSTCHECK", "Lee el estado EcoQoS después de aplicarlo.", true),
            ["cpu.ecoqos.restore"] = new("POSTCHECK", "Verifica el estado restaurado del proceso.", true),
            ["disk.cleanup.execute"] = new("DIRECT_VERIFIED", "Elimina únicamente candidatos previsualizados y reporta resultado por archivo/categoría.", false),
            ["disk.hibernate.reduce"] = new("DIRECT_VERIFIED", "powercfg devuelve resultado de la configuración solicitada.", false),
            ["network.flushdns"] = new("DIRECT_VERIFIED", "El comando de Windows confirma el vaciado; no deja estado persistente propio.", false),
            ["network.winsock.reset"] = new("REBOOT_REQUIRED", "El reset se acepta ahora, pero el estado final requiere reinicio.", false),
            ["drivers.rescan"] = new("POSTCHECK", "Reescanea PnP y vuelve a analizar códigos de problema.", false),
            ["drivers.usb.restart"] = new("POSTCHECK", "Reinicia, reescanea y verifica el código de problema del dispositivo exacto.", false),
            ["browsers.extensions.orphans.quarantine"] = new("POSTCHECK", "Mueve a cuarentena app-owned y conserva rollback.", true),
            ["browsers.extensions.orphans.restore"] = new("POSTCHECK", "Devuelve el lote y comprueba su restauración.", false),
            ["multimedia.audio.restart"] = new("POSTCHECK", "Reinicia Windows Audio y vuelve a inventariar dispositivos.", false),
            ["startup.service.setmode"] = new("POSTCHECK", "Relee StartMode y conserva snapshot.", true),
            ["startup.service.restore"] = new("POSTCHECK", "Relee StartMode después del rollback.", false),
            ["startup.entry.disable"] = new("POSTCHECK", "Comprueba ausencia del valor Run/RunOnce y conserva snapshot.", true),
            ["startup.entry.restore"] = new("POSTCHECK", "Comprueba el valor exacto después del rollback.", false),
            ["windows.update.services.restart"] = new("POSTCHECK", "Reinicia servicios y vuelve a auditar Windows Update.", false),
            ["thermal.power.balanced"] = new("POSTCHECK", "Lee el plan activo después del cambio.", true),
            ["thermal.power.performance"] = new("POSTCHECK", "Lee el plan activo después del cambio.", true),
            ["thermal.power.restore"] = new("POSTCHECK", "Lee el plan activo después del rollback.", false),
            ["explorer.restart"] = new("POSTCHECK", "Comprueba que explorer.exe vuelva a existir y responder.", false),
            ["maintenance.policy.readonly"] = new("DIRECT_VERIFIED", "Política app-owned persistida y releída; no modifica Windows.", true),
            ["maintenance.policy.off"] = new("DIRECT_VERIFIED", "Política app-owned persistida y releída; no modifica Windows.", true)
        };

    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "COMPLETED",
            "FAILED",
            "REJECTED",
            "CANCELLED"
        };

    private readonly ActionCatalog catalog;
    private readonly string logPath;

    public OutcomeAuditService(
        ActionCatalog catalog,
        string? logPath = null)
    {
        this.catalog = catalog;
        this.logPath = string.IsNullOrWhiteSpace(logPath)
            ? AppPaths.AppLog
            : Path.GetFullPath(logPath);
    }

    public OutcomeAuditReport Analyze()
    {
        var writes = catalog.All
            .Where(action => action.Mode == ActionMode.WRITE)
            .OrderBy(action => action.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var evidence = ReadOperationEvidence();
        var rows = writes.Select(action =>
        {
            var policy = Policies.TryGetValue(action.Id, out var known)
                ? known
                : new OutcomePolicy(
                    "UNCLASSIFIED",
                    "La acción WRITE todavía no tiene una política de outcome registrada.",
                    action.Reversible);

            evidence.TryGetValue(action.Id, out var observed);
            return new OutcomeCoverageItem(
                action.Id,
                action.Title,
                policy.Level,
                policy.Detail,
                action.Reversible,
                policy.RollbackExpected,
                observed?.Completed ?? 0,
                observed?.Failed ?? 0,
                observed?.Rejected ?? 0,
                observed?.Incomplete ?? 0,
                observed?.LastTerminalAt);
        }).ToArray();

        var classified = rows.Count(item =>
            item.VerificationLevel != "UNCLASSIFIED");
        var postchecked = rows.Count(item =>
            item.VerificationLevel == "POSTCHECK");
        var incomplete = rows.Sum(item => item.IncompleteRuns);

        return new OutcomeAuditReport(
            rows.Length,
            classified,
            postchecked,
            rows.Length == 0
                ? 100d
                : Math.Round(classified * 100d / rows.Length, 1),
            incomplete,
            rows);
    }

    internal static IReadOnlyDictionary<string, OutcomePolicy>
        KnownPoliciesForTests() => Policies;

    private Dictionary<string, OutcomeEvidence> ReadOperationEvidence()
    {
        var operations = new Dictionary<string, OperationLogState>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in GetLogFiles())
        {
            try
            {
                foreach (var line in ReadLinesShared(path))
                    ParseLine(line, operations);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var byAction = new Dictionary<string, OutcomeEvidence>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var state in operations.Values)
        {
            if (!byAction.TryGetValue(state.ActionId, out var value))
                value = new OutcomeEvidence();

            if (!TerminalStatuses.Contains(state.LastStatus))
            {
                value.Incomplete++;
            }
            else if (state.LastStatus.Equals(
                         "COMPLETED",
                         StringComparison.OrdinalIgnoreCase))
            {
                value.Completed++;
                value.LastTerminalAt = Max(
                    value.LastTerminalAt,
                    state.LastTimestamp);
            }
            else if (state.LastStatus.Equals(
                         "FAILED",
                         StringComparison.OrdinalIgnoreCase))
            {
                value.Failed++;
                value.LastTerminalAt = Max(
                    value.LastTerminalAt,
                    state.LastTimestamp);
            }
            else
            {
                value.Rejected++;
                value.LastTerminalAt = Max(
                    value.LastTerminalAt,
                    state.LastTimestamp);
            }

            byAction[state.ActionId] = value;
        }

        return byAction;
    }

    private IReadOnlyList<string> GetLogFiles()
    {
        var directory = Path.GetDirectoryName(logPath);
        if (string.IsNullOrWhiteSpace(directory) ||
            !Directory.Exists(directory))
            return [];

        try
        {
            var result = new DirectoryInfo(directory)
                .GetFiles("app-*.jsonl")
                .OrderBy(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .ToList();
            if (File.Exists(logPath))
                result.Add(logPath);
            return result;
        }
        catch
        {
            return File.Exists(logPath)
                ? [logPath]
                : [];
        }
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static void ParseLine(
        string line,
        IDictionary<string, OperationLogState> operations)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            var actionId = ReadString(root, "actionId");
            var status = ReadString(root, "status");
            var operationId = ReadString(root, "operationId");

            if (string.IsNullOrWhiteSpace(actionId) ||
                string.IsNullOrWhiteSpace(status) ||
                !root.TryGetProperty("timestamp", out var timeNode) ||
                timeNode.ValueKind != JsonValueKind.String ||
                !timeNode.TryGetDateTimeOffset(out var timestamp))
            {
                return;
            }

            var key = string.IsNullOrWhiteSpace(operationId)
                ? "legacy:" + actionId
                : operationId;

            if (!operations.TryGetValue(key, out var state))
            {
                state = new OperationLogState(actionId);
                operations[key] = state;
            }

            if (timestamp < state.LastTimestamp)
                return;

            if (timestamp == state.LastTimestamp &&
                TerminalStatuses.Contains(state.LastStatus) &&
                status.Equals("STARTED", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            state.LastStatus = status;
            state.LastTimestamp = timestamp;
        }
        catch (JsonException)
        {
        }
    }

    private static string? ReadString(
        JsonElement element,
        string property) =>
        element.TryGetProperty(property, out var node) &&
        node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static DateTimeOffset? Max(
        DateTimeOffset? left,
        DateTimeOffset right) =>
        left is null || right > left.Value
            ? right
            : left;

    internal sealed record OutcomePolicy(
        string Level,
        string Detail,
        bool RollbackExpected);

    private sealed class OutcomeEvidence
    {
        public int Completed { get; set; }
        public int Failed { get; set; }
        public int Rejected { get; set; }
        public int Incomplete { get; set; }
        public DateTimeOffset? LastTerminalAt { get; set; }
    }

    private sealed class OperationLogState(string actionId)
    {
        public string ActionId { get; } = actionId;
        public string LastStatus { get; set; } = "UNKNOWN";
        public DateTimeOffset LastTimestamp { get; set; } =
            DateTimeOffset.MinValue;
    }
}

public sealed record OutcomeCoverageItem(
    string ActionId,
    string Title,
    string VerificationLevel,
    string VerificationDetail,
    bool Reversible,
    bool RollbackExpected,
    int CompletedRuns,
    int FailedRuns,
    int RejectedRuns,
    int IncompleteRuns,
    DateTimeOffset? LastTerminalAt);

public sealed record OutcomeAuditReport(
    int WriteActionCount,
    int ClassifiedCount,
    int PostcheckedCount,
    double CoveragePercent,
    int IncompleteRuns,
    IReadOnlyList<OutcomeCoverageItem> Actions);
