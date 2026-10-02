using System.IO;
using System.Security;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class StorageAnalysisService
{
    private static readonly TimeSpan MinimumCandidateAge = TimeSpan.FromDays(7);
    public Task<StorageEstimate> AnalyzeSafeAsync() =>
        Task.Run(() =>
        {
            var categories = new List<StorageCategory>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddDirectory(
                categories,
                seen,
                "temp.user",
                "TEMP del usuario",
                Path.GetTempPath(),
                "SAFE");

            var windows = Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrWhiteSpace(windows))
            {
                AddDirectory(
                    categories,
                    seen,
                    "temp.windows",
                    "TEMP de Windows",
                    Path.Combine(windows, "Temp"),
                    "CAUTION");
            }

            return new StorageEstimate(
                categories.Sum(category => category.Bytes),
                categories);
        });

    private static void AddDirectory(
        ICollection<StorageCategory> categories,
        ISet<string> seen,
        string id,
        string label,
        string path,
        string risk)
    {
        try
        {
            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar);

            if (!Directory.Exists(fullPath) || !seen.Add(fullPath))
                return;

            categories.Add(new StorageCategory(
                id,
                label,
                CalculateCandidateBytes(fullPath),
                risk));
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            NotSupportedException)
        {
            // Unknown or inaccessible folders are never treated as reclaimable.
        }
    }

    private static long CalculateCandidateBytes(string root)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            AddFiles(current, ref total);
            AddDirectories(current, pending);
        }

        return Math.Max(0, total);
    }

    private static void AddFiles(
        string current,
        ref long total)
    {
        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(current);
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            SecurityException or
            DirectoryNotFoundException)
        {
            return;
        }

        try
        {
            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    info.Refresh();
                    if (!info.Exists ||
                        (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        (info.Attributes & FileAttributes.System) != 0)
                    {
                        continue;
                    }

                    var age = DateTime.UtcNow - info.LastWriteTimeUtc;
                    if (age < MinimumCandidateAge)
                        continue;

                    total = checked(total + info.Length);
                }
                catch (OverflowException)
                {
                    total = long.MaxValue;
                    return;
                }
                catch (Exception ex) when (
                    ex is IOException or
                    UnauthorizedAccessException or
                    SecurityException or
                    FileNotFoundException)
                {
                    // File disappeared or became inaccessible during enumeration.
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            SecurityException or
            DirectoryNotFoundException)
        {
            // Directory changed while it was being enumerated.
        }
    }

    private static void AddDirectories(
        string current,
        Stack<string> pending)
    {
        IEnumerable<string> directories;

        try
        {
            directories = Directory.EnumerateDirectories(current);
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            SecurityException or
            DirectoryNotFoundException)
        {
            return;
        }

        try
        {
            foreach (var directory in directories)
            {
                try
                {
                    var attributes = File.GetAttributes(directory);
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Push(directory);
                }
                catch (Exception ex) when (
                    ex is IOException or
                    UnauthorizedAccessException or
                    SecurityException or
                    FileNotFoundException or
                    DirectoryNotFoundException)
                {
                    // Skip unstable/inaccessible directory entries.
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            SecurityException or
            DirectoryNotFoundException)
        {
            // Directory changed while it was being enumerated.
        }
    }
}
