using System.IO;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class OperationRecoveryService(
    EcoQosStateStore ecoQosStateStore,
    string? appLogPath = null)
{
    private readonly EcoQosStateStore _ecoQosStateStore = ecoQosStateStore;
    private readonly string _appLogPath = string.IsNullOrWhiteSpace(appLogPath)
            ? AppPaths.AppLog
            : Path.GetFullPath(appLogPath);
    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "COMPLETED",
            "FAILED",
            "CANCELLED",
            "REJECTED"
        };

    public RecoveryStatus Analyze(string? excludeOperationId = null)
    {
        var (incomplete, failedWriteReview, cancelledWriteReview) = ReadOperations(excludeOperationId);
        var states = _ecoQosStateStore.Snapshot();
        var snapshots = states
            .Select(state =>
                $"EcoQoS · PID {state.ProcessId} · {state.Name} · {state.CapturedAt:O}")
            .ToArray();

        // A snapshot is only actionable while the exact process instance it
        // was captured from is still running.
        var targets = states
            .Select(state => new RollbackTarget(
                state.ProcessId,
                state.Name,
                state.CapturedAt,
                ProcessTuningService.IsSameInstanceRunning(state) == true))
            .ToArray();

        return new RecoveryStatus(incomplete, snapshots, targets, failedWriteReview, cancelledWriteReview);
    }

    private (IReadOnlyList<RecoveryOperation> Incomplete,
        IReadOnlyList<RecoveryOperation> FailedWriteReview,
        IReadOnlyList<RecoveryOperation> CancelledWriteReview) ReadOperations(
        string? excludeOperationId)
    {
        var logFiles = GetLogFiles();
        if (logFiles.Count == 0)
            return ([], [], []);

        var states = new Dictionary<string, OperationStateRecord>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var logFile in logFiles)
        {
            try
            {
                foreach (var line in ReadLinesShared(logFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ParseLine(line, states);
                }
            }
            catch (IOException ex)
            {
                throw new IOException(
                    "No se pudo leer un diario de auditoría. El estado de recuperación no está verificado.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException(
                    "Acceso denegado a un diario de auditoría. El estado de recuperación no está verificado.", ex);
            }
        }

        var possibleChanges = states.Values
            .Where(value =>
                string.IsNullOrWhiteSpace(excludeOperationId) ||
                !string.Equals(
                    value.OperationId,
                    excludeOperationId,
                    StringComparison.OrdinalIgnoreCase))
            .Where(value =>
                value.StartedAt is not null &&
                value.MayHaveChangedState)
            .OrderByDescending(value => value.StartedAt)
            .ToArray();

        var incomplete = possibleChanges
            .Where(value => !TerminalStatuses.Contains(value.LastStatus))
            .Select(value => new RecoveryOperation(
                value.ActionId, value.StartedAt!.Value, value.LastStatus, true))
            .ToArray();
        // FAILED is terminal, not interrupted. A WRITE may nevertheless
        // have changed state before reporting failure; require inspection.
        var failed = possibleChanges
            .Where(value => value.LastStatus.Equals(
                "FAILED", StringComparison.OrdinalIgnoreCase))
            .Select(value => new RecoveryOperation(
                value.ActionId, value.StartedAt!.Value, value.LastStatus, false))
            .ToArray();
        // Cancellation is terminal, but it does not prove that a WRITE
        // stopped before applying changes. Keep it separate from FAILED.
        var cancelled = possibleChanges
            .Where(value => value.LastStatus.Equals(
                "CANCELLED", StringComparison.OrdinalIgnoreCase))
            .Select(value => new RecoveryOperation(
                value.ActionId, value.StartedAt!.Value, value.LastStatus, false))
            .ToArray();
        return (incomplete, failed, cancelled);
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

    private IReadOnlyList<string> GetLogFiles()
    {
        var directory = Path.GetDirectoryName(_appLogPath);
        if (string.IsNullOrWhiteSpace(directory) ||
            !Directory.Exists(directory))
            return [];

        try
        {
            var archives = new DirectoryInfo(directory)
                .GetFiles("app-*.jsonl")
                .OrderBy(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .ToList();

            if (File.Exists(_appLogPath))
                archives.Add(_appLogPath);

            return archives;
        }
        catch (IOException ex)
        {
            throw new IOException(
                "No se pudo enumerar los diarios de auditoría. El estado de recuperación no está verificado.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(
                "Acceso denegado al enumerar diarios de auditoría. El estado de recuperación no está verificado.", ex);
        }
    }

    private static void ParseLine(
        string line,
        IDictionary<string, OperationStateRecord> states)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            // A syntactically valid JSON scalar or array is not an audit
            // record. It might replace the only STARTED evidence of a WRITE;
            // never silently classify the recovery state as clean.
            if (root.ValueKind != JsonValueKind.Object)
                throw new IOException(
                    "Registro de auditoría no es un objeto JSON. El estado de recuperación no está verificado.");

            var actionId = root.TryGetProperty(
                    "actionId",
                    out var actionNode) &&
                actionNode.ValueKind == JsonValueKind.String
                    ? actionNode.GetString()
                    : null;
            var status = root.TryGetProperty(
                    "status",
                    out var statusNode) &&
                statusNode.ValueKind == JsonValueKind.String
                    ? statusNode.GetString()
                    : null;
            var operationId = root.TryGetProperty(
                "operationId",
                out var operationNode) &&
                operationNode.ValueKind == JsonValueKind.String
                    ? operationNode.GetString()
                    : null;

            // Valid JSON with missing/invalid audit fields can conceal the
            // only STARTED record for a WRITE. Fail closed without altering
            // the original journal or claiming that recovery is clean.
            if (string.IsNullOrWhiteSpace(actionId) ||
                string.IsNullOrWhiteSpace(status) ||
                (root.TryGetProperty("operationId", out var operationField) &&
                 operationField.ValueKind != JsonValueKind.String &&
                 operationField.ValueKind != JsonValueKind.Null) ||
                !root.TryGetProperty("timestamp", out var timeNode) ||
                timeNode.ValueKind != JsonValueKind.String ||
                !timeNode.TryGetDateTimeOffset(out var timestamp))
            {
                throw new IOException(
                    "Registro de auditoría incompleto o con campos inválidos. El estado de recuperación no está verificado.");
            }

            // Identity includes the action as well as the operation ID.
            // A colliding terminal record from another action must never
            // conceal a WRITE STARTED, regardless of archive read order.
            var key = !string.IsNullOrWhiteSpace(operationId)
                ? operationId.Length + ":" + operationId + ":" + actionId
                : "legacy:" + actionId;

            if (!states.TryGetValue(key, out var state))
            {
                state = new OperationStateRecord(actionId, operationId);
                states[key] = state;
            }

            // A terminal record for a different action cannot close this
            // operation, even if a malformed writer reused its ID.
            if (!string.Equals(
                    state.ActionId,
                    actionId,
                    StringComparison.OrdinalIgnoreCase))
                return;

            if (timestamp < state.LastTimestamp)
                return;

            if (timestamp == state.LastTimestamp &&
                TerminalStatuses.Contains(state.LastStatus) &&
                status.Equals(
                    "STARTED",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            state.LastTimestamp = timestamp;
            state.LastStatus = status;

            if (status.Equals(
                    "STARTED",
                    StringComparison.OrdinalIgnoreCase))
            {
                state.StartedAt = timestamp;
                state.MayHaveChangedState = !IsDeclaredReadOnly(root);
            }
        }
        catch (JsonException ex)
        {
            // A truncated JSONL record may be the only evidence of a
            // partially applied WRITE. Never report recovery as clean when
            // audit records cannot be interpreted.
            throw new IOException(
                "Registro de auditoría JSON inválido. El estado de recuperación no está verificado.", ex);
        }
    }

    /// <summary>
    /// True only when the STARTED record itself declares a READ or DRY_RUN
    /// mode. Records without a mode (older logs, other writers) stay
    /// reportable: an unknown operation is never assumed to be harmless.
    /// </summary>
    private static bool IsDeclaredReadOnly(JsonElement record)
    {
        if (!record.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("mode", out var mode) ||
            mode.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = mode.GetString();
        return string.Equals(value, "READ", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "DRY_RUN", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class OperationStateRecord(string actionId, string? operationId)
    {
        public string ActionId { get; } = actionId;
        public string? OperationId { get; } = operationId;
        public bool MayHaveChangedState { get; set; } = true;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset LastTimestamp { get; set; } = DateTimeOffset.MinValue;
        public string LastStatus { get; set; } = "UNKNOWN";
    }
}
