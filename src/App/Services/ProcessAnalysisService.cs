using System.ComponentModel;
using System.Diagnostics;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class ProcessAnalysisService
{
    private const long MemoryPreviewThresholdBytes =
        128L * 1024 * 1024;
    private const int CandidateLimit = 20;
    private static readonly TimeSpan PreviewTtl =
        TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private CandidateSnapshot? _memoryPreview;
    private CandidateSnapshot? _ecoQosPreview;

    public ProcessAnalysis AnalyzeMemoryTrimCandidates()
    {
        var observed = 0;
        var candidates = new List<ProcessCandidate>();
        var identities = new Dictionary<int, ProcessIdentity>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!TryReadProcess(
                        process,
                        out var candidate,
                        out var identity))
                {
                    continue;
                }

                observed++;
                if (candidate.WorkingSetBytes <
                        MemoryPreviewThresholdBytes ||
                    !ProcessSafetyPolicy.IsEligible(
                        candidate,
                        requireBackground: true))
                {
                    continue;
                }

                candidates.Add(candidate);
                identities[candidate.ProcessId] = identity;
            }
        }

        var selected = candidates
            .OrderByDescending(item => item.WorkingSetBytes)
            .Take(CandidateLimit)
            .ToArray();
        StorePreview(
            ref _memoryPreview,
            selected,
            identities);

        return new ProcessAnalysis(observed, selected);
    }

    public ProcessAnalysis AnalyzeEcoQosCandidates()
    {
        var observed = 0;
        var candidates = new List<ProcessCandidate>();
        var identities = new Dictionary<int, ProcessIdentity>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!TryReadProcess(
                        process,
                        out var candidate,
                        out var identity))
                {
                    continue;
                }

                observed++;
                if (!ProcessSafetyPolicy.IsEligible(
                        candidate,
                        requireBackground: true))
                {
                    continue;
                }

                candidates.Add(candidate);
                identities[candidate.ProcessId] = identity;
            }
        }

        var selected = candidates
            .OrderByDescending(item => item.WorkingSetBytes)
            .Take(CandidateLimit)
            .ToArray();
        StorePreview(
            ref _ecoQosPreview,
            selected,
            identities);

        return new ProcessAnalysis(observed, selected);
    }

    public void ValidateMemoryTrimSelection(
        IReadOnlyCollection<int> processIds) =>
        ValidateSelection(processIds, _memoryPreview, "MemoryTrim");

    public void ValidateEcoQosSelection(
        IReadOnlyCollection<int> processIds) =>
        ValidateSelection(processIds, _ecoQosPreview, "EcoQoS");

    private void StorePreview(
        ref CandidateSnapshot? target,
        IReadOnlyCollection<ProcessCandidate> selected,
        IReadOnlyDictionary<int, ProcessIdentity> identities)
    {
        var allowed = selected
            .Where(candidate =>
                identities.ContainsKey(candidate.ProcessId))
            .ToDictionary(
                candidate => candidate.ProcessId,
                candidate => identities[candidate.ProcessId]);

        lock (_gate)
        {
            target = new CandidateSnapshot(
                DateTimeOffset.UtcNow,
                allowed);
        }
    }

    private void ValidateSelection(
        IReadOnlyCollection<int> processIds,
        CandidateSnapshot? preview,
        string operation)
    {
        CandidateSnapshot snapshot;
        lock (_gate)
        {
            snapshot = preview
                ?? throw new InvalidOperationException(
                    $"{operation}: ejecutá primero el análisis previo.");
        }

        if (DateTimeOffset.UtcNow - snapshot.CapturedAt > PreviewTtl)
        {
            throw new InvalidOperationException(
                $"{operation}: la previsualización expiró; analizá de nuevo.");
        }

        foreach (var processId in processIds)
        {
            if (!snapshot.Identities.TryGetValue(
                    processId,
                    out var expected))
            {
                throw new InvalidOperationException(
                    $"{operation}: PID {processId} no pertenece a la previsualización.");
            }

            ValidateCurrentIdentity(processId, expected, operation);
        }
    }

    private static void ValidateCurrentIdentity(
        int processId,
        ProcessIdentity expected,
        string operation)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var currentName = process.ProcessName;
            var currentStart = new DateTimeOffset(
                process.StartTime.ToUniversalTime());

            if (!string.Equals(
                    currentName,
                    expected.Name,
                    StringComparison.OrdinalIgnoreCase) ||
                currentStart != expected.StartTime)
            {
                throw new InvalidOperationException(
                    $"{operation}: el PID {processId} cambió de identidad; operación bloqueada.");
            }
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"{operation}: el proceso {processId} ya no existe.",
                ex);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"{operation}: no se pudo volver a verificar el proceso {processId}.",
                ex);
        }
        catch (InvalidOperationException ex) when (
            !ex.Message.StartsWith(operation + ":", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{operation}: no se pudo volver a verificar el proceso {processId}.",
                ex);
        }
    }

    private static bool TryReadProcess(
        Process process,
        out ProcessCandidate candidate,
        out ProcessIdentity identity)
    {
        candidate = default!;
        identity = default!;

        try
        {
            if (process.Id <= 0)
                return false;

            var name = string.IsNullOrWhiteSpace(process.ProcessName)
                ? "Unknown"
                : process.ProcessName;
            var startTime = new DateTimeOffset(
                process.StartTime.ToUniversalTime());

            candidate = new ProcessCandidate(
                process.Id,
                name,
                Math.Max(0, process.WorkingSet64),
                process.MainWindowHandle != IntPtr.Zero);
            identity = new ProcessIdentity(name, startTime);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private sealed record ProcessIdentity(
        string Name,
        DateTimeOffset StartTime);

    private sealed record CandidateSnapshot(
        DateTimeOffset CapturedAt,
        IReadOnlyDictionary<int, ProcessIdentity> Identities);
}
