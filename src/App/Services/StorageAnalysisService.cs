using System.Diagnostics;
using System.IO;
using System.Security;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

// Explicit allowlist: never recurse from AppData, Packages, Programs or a browser profile root.
// No deleting extension installations, sessions, workspaces, backups or personal documents.
public sealed class StorageAnalysisService
{
    private static readonly TimeSpan MinimumCandidateAge = TimeSpan.FromDays(7);

    private sealed record CacheTarget(
        string Id, string Label, string Path, string Risk = "SAFE",
        string? ProcessName = null);

    private static IReadOnlyList<CacheTarget> Targets()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var code = Path.Combine(roaming, "Code");
        var targets = new List<CacheTarget>
        {
            new("temp.user", "Temporales antiguos del usuario", Path.GetTempPath()),
            new("cache.npm", "npm cache", Path.Combine(local, "npm-cache"), ProcessName: "node"),
            new("cache.pip", "pip cache", Path.Combine(local, "pip", "Cache")),
            new("cache.uv", "uv cache", Path.Combine(local, "uv", "cache"), ProcessName: "uv"),
            new("cache.nuget", "NuGet cache", Path.Combine(local, "NuGet", "v3-cache"), ProcessName: "dotnet"),
            new("cache.electron", "Electron cache", Path.Combine(local, "electron", "Cache")),
            new("cache.d3d", "Caché gráfica D3D", Path.Combine(local, "D3DSCache")),
            new("cache.squirrel", "Temporales Squirrel", Path.Combine(local, "SquirrelTemp")),
            new("vscode.vsix", "VS Code: paquetes VSIX descargados", Path.Combine(code, "CachedExtensionVSIXs"), ProcessName: "Code"),
            new("vscode.cache", "VS Code: Cache", Path.Combine(code, "Cache"), ProcessName: "Code"),
            new("vscode.cacheddata", "VS Code: CachedData", Path.Combine(code, "CachedData"), ProcessName: "Code"),
            new("vscode.codecache", "VS Code: Code Cache", Path.Combine(code, "Code Cache"), ProcessName: "Code"),
            new("vscode.gpucache", "VS Code: GPUCache", Path.Combine(code, "GPUCache"), ProcessName: "Code"),
            new("vscode.logs", "VS Code: logs antiguos", Path.Combine(code, "logs"), ProcessName: "Code"),
            new("spotify.browsercache", "Spotify: caché web", Path.Combine(local, "Spotify", "Browser", "Cache"), ProcessName: "Spotify"),
            new("spotify.codecache", "Spotify: caché de código", Path.Combine(local, "Spotify", "Browser", "Code Cache"), ProcessName: "Spotify"),
            new("spotify.gpucache", "Spotify: caché gráfica", Path.Combine(local, "Spotify", "Browser", "GPUCache"), ProcessName: "Spotify"),
            new("spotify.shader", "Spotify: shader cache", Path.Combine(local, "Spotify", "GrShaderCache"), ProcessName: "Spotify"),
            new("edge.cache", "Edge: caché web", Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache"), ProcessName: "msedge"),
            new("edge.codecache", "Edge: caché de código", Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Code Cache"), ProcessName: "msedge"),
            new("edge.gpucache", "Edge: caché gráfica", Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "GPUCache"), ProcessName: "msedge")
        };
        var windir = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrWhiteSpace(windir))
        {
            targets.Add(new CacheTarget("temp.windows", "Windows Temp (solo diagnóstico)",
                Path.Combine(windir, "Temp"), "CAUTION"));
        }
        return targets;
    }

    public Task<StorageEstimate> AnalyzeSafeAsync() => Task.Run(() =>
    {
        var categories = new List<StorageCategory>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in Targets())
        {
            var path = SafeRoot(target.Path);
            if (path is null || !seen.Add(path)) continue;
            var running = IsRunning(target.ProcessName);
            categories.Add(new StorageCategory(target.Id, target.Label,
                running ? 0 : Traverse(path, delete: false).bytes,
                running ? "EN_USO" : target.Risk));
        }
        return new StorageEstimate(
            categories.Where(item => item.Risk == "SAFE").Sum(item => item.Bytes),
            categories);
    });

    public Task<StorageCleanupSummary> CleanupSafeAsync() => Task.Run(() =>
    {
        long deletedBytes = 0;
        int deletedFiles = 0;
        int failedFiles = 0;
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in Targets())
        {
            // CAUTION targets (e.g. Windows Temp) are never auto-deleted.
            if (target.Risk != "SAFE") continue;
            var path = SafeRoot(target.Path);
            if (path is null || !seen.Add(path)) continue;
            if (IsRunning(target.ProcessName))
            {
                skipped.Add(target.Label + ": aplicación en ejecución");
                continue;
            }
            var result = Traverse(path, delete: true);
            deletedBytes += result.bytes;
            deletedFiles += result.files;
            failedFiles += result.failed;
        }
        return new StorageCleanupSummary(deletedBytes, deletedFiles, failedFiles, skipped);
    });

    // Full AppData\\Local ranking: READ ONLY. Deliberately separate from the
    // cleanup allowlist; large program/runtime folders are never delete targets.
    public Task<IReadOnlyList<AppDataFolderUsage>> RankLocalAsync(int limit = 25) => Task.Run(() =>
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var result = new List<AppDataFolderUsage>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var full = SafeRoot(folder);
                if (full is null) continue;
                result.Add(new AppDataFolderUsage(Path.GetFileName(full),
                    CountAllBytes(full)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { /* Preserve partial results. */ }
        return (IReadOnlyList<AppDataFolderUsage>)result.OrderByDescending(f => f.Bytes)
            .Take(Math.Clamp(limit, 1, 50)).ToList();
    });

    private static long CountAllBytes(string root)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    try
                    {
                        var attrs = File.GetAttributes(entry);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attrs & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                        }
                        else
                        {
                            var size = new FileInfo(entry).Length;
                            total = total > long.MaxValue - size ? long.MaxValue : total + size;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        SecurityException or FileNotFoundException or DirectoryNotFoundException)
                    { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                SecurityException or DirectoryNotFoundException)
            { }
        }
        return total;
    }

    private static bool IsRunning(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            var found = Process.GetProcessesByName(name);
            try { return found.Length > 0; }
            finally { foreach (var process in found) process.Dispose(); }
        }
        catch { return true; } // Fail closed.
    }

    private static string? SafeRoot(string path)
    {
        try
        {
            var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(normalized)) return null;
            if ((File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
                return null;
            return normalized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or SecurityException)
        { return null; }
    }

    private static (long bytes, int files, int failed) Traverse(string root, bool delete)
    {
        long bytes = 0;
        int files = 0;
        int failed = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    try
                    {
                        var attr = File.GetAttributes(entry);
                        if ((attr & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0)
                            continue;
                        if ((attr & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                            continue;
                        }
                        var info = new FileInfo(entry);
                        info.Refresh();
                        if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc < MinimumCandidateAge)
                            continue;
                        if (delete)
                        {
                            try { File.Delete(entry); }
                            catch (IOException) { failed++; continue; }
                            catch (UnauthorizedAccessException) { failed++; continue; }
                        }
                        bytes = bytes > long.MaxValue - info.Length
                            ? long.MaxValue : bytes + info.Length;
                        files++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        SecurityException or FileNotFoundException or DirectoryNotFoundException)
                    { if (delete) failed++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                SecurityException or DirectoryNotFoundException)
            { if (delete) failed++; }
        }
        return (bytes, files, failed);
    }
}

public sealed record StorageCleanupSummary(
    long DeletedBytes, int DeletedFiles, int FailedFiles,
    IReadOnlyList<string> SkippedCategories);

public sealed record AppDataFolderUsage(string Name, long Bytes);
