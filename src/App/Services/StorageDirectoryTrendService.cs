using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

/// <summary>
/// Conservative per-directory growth snapshots. An incomplete traversal is never
/// compared against a previous measurement or reported as a precise size delta.
/// </summary>
public sealed class StorageDirectoryTrendService
{
    private readonly string statePath;
    private readonly IReadOnlyList<string> roots;
    private readonly TimeSpan perRootBudget;
    private readonly int maxFilesPerRoot;

    private sealed record Snapshot(DateTimeOffset At,
        IReadOnlyList<DirectoryMeasurement> Measurements);
    private sealed record SavedState(IReadOnlyList<Snapshot> Captures);
    private sealed record DirectoryMeasurement(string Path, long Bytes, bool Complete);

    public StorageDirectoryTrendService(
        string? statePath = null, IReadOnlyList<string>? roots = null,
        TimeSpan? perRootBudget = null, int maxFilesPerRoot = 30000)
    {
        this.statePath = statePath is null
            ? Path.Combine(AppPaths.State, "storage-directories.json")
            : Path.GetFullPath(statePath);
        this.roots = roots ?? DefaultRoots();
        this.perRootBudget = perRootBudget ?? TimeSpan.FromSeconds(2);
        this.maxFilesPerRoot = Math.Clamp(maxFilesPerRoot, 1, 100000);
    }

    public static IReadOnlyList<string> DefaultRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(local, "Temp"),
            Path.Combine(local, "npm-cache"),
            Path.Combine(user, ".cache"),
            Path.Combine(local, "ms-playwright"),
            @"D:\Downloads",
            @"D:\DOCS\AI\PROYECTOS IA",
            @"D:\DOCS\Z-CV\bot_linkedin"
        };
        return roots.Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<StorageDirectoryTrendReport> ObserveAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Observe(cancellationToken), cancellationToken);

    private StorageDirectoryTrendReport Observe(CancellationToken token)
    {
        var previous = LoadState()?.Captures.LastOrDefault();
        var now = DateTimeOffset.UtcNow;
        var items = new List<StorageDirectoryTrendItem>();
        var next = new List<DirectoryMeasurement>();
        foreach (var path in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var (bytes, count, complete) = CountBounded(path, token);
            var previousItem = previous?.Measurements.FirstOrDefault(x =>
                string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            long? growth = previousItem is { Complete: true } && complete
                ? bytes - previousItem.Bytes
                : null;
            items.Add(new StorageDirectoryTrendItem(path, bytes, count, complete, growth));
            next.Add(new DirectoryMeasurement(path, bytes, complete));
        }
        var history = LoadState()?.Captures.ToList() ?? [];
        history.Add(new Snapshot(now, next));
        if (history.Count > 32) history = history[^32..];
        Persist(new SavedState(history));
        return new StorageDirectoryTrendReport(now, previous is null,
            items.Any(item => !item.Complete), items);
    }

    private (long Bytes, int Count, bool Complete) CountBounded(string root,
        CancellationToken token)
    {
        long bytes = 0;
        var count = 0;
        var partial = false;
        var sw = Stopwatch.StartNew();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (count >= maxFilesPerRoot || sw.Elapsed >= perRootBudget)
            { partial = true; break; }
            var current = pending.Pop();
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                { partial = true; continue; }
                foreach (var path in Directory.EnumerateFileSystemEntries(current))
                {
                    token.ThrowIfCancellationRequested();
                    if (count >= maxFilesPerRoot || sw.Elapsed >= perRootBudget)
                    { partial = true; break; }
                    try
                    {
                        var attrs = File.GetAttributes(path);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attrs & FileAttributes.Directory) != 0)
                        { pending.Push(path); continue; }
                        if ((attrs & FileAttributes.System) != 0) continue;
                        bytes += new FileInfo(path).Length;
                        count++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or SecurityException or FileNotFoundException)
                    { partial = true; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or SecurityException or DirectoryNotFoundException)
            { partial = true; }
        }
        return (bytes, count, !partial);
    }

    private SavedState? LoadState()
    {
        foreach (var file in new[] { statePath, statePath + ".bak" })
        {
            try
            {
                if (File.Exists(file))
                    return JsonSerializer.Deserialize<SavedState>(
                        File.ReadAllText(file), HostBridge.JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException or SecurityException)
            { }
        }
        return null;
    }

    private void Persist(SavedState state)
    {
        var folder = Path.GetDirectoryName(statePath)
            ?? throw new IOException("Missing state folder.");
        Directory.CreateDirectory(folder);
        var temp = statePath + ".tmp";
        var backup = statePath + ".bak";
        var data = JsonSerializer.SerializeToUtf8Bytes(state, HostBridge.JsonOptions);
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(data);
            file.Flush(flushToDisk: true);
        }
        if (File.Exists(statePath)) File.Copy(statePath, backup, overwrite: true);
        File.Move(temp, statePath, overwrite: true);
    }
}

public sealed record StorageDirectoryTrendItem(
    string Path, long MeasuredBytes, int FilesMeasured, bool Complete,
    long? GrowthBytes);

public sealed record StorageDirectoryTrendReport(
    DateTimeOffset CapturedAt, bool BaselineCreated, bool Partial,
    IReadOnlyList<StorageDirectoryTrendItem> Items);
