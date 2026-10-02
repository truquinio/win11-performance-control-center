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
        var incomplete = ReadIncompleteOperations(excludeOperationId);
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

        return new RecoveryStatus(incomplete, snapshots, targets);
    }

    private IReadOnlyList<RecoveryOperation> ReadIncompleteOperations(
        string? excludeOperationId)
    {
        var logFiles = GetLogFiles();
        if (logFiles.Count == 0)
            return [];

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
            catch (IOException)
            {
                // One rotated log must not hide evidence from the others.
            }
            catch (UnauthorizedAccessException)
            {
                // Recovery is best-effort when local policy restricts a log.
            }
        }

        return [.. states
            .Where(pair =>
                !string.Equals(
                    pair.Key,
                    excludeOperationId,
                    StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .Where(value =>
                value.StartedAt is not null &&
                !TerminalStatuses.Contains(value.LastStatus))
            .OrderByDescending(value => value.StartedAt)
            .Select(value => new RecoveryOperation(
                value.ActionId,
                value.StartedAt!.Value,
                value.LastStatus,
                true))];
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
        catch (IOException)
        {
            return File.Exists(_appLogPath)
                ? [_appLogPath]
                : Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
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

            if (string.IsNullOrWhiteSpace(actionId) ||
                string.IsNullOrWhiteSpace(status))
                return;

            if (!root.TryGetProperty(
                    "timestamp",
                    out var timeNode) ||
                timeNode.ValueKind != JsonValueKind.String ||
                !timeNode.TryGetDateTimeOffset(out var timestamp))
            {
                return;
            }

            var key = !string.IsNullOrWhiteSpace(operationId)
                ? operationId
                : "legacy:" + actionId;

            if (!states.TryGetValue(key, out var state))
            {
                state = new OperationStateRecord(actionId);
                states[key] = state;
            }

            if (status.Equals(
                    "STARTED",
                    StringComparison.OrdinalIgnoreCase))
            {
                state.StartedAt = timestamp;
                state.LastStatus = status;
                return;
            }

            if (state.StartedAt is not null)
                state.LastStatus = status;
        }
        catch (JsonException)
        {
            // Ignore one malformed historical line.
        }
    }

    private sealed class OperationStateRecord(string actionId)
    {
        public string ActionId { get; } = actionId;
        public DateTimeOffset? StartedAt { get; set; }
        public string LastStatus { get; set; } = "UNKNOWN";
    }
}
