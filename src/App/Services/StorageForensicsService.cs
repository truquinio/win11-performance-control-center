using System.IO;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;

namespace Win11PerformanceControlCenter.App.Services;

/// <summary>
/// Bounded read-only inventory. Hash is computed only for same-size files; never follows junctions.
/// A partial report is deliberately not described as a complete disk-wide duplicate search.
/// </summary>
public sealed class StorageForensicsService
{
    private static readonly string[] DeniedSegments =
    [
        "$Recycle.Bin", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "Recovery", ".git", "node_modules",
        "WhatsApp", "Claude Extensions", "workspaceStorage", "Local Storage",
        ".venv", "WinGet"
    ];

    private readonly TimeSpan budget;
    private readonly int maxFiles;
    private readonly long maxHashFileBytes;

    public StorageForensicsService(
        TimeSpan? budget = null, int maxFiles = 4000,
        long maxHashFileBytes = 512L * 1024 * 1024)
    {
        this.budget = budget ?? TimeSpan.FromSeconds(7);
        this.maxFiles = Math.Clamp(maxFiles, 1, 20000);
        this.maxHashFileBytes = Math.Max(1, maxHashFileBytes);
    }

    public Task<StorageForensicsReport> ScanAsync(
        IReadOnlyList<string> roots, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(roots, cancellationToken), cancellationToken);

    public static IReadOnlyList<string> DefaultRoots()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string>();
        // D:\Downloads is deliberately resolved as a real directory, not through
        // the user's Downloads junction to avoid counting the same bytes twice.
        var downloads = Path.Combine(user, "Downloads");
        try
        {
            var info = new DirectoryInfo(downloads);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = info.ResolveLinkTarget(true);
                if (target is not null) downloads = target.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
        roots.Add(downloads);
        roots.Add(Path.Combine(user, "Desktop"));
        roots.Add(Path.Combine(local, "Temp"));
        var dProjects = @"D:\DOCS\AI\PROYECTOS IA";
        if (Directory.Exists(dProjects)) roots.Add(dProjects);
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private StorageForensicsReport Scan(IReadOnlyList<string> roots, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var files = new Dictionary<long, List<string>>();
        var visitedRoots = new List<string>();
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var skippedLinks = 0;
        var errors = 0;
        var partial = false;
        foreach (var requested in roots)
        {
            token.ThrowIfCancellationRequested();
            if (sw.Elapsed >= budget || scanned >= maxFiles) { partial = true; break; }
            string path;
            try { path = Path.GetFullPath(requested); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            { partial = true; errors++; continue; }
            if (IsDenied(path)) { partial = true; continue; }
            if (!Directory.Exists(path)) { partial = true; errors++; continue; }
            var pending = new Stack<string>();
            pending.Push(path);
            visitedRoots.Add(path);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (sw.Elapsed >= budget || scanned >= maxFiles)
                { partial = true; break; }
                var current = pending.Pop();
                if (!visitedPaths.Add(current)) continue;
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    { skippedLinks++; continue; }
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        token.ThrowIfCancellationRequested();
                        if (sw.Elapsed >= budget || scanned >= maxFiles)
                        { partial = true; break; }
                        if (IsDenied(entry)) continue;
                        try
                        {
                            var attrs = File.GetAttributes(entry);
                            if ((attrs & FileAttributes.ReparsePoint) != 0)
                            { skippedLinks++; continue; }
                            if ((attrs & FileAttributes.Directory) != 0)
                            { pending.Push(entry); continue; }
                            if ((attrs & FileAttributes.System) != 0) continue;
                            var size = new FileInfo(entry).Length;
                            scanned++;
                            if (size > 0 && size <= maxHashFileBytes)
                            {
                                if (!files.TryGetValue(size, out var paths))
                                { paths = []; files[size] = paths; }
                                paths.Add(entry);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                            or SecurityException or FileNotFoundException)
                        { errors++; partial = true; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or SecurityException or DirectoryNotFoundException)
                { errors++; partial = true; }
            }
        }

        var duplicateGroups = new List<StorageDuplicateGroup>();
        foreach (var group in files.OrderByDescending(group => group.Key).Where(g => g.Value.Count > 1))
        {
            if (sw.Elapsed >= budget) { partial = true; break; }
            var hashes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var file in group.Value)
            {
                token.ThrowIfCancellationRequested();
                if (sw.Elapsed >= budget) { partial = true; break; }
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                    if (stream.Length != group.Key) { partial = true; continue; }
                    var hash = Convert.ToHexString(SHA256.HashData(stream));
                    if (!hashes.TryGetValue(hash, out var groupPaths))
                    { groupPaths = []; hashes[hash] = groupPaths; }
                    groupPaths.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or SecurityException)
                { errors++; partial = true; }
            }
            duplicateGroups.AddRange(hashes.Where(h => h.Value.Count > 1)
                .Select(h => new StorageDuplicateGroup(group.Key, h.Key,
                    h.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    (h.Value.Count - 1) * group.Key)));
        }
        sw.Stop();
        return new StorageForensicsReport(visitedRoots, scanned, skippedLinks, errors,
            partial, sw.ElapsedMilliseconds,
            duplicateGroups.OrderByDescending(g => g.ReclaimableLogicalBytes).ToArray());
    }

    private static bool IsDenied(string path)
    {
        try
        {
            return Path.GetFullPath(path).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => DeniedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { return true; }
    }
}

public sealed record StorageDuplicateGroup(
    long FileBytes, string Sha256, IReadOnlyList<string> Paths, long ReclaimableLogicalBytes);

public sealed record StorageForensicsReport(
    IReadOnlyList<string> ScannedRoots, int ScannedFiles, int SkippedLinks,
    int Errors, bool Partial, long ElapsedMilliseconds,
    IReadOnlyList<StorageDuplicateGroup> Duplicates);
