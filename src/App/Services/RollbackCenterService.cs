using System.IO;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class RollbackCenterService
{
    private readonly OperationRecoveryService recovery;
    private readonly string pageFileStatePath;
    private readonly string powerPlanStatePath;
    private readonly string serviceStartupStatePath;
    private readonly string startupEntryStatePath;
    private readonly string edgeQuarantineRoot;

    public RollbackCenterService(
        OperationRecoveryService recovery,
        string? pageFileStatePath = null,
        string? powerPlanStatePath = null,
        string? serviceStartupStatePath = null,
        string? edgeQuarantineRoot = null,
        string? startupEntryStatePath = null)
    {
        this.recovery = recovery;
        this.pageFileStatePath = string.IsNullOrWhiteSpace(pageFileStatePath)
            ? AppPaths.PageFileState
            : Path.GetFullPath(pageFileStatePath);
        this.powerPlanStatePath = string.IsNullOrWhiteSpace(powerPlanStatePath)
            ? AppPaths.PowerPlanState
            : Path.GetFullPath(powerPlanStatePath);
        this.serviceStartupStatePath =
            string.IsNullOrWhiteSpace(serviceStartupStatePath)
                ? AppPaths.ServiceStartupState
                : Path.GetFullPath(serviceStartupStatePath);
        this.startupEntryStatePath =
            string.IsNullOrWhiteSpace(startupEntryStatePath)
                ? AppPaths.StartupEntryState
                : Path.GetFullPath(startupEntryStatePath);
        this.edgeQuarantineRoot =
            string.IsNullOrWhiteSpace(edgeQuarantineRoot)
                ? AppPaths.EdgeExtensionQuarantine
                : Path.GetFullPath(edgeQuarantineRoot);
    }

    public RollbackCenterReport Analyze(
        string? excludeOperationId = null)
    {
        var recoveryStatus = recovery.Analyze(excludeOperationId);
        var entries = new List<RollbackCenterEntry>();

        var ecoTargets = recoveryStatus.EcoQosTargets
            .Where(target => target.Restorable)
            .ToArray();
        if (ecoTargets.Length > 0)
        {
            entries.Add(new RollbackCenterEntry(
                "ecoqos",
                "EcoQoS",
                $"{ecoTargets.Length} proceso(s) con estado previo restorable.",
                "cpu.ecoqos.restore",
                new Dictionary<string, object?>
                {
                    ["processIds"] = ecoTargets
                        .Select(target => target.ProcessId)
                        .ToArray()
                },
                false,
                "SAFE"));
        }

        if (HasNonEmptyState(pageFileStatePath))
        {
            entries.Add(new RollbackCenterEntry(
                "pagefile",
                "Archivo de paginación",
                "Configuración previa guardada por la app.",
                "memory.pagefile.restore",
                new Dictionary<string, object?>(),
                true,
                "SAFE"));
        }

        if (HasNonEmptyState(powerPlanStatePath))
        {
            entries.Add(new RollbackCenterEntry(
                "power-plan",
                "Plan de energía",
                "Plan activo anterior disponible para restaurar.",
                "thermal.power.restore",
                new Dictionary<string, object?>(),
                false,
                "SAFE"));
        }

        foreach (var serviceName in ReadServiceSnapshots())
        {
            entries.Add(new RollbackCenterEntry(
                "service:" + serviceName,
                "Servicio · " + serviceName,
                "StartMode anterior guardado.",
                "startup.service.restore",
                new Dictionary<string, object?>
                {
                    ["serviceName"] = serviceName
                },
                true,
                "SAFE"));
        }

        foreach (var startupEntry in ReadStartupEntrySnapshots())
        {
            entries.Add(new RollbackCenterEntry(
                "startup-entry:" + startupEntry.EntryId,
                "Autoarranque · " + startupEntry.Name,
                "Entrada Run/RunOnce deshabilitada por la app.",
                "startup.entry.restore",
                new Dictionary<string, object?>
                {
                    ["entryId"] = startupEntry.EntryId
                },
                true,
                "SAFE"));
        }

        var edgeBatch = FindLatestEdgeBatch();
        if (edgeBatch is not null)
        {
            entries.Add(new RollbackCenterEntry(
                "edge:" + edgeBatch,
                "Extensiones Edge",
                "Último lote de residuos en cuarentena: " + edgeBatch,
                "browsers.extensions.orphans.restore",
                new Dictionary<string, object?>(),
                false,
                "CAUTION"));
        }

        return new RollbackCenterReport(
            entries
                .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            recoveryStatus.IncompleteOperations);
    }

    private static bool HasNonEmptyState(string path)
    {
        try
        {
            return File.Exists(path) &&
                new FileInfo(path).Length > 2;
        }
        catch
        {
            return false;
        }
    }

    private IReadOnlyList<string> ReadServiceSnapshots()
    {
        try
        {
            if (!File.Exists(serviceStartupStatePath))
                return [];

            using var document = JsonDocument.Parse(
                File.ReadAllText(serviceStartupStatePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            return document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(64)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private IReadOnlyList<StartupEntryRollbackSnapshot>
        ReadStartupEntrySnapshots()
    {
        try
        {
            if (!File.Exists(startupEntryStatePath))
                return [];

            using var document = JsonDocument.Parse(
                File.ReadAllText(startupEntryStatePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var result = new List<StartupEntryRollbackSnapshot>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var id = property.Value.TryGetProperty("entryId", out var idNode)
                    ? idNode.GetString()
                    : property.Name;
                var name = property.Value.TryGetProperty("name", out var nameNode)
                    ? nameNode.GetString()
                    : property.Name;
                if (string.IsNullOrWhiteSpace(id) ||
                    string.IsNullOrWhiteSpace(name))
                    continue;

                result.Add(new StartupEntryRollbackSnapshot(id, name));
                if (result.Count >= 64)
                    break;
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private string? FindLatestEdgeBatch()
    {
        try
        {
            if (!Directory.Exists(edgeQuarantineRoot))
                return null;

            return Directory
                .EnumerateDirectories(edgeQuarantineRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderByDescending(
                    name => name,
                    StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record StartupEntryRollbackSnapshot(
    string EntryId,
    string Name);

public sealed record RollbackCenterEntry(
    string Id,
    string Title,
    string Detail,
    string ActionId,
    IReadOnlyDictionary<string, object?> Parameters,
    bool RequiresAdmin,
    string Risk);

public sealed record RollbackCenterReport(
    IReadOnlyList<RollbackCenterEntry> Entries,
    IReadOnlyList<RecoveryOperation> IncompleteOperations);
