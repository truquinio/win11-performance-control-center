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
    private static readonly TimeSpan AppDataRankBudget = TimeSpan.FromSeconds(8);
    private static readonly string[] ProtectedDirectoryNames =
    [
        "Claude Extensions",
        "WhatsApp",
        ".git",
        ".venv",
        "workspaceStorage",
        "Local Storage"
    ];
    private static readonly string[] ProtectedPathFragments =
    [
        @"\WinGet\Packages\",
        @"\AppData\Local\Programs\",
        @"\AppData\Local\Packages\"
    ];

    private readonly IReadOnlyList<CacheTarget> targets;
    private readonly Func<string?, bool> processRunning;
    private readonly Func<CacheTarget, string, bool> targetPolicy;
    private readonly string localAppDataRoot;

    internal sealed record CacheTarget(
        string Id, string Label, string Path, string Risk = "SAFE",
        string? ProcessName = null);

    public StorageAnalysisService()
        : this(
            Targets(),
            IsRunning,
            IsProductionApprovedTarget,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    internal StorageAnalysisService(
        IReadOnlyList<CacheTarget> targets,
        Func<string?, bool> processRunning,
        string evaluationRoot)
        : this(
            targets,
            processRunning,
            (target, path) =>
                IsUnderRoot(path, evaluationRoot) &&
                target.Id.StartsWith("eval.", StringComparison.Ordinal),
            evaluationRoot)
    {
    }

    private StorageAnalysisService(
        IReadOnlyList<CacheTarget> targets,
        Func<string?, bool> processRunning,
        Func<CacheTarget, string, bool> targetPolicy,
        string localAppDataRoot)
    {
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        this.processRunning = processRunning ?? throw new ArgumentNullException(nameof(processRunning));
        this.targetPolicy = targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy));
        this.localAppDataRoot = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? throw new ArgumentException("AppData root requerido.", nameof(localAppDataRoot))
            : Path.GetFullPath(localAppDataRoot);
    }

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
        foreach (var target in targets)
        {
            var path = SafeRoot(target.Path);
            if (path is null || !seen.Add(path))
                continue;
            if (!targetPolicy(target, path))
            {
                categories.Add(new StorageCategory(
                    target.Id,
                    target.Label,
                    0,
                    "POLICY_BLOCKED"));
                continue;
            }
            var running = processRunning(target.ProcessName);
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
        foreach (var target in targets)
        {
            // CAUTION targets (e.g. Windows Temp) are never auto-deleted.
            if (target.Risk != "SAFE")
                continue;
            var path = SafeRoot(target.Path);
            if (path is null || !seen.Add(path))
                continue;
            if (!targetPolicy(target, path))
            {
                skipped.Add(target.Label + ": bloqueado por política");
                continue;
            }
            if (IsProtectedPath(path))
            {
                skipped.Add(target.Label + ": ruta protegida");
                continue;
            }
            if (processRunning(target.ProcessName))
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
    public Task<AppDataRankResult> RankLocalAsync(int limit = 25) => Task.Run(() =>
    {
        var result = new List<AppDataFolderUsage>();
        var stopwatch = Stopwatch.StartNew();
        var complete = true;
        var discoveredFolders = 0;

        try
        {
            var folders = Directory.EnumerateDirectories(localAppDataRoot).ToArray();
            discoveredFolders = folders.Length;

            foreach (var folder in folders)
            {
                if (stopwatch.Elapsed >= AppDataRankBudget)
                {
                    complete = false;
                    break;
                }

                var full = SafeRoot(folder);
                if (full is null)
                    continue;

                var counted = CountAllBytes(full, stopwatch);
                result.Add(new AppDataFolderUsage(
                    Path.GetFileName(full),
                    counted.bytes,
                    counted.complete));

                if (!counted.complete)
                {
                    complete = false;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            complete = false;
        }

        stopwatch.Stop();
        return new AppDataRankResult(
            result.OrderByDescending(f => f.Bytes)
                .Take(Math.Clamp(limit, 1, 50))
                .ToList(),
            complete,
            discoveredFolders,
            stopwatch.ElapsedMilliseconds);
    });

    private static (long bytes, bool complete) CountAllBytes(
        string root,
        Stopwatch stopwatch)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            if (stopwatch.Elapsed >= AppDataRankBudget)
                return (total, false);

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if (stopwatch.Elapsed >= AppDataRankBudget)
                        return (total, false);

                    try
                    {
                        var attrs = File.GetAttributes(entry);
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                            continue;

                        if ((attrs & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                        }
                        else
                        {
                            var size = new FileInfo(entry).Length;
                            total = total > long.MaxValue - size
                                ? long.MaxValue
                                : total + size;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        SecurityException or FileNotFoundException or DirectoryNotFoundException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                SecurityException or DirectoryNotFoundException)
            {
            }
        }

        return (total, true);
    }

    internal static bool IsProtectedPath(string path)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or
            NotSupportedException or SecurityException)
        {
            return true;
        }

        var segments = normalized.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
            ProtectedDirectoryNames.Contains(
                segment,
                StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        var wrapped = Path.DirectorySeparatorChar +
            normalized.Trim(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return ProtectedPathFragments.Any(fragment =>
            wrapped.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsProductionApprovedTarget(
        CacheTarget target,
        string normalizedPath)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var code = Path.Combine(roaming, "Code");
        var windir = Environment.GetEnvironmentVariable("WINDIR");

        var expected = target.Id switch
        {
            "temp.user" => Path.GetTempPath(),
            "cache.npm" => Path.Combine(local, "npm-cache"),
            "cache.pip" => Path.Combine(local, "pip", "Cache"),
            "cache.uv" => Path.Combine(local, "uv", "cache"),
            "cache.nuget" => Path.Combine(local, "NuGet", "v3-cache"),
            "cache.electron" => Path.Combine(local, "electron", "Cache"),
            "cache.d3d" => Path.Combine(local, "D3DSCache"),
            "cache.squirrel" => Path.Combine(local, "SquirrelTemp"),
            "vscode.vsix" => Path.Combine(code, "CachedExtensionVSIXs"),
            "vscode.cache" => Path.Combine(code, "Cache"),
            "vscode.cacheddata" => Path.Combine(code, "CachedData"),
            "vscode.codecache" => Path.Combine(code, "Code Cache"),
            "vscode.gpucache" => Path.Combine(code, "GPUCache"),
            "vscode.logs" => Path.Combine(code, "logs"),
            "spotify.browsercache" => Path.Combine(local, "Spotify", "Browser", "Cache"),
            "spotify.codecache" => Path.Combine(local, "Spotify", "Browser", "Code Cache"),
            "spotify.gpucache" => Path.Combine(local, "Spotify", "Browser", "GPUCache"),
            "spotify.shader" => Path.Combine(local, "Spotify", "GrShaderCache"),
            "edge.cache" => Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache"),
            "edge.codecache" => Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Code Cache"),
            "edge.gpucache" => Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "GPUCache"),
            "temp.windows" when !string.IsNullOrWhiteSpace(windir) =>
                Path.Combine(windir, "Temp"),
            _ => null
        };

        return expected is not null && SamePath(normalizedPath, expected);
    }

    private static bool SamePath(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or
            NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar);
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(
                normalizedPath,
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or
            NotSupportedException or SecurityException)
        {
            return false;
        }
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
                        if (IsProtectedPath(entry))
                            continue;

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

public sealed record AppDataFolderUsage(
    string Name,
    long Bytes,
    bool Complete);

public sealed record AppDataRankResult(
    IReadOnlyList<AppDataFolderUsage> Folders,
    bool Complete,
    int DiscoveredFolders,
    long ElapsedMilliseconds);
