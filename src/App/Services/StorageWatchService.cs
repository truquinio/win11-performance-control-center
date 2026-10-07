using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class StorageWatchService
{
    private const long Gib = 1024L * 1024 * 1024;
    private const double CriticalFreePercent = 5d;
    private const double LowFreePercent = 10d;
    private const double WatchFreePercent = 15d;
    private const long GrowthAlertBytes = 5L * Gib;
    private static readonly TimeSpan DefaultHotspotBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PerFolderBudget = TimeSpan.FromMilliseconds(700);
    private const int MaxHistory = 32;
    private const int MaxTopLevelFolders = 16;

    private readonly string statePath;
    private readonly Func<IReadOnlyList<VolumeProbe>> volumeProbe;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan hotspotBudget;

    internal sealed record VolumeProbe(
        string Name,
        string Label,
        string FileSystem,
        string RootPath,
        long TotalBytes,
        long FreeBytes);

    private sealed record WatchState(
        IReadOnlyList<WatchCapture> Captures);

    private sealed record WatchCapture(
        DateTimeOffset CapturedAt,
        IReadOnlyList<WatchVolume> Volumes);

    private sealed record WatchVolume(
        string Name,
        long TotalBytes,
        long FreeBytes);

    public StorageWatchService(
        string? statePath = null,
        string? evaluationRoot = null)
    {
        if (string.IsNullOrWhiteSpace(statePath))
        {
            AppPaths.EnsureDirectories();
            this.statePath = AppPaths.StorageWatchState;
        }
        else
        {
            this.statePath = Path.GetFullPath(statePath);
            var directory = Path.GetDirectoryName(this.statePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }

        if (!string.IsNullOrWhiteSpace(evaluationRoot))
        {
            var root = Path.GetFullPath(evaluationRoot);
            Directory.CreateDirectory(root);
            volumeProbe = () =>
            [
                new VolumeProbe(
                    "TEST:\\",
                    "Test fixture",
                    "TEST",
                    root,
                    10L * Gib,
                    5L * Gib)
            ];
        }
        else
        {
            volumeProbe = ProbeFixedVolumes;
        }

        clock = () => DateTimeOffset.UtcNow;
        hotspotBudget = DefaultHotspotBudget;
    }

    internal StorageWatchService(
        string statePath,
        Func<IReadOnlyList<VolumeProbe>> volumeProbe,
        Func<DateTimeOffset> clock,
        TimeSpan? hotspotBudget = null)
    {
        this.statePath = Path.GetFullPath(statePath);
        this.volumeProbe = volumeProbe;
        this.clock = clock;
        this.hotspotBudget = hotspotBudget ?? DefaultHotspotBudget;
        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public Task<StorageVolumeAudit> AuditAsync() => Task.Run(() =>
    {
        var volumes = volumeProbe()
            .Select(ToStatus)
            .OrderBy(volume => volume.FreePercent)
            .ToArray();
        return new StorageVolumeAudit(
            clock(),
            volumes,
            volumes.Count(volume =>
                volume.Status is not "OK"));
    });

    public Task<StorageWatchReport> WatchAsync() => Task.Run(() =>
    {
        var now = clock();
        var probes = volumeProbe();
        var state = LoadState();
        var previous = state?.Captures
            .OrderBy(capture => capture.CapturedAt)
            .LastOrDefault();

        var changes = probes
            .Select(probe =>
            {
                var status = ToStatus(probe);
                var old = previous?.Volumes.FirstOrDefault(
                    volume => string.Equals(
                        volume.Name,
                        probe.Name,
                        StringComparison.OrdinalIgnoreCase));
                long? freeDelta = old is null
                    ? null
                    : probe.FreeBytes - old.FreeBytes;
                long? growth = freeDelta is null
                    ? null
                    : Math.Max(0, -freeDelta.Value);
                var trend = ClassifyTrend(
                    status.FreePercent,
                    growth);
                return new StorageVolumeChange(
                    status,
                    freeDelta,
                    growth,
                    trend);
            })
            .OrderBy(change => change.Volume.FreePercent)
            .ToArray();

        var current = new WatchCapture(
            now,
            probes.Select(probe => new WatchVolume(
                probe.Name,
                probe.TotalBytes,
                probe.FreeBytes)).ToArray());

        var captures = state?.Captures.ToList() ?? [];
        captures.Add(current);
        if (captures.Count > MaxHistory)
            captures = captures[^MaxHistory..];
        PersistState(new WatchState(captures));

        return new StorageWatchReport(
            now,
            previous?.CapturedAt,
            previous is null,
            changes,
            changes.Count(change =>
                change.TrendStatus is not "OK"));
    });

    public Task<StorageHotspotReport> ScanLowSpaceHotspotsAsync() =>
        Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            var lowVolumes = volumeProbe()
                .Select(probe => new
                {
                    Probe = probe,
                    Status = ToStatus(probe)
                })
                .Where(item =>
                    item.Status.FreePercent <= WatchFreePercent)
                .OrderBy(item => item.Status.FreePercent)
                .ToArray();

            var results = new List<StorageHotspotVolume>();
            foreach (var item in lowVolumes)
            {
                if (watch.Elapsed >= hotspotBudget)
                    break;

                results.Add(ScanVolume(item.Probe, watch));
            }

            watch.Stop();
            return new StorageHotspotReport(
                clock(),
                hotspotBudget.TotalSeconds,
                watch.ElapsedMilliseconds,
                results,
                lowVolumes.Length > results.Count ||
                results.Any(volume => !volume.Complete));
        });

    private StorageHotspotVolume ScanVolume(
        VolumeProbe probe,
        Stopwatch globalWatch)
    {
        var started = globalWatch.ElapsedMilliseconds;
        var items = new List<StorageHotspotItem>();
        var complete = true;

        try
        {
            var directories = Directory
                .EnumerateDirectories(probe.RootPath)
                .Take(MaxTopLevelFolders)
                .ToArray();

            foreach (var directory in directories)
            {
                if (globalWatch.Elapsed >= hotspotBudget)
                {
                    complete = false;
                    break;
                }

                var folderWatch = Stopwatch.StartNew();
                var measured = CountFolderBounded(
                    directory,
                    globalWatch,
                    folderWatch);
                items.Add(new StorageHotspotItem(
                    directory,
                    measured.Bytes,
                    measured.Complete,
                    measured.FilesMeasured));
                if (!measured.Complete)
                    complete = false;
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            DirectoryNotFoundException or
            System.Security.SecurityException)
        {
            complete = false;
        }

        return new StorageHotspotVolume(
            probe.Name,
            complete,
            globalWatch.ElapsedMilliseconds - started,
            items.OrderByDescending(item => item.MeasuredBytes)
                .Take(12)
                .ToArray());
    }

    private (long Bytes, int FilesMeasured, bool Complete) CountFolderBounded(
        string root,
        Stopwatch globalWatch,
        Stopwatch folderWatch)
    {
        long bytes = 0;
        var files = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            if (globalWatch.Elapsed >= hotspotBudget ||
                folderWatch.Elapsed >= PerFolderBudget)
            {
                return (bytes, files, false);
            }

            var current = pending.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (globalWatch.Elapsed >= hotspotBudget ||
                        folderWatch.Elapsed >= PerFolderBudget)
                    {
                        return (bytes, files, false);
                    }

                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                        }
                        else
                        {
                            var length = new FileInfo(entry).Length;
                            bytes = bytes > long.MaxValue - length
                                ? long.MaxValue
                                : bytes + length;
                            files++;
                        }
                    }
                    catch (Exception ex) when (ex is
                        IOException or
                        UnauthorizedAccessException or
                        FileNotFoundException or
                        DirectoryNotFoundException or
                        System.Security.SecurityException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is
                IOException or
                UnauthorizedAccessException or
                DirectoryNotFoundException or
                System.Security.SecurityException)
            {
            }
        }

        return (bytes, files, true);
    }

    private static StorageVolumeStatus ToStatus(VolumeProbe probe)
    {
        var total = Math.Max(0, probe.TotalBytes);
        var free = Math.Clamp(probe.FreeBytes, 0, total);
        var freePercent = total == 0
            ? 0
            : free * 100d / total;

        var status = freePercent <= CriticalFreePercent
            ? "CRITICAL"
            : freePercent <= LowFreePercent
                ? "LOW"
                : freePercent <= WatchFreePercent
                    ? "WATCH"
                    : "OK";

        return new StorageVolumeStatus(
            probe.Name,
            probe.Label,
            probe.FileSystem,
            total,
            free,
            Math.Round(freePercent, 1),
            status);
    }

    private static string ClassifyTrend(
        double freePercent,
        long? growthBytes)
    {
        if (freePercent <= CriticalFreePercent)
            return "CRITICAL_FREE_SPACE";
        if (freePercent <= LowFreePercent)
            return "LOW_FREE_SPACE";
        if (growthBytes is >= GrowthAlertBytes)
            return "GROWTH_ALERT";
        if (freePercent <= WatchFreePercent)
            return "WATCH";
        return "OK";
    }

    private static IReadOnlyList<VolumeProbe> ProbeFixedVolumes()
    {
        var result = new List<VolumeProbe>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady ||
                    drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                result.Add(new VolumeProbe(
                    drive.Name,
                    drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.RootDirectory.FullName,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is
                IOException or
                UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private WatchState? LoadState()
    {
        return TryLoadState(statePath) ??
            TryLoadState(statePath + ".bak");
    }

    private static WatchState? TryLoadState(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<WatchState>(
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

    private void PersistState(WatchState state)
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
}

public sealed record StorageVolumeStatus(
    string Name,
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    double FreePercent,
    string Status);

public sealed record StorageVolumeAudit(
    DateTimeOffset CapturedAt,
    IReadOnlyList<StorageVolumeStatus> Volumes,
    int WarningCount);

public sealed record StorageVolumeChange(
    StorageVolumeStatus Volume,
    long? FreeBytesDelta,
    long? GrowthBytes,
    string TrendStatus);

public sealed record StorageWatchReport(
    DateTimeOffset CapturedAt,
    DateTimeOffset? PreviousCapturedAt,
    bool BaselineCreated,
    IReadOnlyList<StorageVolumeChange> Volumes,
    int AlertCount);

public sealed record StorageHotspotItem(
    string Path,
    long MeasuredBytes,
    bool Complete,
    int FilesMeasured);

public sealed record StorageHotspotVolume(
    string Drive,
    bool Complete,
    long ElapsedMilliseconds,
    IReadOnlyList<StorageHotspotItem> Items);

public sealed record StorageHotspotReport(
    DateTimeOffset CapturedAt,
    double BudgetSeconds,
    long ElapsedMilliseconds,
    IReadOnlyList<StorageHotspotVolume> Volumes,
    bool Partial);
