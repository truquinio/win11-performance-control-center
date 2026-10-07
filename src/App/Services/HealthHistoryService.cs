using System.IO;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class HealthHistoryService
{
    private const int MaxHistory = 64;
    private const long Gib = 1024L * 1024 * 1024;

    private readonly string statePath;
    private readonly Func<Task<SystemSnapshot>> snapshotReader;
    private readonly Func<Task<StorageVolumeAudit>> storageReader;
    private readonly Func<int, Task<IReadOnlyList<ReliabilityEventDto>>> reliabilityReader;
    private readonly Func<DateTimeOffset> clock;

    public HealthHistoryService(
        SystemSnapshotService snapshotService,
        StorageWatchService storageWatchService,
        ReliabilityService reliabilityService,
        string? statePath = null)
        : this(
            string.IsNullOrWhiteSpace(statePath)
                ? AppPaths.HealthHistoryState
                : Path.GetFullPath(statePath),
            snapshotService.CaptureAsync,
            storageWatchService.AuditAsync,
            reliabilityService.GetRecentAsync,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal HealthHistoryService(
        string statePath,
        Func<Task<SystemSnapshot>> snapshotReader,
        Func<Task<StorageVolumeAudit>> storageReader,
        Func<int, Task<IReadOnlyList<ReliabilityEventDto>>> reliabilityReader,
        Func<DateTimeOffset>? clock = null)
    {
        this.statePath = Path.GetFullPath(statePath);
        this.snapshotReader = snapshotReader;
        this.storageReader = storageReader;
        this.reliabilityReader = reliabilityReader;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);

        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public async Task<HealthScoreReport> CaptureAsync()
    {
        var snapshotTask = snapshotReader();
        var storageTask = storageReader();
        var reliabilityTask = reliabilityReader(80);

        await Task.WhenAll(snapshotTask, storageTask, reliabilityTask);

        var snapshot = await snapshotTask;
        var storage = await storageTask;
        var reliability = await reliabilityTask;

        var factors = ScoreFactors(snapshot, storage, reliability);
        var score = Math.Clamp(
            100 - factors.Sum(item => item.Penalty),
            0,
            100);
        var band = ScoreBand(score);

        var capture = ToCapture(
            snapshot,
            storage,
            reliability,
            score,
            band,
            clock());

        var state = LoadState();
        var previous = state.Captures
            .OrderBy(item => item.CapturedAt)
            .LastOrDefault();

        var changes = previous is null
            ? Array.Empty<HealthChange>()
            : Compare(previous, capture);

        var captures = state.Captures.ToList();
        captures.Add(capture);
        if (captures.Count > MaxHistory)
            captures = captures[^MaxHistory..];
        PersistState(new HealthHistoryState(captures));

        return new HealthScoreReport(
            capture.CapturedAt,
            score,
            band,
            previous?.CapturedAt,
            previous is null,
            captures.Count,
            factors,
            changes,
            captures
                .OrderByDescending(item => item.CapturedAt)
                .Take(12)
                .OrderBy(item => item.CapturedAt)
                .Select(item => new HealthTrendPoint(
                    item.CapturedAt,
                    item.Score,
                    item.Band,
                    item.MemoryUsedPercent,
                    item.SystemDiskFreePercent))
                .ToArray());
    }

    public HealthChangeReport ReadChanges()
    {
        var captures = LoadState().Captures
            .OrderBy(item => item.CapturedAt)
            .ToArray();

        if (captures.Length == 0)
        {
            return new HealthChangeReport(
                null,
                null,
                true,
                [],
                []);
        }

        var current = captures[^1];
        var previous = captures.Length >= 2
            ? captures[^2]
            : null;

        return new HealthChangeReport(
            previous?.CapturedAt,
            current.CapturedAt,
            previous is null,
            previous is null
                ? Array.Empty<HealthChange>()
                : Compare(previous, current),
            captures
                .TakeLast(12)
                .Select(item => new HealthTrendPoint(
                    item.CapturedAt,
                    item.Score,
                    item.Band,
                    item.MemoryUsedPercent,
                    item.SystemDiskFreePercent))
                .ToArray());
    }

    private static IReadOnlyList<HealthScoreFactor> ScoreFactors(
        SystemSnapshot snapshot,
        StorageVolumeAudit storage,
        IReadOnlyList<ReliabilityEventDto> reliability)
    {
        var result = new List<HealthScoreFactor>();
        var memoryPercent = snapshot.MemoryTotalBytes == 0
            ? 0d
            : snapshot.MemoryUsedBytes * 100d /
              snapshot.MemoryTotalBytes;
        var systemDiskPercent = snapshot.DiskTotalBytes <= 0
            ? 100d
            : snapshot.DiskFreeBytes * 100d /
              snapshot.DiskTotalBytes;

        AddRangeFactor(
            result,
            "Memoria",
            memoryPercent,
            [
                new Threshold(90, 20, "CRITICAL"),
                new Threshold(80, 10, "WATCH")
            ],
            $"{memoryPercent:F0}% de RAM en uso.");

        AddInverseRangeFactor(
            result,
            "Disco del sistema",
            systemDiskPercent,
            [
                new Threshold(5, 30, "CRITICAL"),
                new Threshold(10, 20, "LOW"),
                new Threshold(20, 10, "WATCH")
            ],
            $"{systemDiskPercent:F1}% libre en {snapshot.DiskDrive}.");

        foreach (var volume in storage.Volumes.Where(volume =>
                     !string.Equals(
                         volume.Name,
                         snapshot.DiskDrive,
                         StringComparison.OrdinalIgnoreCase)))
        {
            var penalty = volume.Status switch
            {
                "CRITICAL" => 20,
                "LOW" => 12,
                "WATCH" => 6,
                _ => 0
            };
            if (penalty > 0)
            {
                result.Add(new HealthScoreFactor(
                    "Almacenamiento",
                    volume.Status,
                    penalty,
                    $"{volume.Name} tiene {volume.FreePercent:F1}% libre."));
            }
        }

        var integrityPenalty = snapshot.IntegrityStatus switch
        {
            "UNREPAIRABLE" => 30,
            "REPAIRABLE" => 18,
            "ERROR" => 12,
            _ => 0
        };
        if (integrityPenalty > 0)
        {
            result.Add(new HealthScoreFactor(
                "Integridad",
                snapshot.IntegrityStatus,
                integrityPenalty,
                "Estado reportado por la comprobación de integridad de Windows."));
        }

        if (snapshot.DriverStatus == "WARNING")
        {
            result.Add(new HealthScoreFactor(
                "Drivers",
                "WARNING",
                10,
                "Windows reportó uno o más dispositivos con código de problema."));
        }

        if (snapshot.RebootRequired == true)
        {
            result.Add(new HealthScoreFactor(
                "Reinicio",
                "PENDING",
                5,
                "Windows indica que hay un reinicio pendiente."));
        }

        var whea = reliability.Count(item =>
            item.Provider.Equals(
                "Microsoft-Windows-WHEA-Logger",
                StringComparison.OrdinalIgnoreCase));
        if (whea > 0)
        {
            result.Add(new HealthScoreFactor(
                "Hardware",
                "WHEA",
                Math.Min(15, 5 + whea * 2),
                $"{whea} evento(s) WHEA en la ventana de fiabilidad."));
        }

        var unclean = reliability.Count(item =>
            (item.Provider.Equals(
                    "Microsoft-Windows-Kernel-Power",
                    StringComparison.OrdinalIgnoreCase) &&
                item.Id == 41) ||
            (item.Provider.Equals(
                    "EventLog",
                    StringComparison.OrdinalIgnoreCase) &&
                item.Id == 6008));
        if (unclean > 0)
        {
            result.Add(new HealthScoreFactor(
                "Estabilidad",
                "UNCLEAN_SHUTDOWN",
                Math.Min(12, 4 + unclean * 2),
                $"{unclean} apagado(s) o reinicio(s) no limpios registrados."));
        }

        var hangs = reliability.Count(item =>
            item.Provider.Equals(
                "Application Hang",
                StringComparison.OrdinalIgnoreCase));
        if (hangs >= 3)
        {
            result.Add(new HealthScoreFactor(
                "Aplicaciones",
                "HANGS",
                Math.Min(8, hangs),
                $"{hangs} eventos de aplicación sin responder."));
        }

        if (snapshot.CpuPercent >= 95)
        {
            result.Add(new HealthScoreFactor(
                "CPU",
                "SPIKE",
                5,
                $"Uso puntual de CPU: {snapshot.CpuPercent:F0}%. Se pondera poco porque es una muestra transitoria."));
        }

        return result
            .OrderByDescending(item => item.Penalty)
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddRangeFactor(
        ICollection<HealthScoreFactor> factors,
        string category,
        double value,
        IReadOnlyList<Threshold> thresholds,
        string detail)
    {
        foreach (var threshold in thresholds.OrderByDescending(item => item.Value))
        {
            if (value < threshold.Value)
                continue;

            factors.Add(new HealthScoreFactor(
                category,
                threshold.Status,
                threshold.Penalty,
                detail));
            return;
        }
    }

    private static void AddInverseRangeFactor(
        ICollection<HealthScoreFactor> factors,
        string category,
        double value,
        IReadOnlyList<Threshold> thresholds,
        string detail)
    {
        foreach (var threshold in thresholds.OrderBy(item => item.Value))
        {
            if (value > threshold.Value)
                continue;

            factors.Add(new HealthScoreFactor(
                category,
                threshold.Status,
                threshold.Penalty,
                detail));
            return;
        }
    }

    private static HealthCapture ToCapture(
        SystemSnapshot snapshot,
        StorageVolumeAudit storage,
        IReadOnlyList<ReliabilityEventDto> reliability,
        int score,
        string band,
        DateTimeOffset capturedAt)
    {
        var memoryPercent = snapshot.MemoryTotalBytes == 0
            ? 0d
            : snapshot.MemoryUsedBytes * 100d /
              snapshot.MemoryTotalBytes;
        var systemDiskPercent = snapshot.DiskTotalBytes <= 0
            ? 100d
            : snapshot.DiskFreeBytes * 100d /
              snapshot.DiskTotalBytes;

        var whea = reliability.Count(item =>
            item.Provider.Equals(
                "Microsoft-Windows-WHEA-Logger",
                StringComparison.OrdinalIgnoreCase));
        var unclean = reliability.Count(item =>
            (item.Provider.Equals(
                    "Microsoft-Windows-Kernel-Power",
                    StringComparison.OrdinalIgnoreCase) &&
                item.Id == 41) ||
            (item.Provider.Equals(
                    "EventLog",
                    StringComparison.OrdinalIgnoreCase) &&
                item.Id == 6008));

        return new HealthCapture(
            capturedAt,
            score,
            band,
            Math.Round(snapshot.CpuPercent, 1),
            Math.Round(memoryPercent, 1),
            snapshot.MemoryAvailableBytes,
            snapshot.DiskDrive,
            snapshot.DiskFreeBytes,
            Math.Round(systemDiskPercent, 1),
            snapshot.Network?.Name,
            snapshot.IntegrityStatus,
            snapshot.DriverStatus,
            snapshot.ActivationStatus,
            snapshot.RebootRequired,
            whea,
            unclean,
            storage.Volumes
                .Select(volume => new HealthVolumeCapture(
                    volume.Name,
                    volume.FreeBytes,
                    volume.FreePercent,
                    volume.Status))
                .ToArray());
    }

    private static IReadOnlyList<HealthChange> Compare(
        HealthCapture previous,
        HealthCapture current)
    {
        var changes = new List<HealthChange>();

        AddNumericChange(
            changes,
            "Health Score",
            previous.Score,
            current.Score,
            5,
            "puntos");

        AddNumericChange(
            changes,
            "RAM usada",
            previous.MemoryUsedPercent,
            current.MemoryUsedPercent,
            8,
            "%");

        AddNumericChange(
            changes,
            "CPU",
            previous.CpuPercent,
            current.CpuPercent,
            20,
            "%");

        var diskDelta = current.SystemDiskFreeBytes -
            previous.SystemDiskFreeBytes;
        if (Math.Abs(diskDelta) >= Gib)
        {
            changes.Add(new HealthChange(
                "Almacenamiento",
                current.SystemDiskDrive,
                diskDelta > 0 ? "IMPROVED" : "WORSE",
                previous.SystemDiskFreeBytes.ToString(),
                current.SystemDiskFreeBytes.ToString(),
                diskDelta,
                "bytes"));
        }

        AddStateChange(
            changes,
            "Integridad",
            previous.IntegrityStatus,
            current.IntegrityStatus);
        AddStateChange(
            changes,
            "Drivers",
            previous.DriverStatus,
            current.DriverStatus);
        AddStateChange(
            changes,
            "Reinicio pendiente",
            previous.RebootRequired?.ToString() ?? "UNKNOWN",
            current.RebootRequired?.ToString() ?? "UNKNOWN");
        AddStateChange(
            changes,
            "Red activa",
            previous.NetworkName ?? "NONE",
            current.NetworkName ?? "NONE");

        foreach (var currentVolume in current.Volumes)
        {
            var old = previous.Volumes.FirstOrDefault(volume =>
                string.Equals(
                    volume.Name,
                    currentVolume.Name,
                    StringComparison.OrdinalIgnoreCase));
            if (old is null)
            {
                changes.Add(new HealthChange(
                    "Volumen",
                    currentVolume.Name,
                    "NEW",
                    "ABSENT",
                    currentVolume.Status,
                    null,
                    null));
                continue;
            }

            if (!string.Equals(
                    old.Status,
                    currentVolume.Status,
                    StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new HealthChange(
                    "Volumen",
                    currentVolume.Name,
                    SeverityRank(currentVolume.Status) >
                    SeverityRank(old.Status)
                        ? "WORSE"
                        : "IMPROVED",
                    old.Status,
                    currentVolume.Status,
                    currentVolume.FreeBytes - old.FreeBytes,
                    "bytes"));
            }
            else
            {
                var delta = currentVolume.FreeBytes - old.FreeBytes;
                if (Math.Abs(delta) >= Gib)
                {
                    changes.Add(new HealthChange(
                        "Volumen",
                        currentVolume.Name,
                        delta > 0 ? "IMPROVED" : "WORSE",
                        old.FreeBytes.ToString(),
                        currentVolume.FreeBytes.ToString(),
                        delta,
                        "bytes"));
                }
            }
        }

        return changes
            .OrderBy(item => item.Impact == "WORSE" ? 0 :
                item.Impact == "IMPROVED" ? 1 : 2)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddNumericChange(
        ICollection<HealthChange> changes,
        string name,
        double before,
        double after,
        double threshold,
        string unit)
    {
        var delta = after - before;
        if (Math.Abs(delta) < threshold)
            return;

        var lowerIsBetter = name is "RAM usada" or "CPU";
        var improved = lowerIsBetter
            ? delta < 0
            : delta > 0;

        changes.Add(new HealthChange(
            "Métrica",
            name,
            improved ? "IMPROVED" : "WORSE",
            before.ToString("F1"),
            after.ToString("F1"),
            delta,
            unit));
    }

    private static void AddStateChange(
        ICollection<HealthChange> changes,
        string name,
        string before,
        string after)
    {
        if (string.Equals(
                before,
                after,
                StringComparison.OrdinalIgnoreCase))
            return;

        changes.Add(new HealthChange(
            "Estado",
            name,
            "CHANGED",
            before,
            after,
            null,
            null));
    }

    private static int SeverityRank(string status) =>
        status switch
        {
            "OK" => 0,
            "WATCH" => 1,
            "LOW" => 2,
            "CRITICAL" => 3,
            _ => 1
        };

    private static string ScoreBand(int score) =>
        score switch
        {
            >= 90 => "EXCELLENT",
            >= 75 => "GOOD",
            >= 55 => "WATCH",
            _ => "ACTION"
        };

    private HealthHistoryState LoadState() =>
        TryLoadState(statePath) ??
        TryLoadState(statePath + ".bak") ??
        new HealthHistoryState([]);

    private static HealthHistoryState? TryLoadState(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<HealthHistoryState>(
                File.ReadAllText(path),
                HostBridge.JsonOptions);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            return null;
        }
    }

    private void PersistState(HealthHistoryState state)
    {
        var directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temp = statePath + ".tmp";
        var backup = statePath + ".bak";
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            state,
            HostBridge.JsonOptions);

        using (var stream = new FileStream(
                   temp,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(statePath))
            File.Copy(statePath, backup, overwrite: true);
        File.Move(temp, statePath, overwrite: true);
    }

    private sealed record Threshold(
        double Value,
        int Penalty,
        string Status);
}

public sealed record HealthScoreFactor(
    string Category,
    string Status,
    int Penalty,
    string Detail);

public sealed record HealthChange(
    string Category,
    string Name,
    string Impact,
    string Before,
    string After,
    double? Delta,
    string? Unit);

public sealed record HealthTrendPoint(
    DateTimeOffset CapturedAt,
    int Score,
    string Band,
    double MemoryUsedPercent,
    double SystemDiskFreePercent);

public sealed record HealthScoreReport(
    DateTimeOffset CapturedAt,
    int Score,
    string Band,
    DateTimeOffset? PreviousCapturedAt,
    bool BaselineCreated,
    int HistoryCount,
    IReadOnlyList<HealthScoreFactor> Factors,
    IReadOnlyList<HealthChange> Changes,
    IReadOnlyList<HealthTrendPoint> Trend);

public sealed record HealthChangeReport(
    DateTimeOffset? PreviousCapturedAt,
    DateTimeOffset? CurrentCapturedAt,
    bool BaselineRequired,
    IReadOnlyList<HealthChange> Changes,
    IReadOnlyList<HealthTrendPoint> Trend);

public sealed record HealthVolumeCapture(
    string Name,
    long FreeBytes,
    double FreePercent,
    string Status);

public sealed record HealthCapture(
    DateTimeOffset CapturedAt,
    int Score,
    string Band,
    double CpuPercent,
    double MemoryUsedPercent,
    ulong MemoryAvailableBytes,
    string SystemDiskDrive,
    long SystemDiskFreeBytes,
    double SystemDiskFreePercent,
    string? NetworkName,
    string IntegrityStatus,
    string DriverStatus,
    string ActivationStatus,
    bool? RebootRequired,
    int WheaEvents,
    int UncleanShutdownEvents,
    IReadOnlyList<HealthVolumeCapture> Volumes);

public sealed record HealthHistoryState(
    IReadOnlyList<HealthCapture> Captures);
