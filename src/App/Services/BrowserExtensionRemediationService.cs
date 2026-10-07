using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class BrowserExtensionRemediationService
{
    private static readonly Regex ExtensionIdPattern =
        new("^[a-p]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string edgeUserDataRoot;
    private readonly string quarantineRoot;
    private readonly Func<bool> edgeRunning;
    private readonly Func<DateTimeOffset> clock;

    public BrowserExtensionRemediationService(
        string? edgeUserDataRoot = null,
        string? quarantineRoot = null,
        Func<bool>? edgeRunning = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.edgeUserDataRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(edgeUserDataRoot)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft",
                    "Edge",
                    "User Data")
                : edgeUserDataRoot);

        this.quarantineRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(quarantineRoot)
                ? AppPaths.EdgeExtensionQuarantine
                : quarantineRoot);

        this.edgeRunning = edgeRunning ?? IsEdgeRunning;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public BrowserExtensionOrphanPreview Preview()
    {
        var health = new BrowserExtensionHealthService(edgeUserDataRoot).AnalyzeEdge();
        var candidates = health.Items
            .Where(item => item.Status == "DATA_WITHOUT_INSTALLATION")
            .Select(item => new BrowserExtensionOrphanCandidate(
                item.Profile,
                item.ExtensionId,
                item.DataBytes))
            .OrderByDescending(item => item.DataBytes)
            .ThenBy(item => item.ExtensionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BrowserExtensionOrphanPreview(
            candidates.Length,
            candidates.Sum(item => item.DataBytes),
            edgeRunning(),
            FindLatestBatch() is not null,
            candidates);
    }

    public Task<BrowserExtensionQuarantineResult> QuarantineAsync() =>
        Task.Run(() =>
        {
            var preview = Preview();
            if (preview.EdgeRunning)
            {
                return new BrowserExtensionQuarantineResult(
                    false,
                    "EDGE_RUNNING",
                    0,
                    0,
                    0,
                    null,
                    ["Cierra Microsoft Edge antes de mover residuos a cuarentena."]);
            }

            if (preview.CandidateCount == 0)
            {
                return new BrowserExtensionQuarantineResult(
                    true,
                    "NOTHING_TO_DO",
                    0,
                    0,
                    0,
                    null,
                    []);
            }

            var batchId =
                clock().UtcDateTime.ToString("yyyyMMddTHHmmssfffZ") +
                "-" + Guid.NewGuid().ToString("N")[..8];
            var batchRoot = Path.Combine(quarantineRoot, batchId);
            Directory.CreateDirectory(batchRoot);

            var moved = new List<BrowserExtensionQuarantineEntry>();
            var skipped = new List<string>();

            foreach (var candidate in preview.Candidates)
            {
                if (!TryBuildSourcePath(candidate, out var source) ||
                    !Directory.Exists(source))
                {
                    skipped.Add(candidate.ExtensionId + ": origen ausente o no permitido.");
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(source);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped.Add(candidate.ExtensionId + ": punto de reanálisis omitido.");
                        continue;
                    }

                    var destination = Path.Combine(
                        batchRoot,
                        candidate.Profile,
                        candidate.ExtensionId);
                    if (!IsUnderRoot(destination, batchRoot))
                    {
                        skipped.Add(candidate.ExtensionId + ": destino no permitido.");
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    Directory.Move(source, destination);
                    moved.Add(new BrowserExtensionQuarantineEntry(
                        candidate.Profile,
                        candidate.ExtensionId,
                        candidate.DataBytes,
                        source,
                        destination));
                    PersistManifest(batchRoot, batchId, moved);
                }
                catch (Exception ex) when (ex is
                    IOException or
                    UnauthorizedAccessException or
                    System.Security.SecurityException)
                {
                    skipped.Add(candidate.ExtensionId + ": " + ex.GetType().Name);
                }
            }

            if (moved.Count == 0)
            {
                try
                {
                    Directory.Delete(batchRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            var movedBytes = moved.Sum(item => item.DataBytes);
            return new BrowserExtensionQuarantineResult(
                skipped.Count == 0,
                moved.Count > 0 ? "QUARANTINED" : "NO_CHANGES",
                moved.Count,
                movedBytes,
                skipped.Count,
                moved.Count > 0 ? batchId : null,
                skipped);
        });

    public Task<BrowserExtensionRestoreResult> RestoreLatestAsync() =>
        Task.Run(() =>
        {
            if (edgeRunning())
            {
                return new BrowserExtensionRestoreResult(
                    false,
                    "EDGE_RUNNING",
                    0,
                    0,
                    null,
                    ["Cierra Microsoft Edge antes de restaurar una cuarentena."]);
            }

            var batch = FindLatestBatch();
            if (batch is null)
            {
                return new BrowserExtensionRestoreResult(
                    true,
                    "NO_QUARANTINE",
                    0,
                    0,
                    null,
                    []);
            }

            var manifest = LoadManifest(batch.Value.Path);
            if (manifest is null)
            {
                return new BrowserExtensionRestoreResult(
                    false,
                    "MANIFEST_MISSING",
                    0,
                    0,
                    batch.Value.Id,
                    ["La última cuarentena no tiene un manifest válido."]);
            }

            var restored = 0;
            long restoredBytes = 0;
            var skipped = new List<string>();

            foreach (var entry in manifest.Entries)
            {
                if (!IsUnderRoot(entry.OriginalPath, edgeUserDataRoot) ||
                    !IsUnderRoot(entry.QuarantinePath, batch.Value.Path) ||
                    !ExtensionIdPattern.IsMatch(entry.ExtensionId))
                {
                    skipped.Add(entry.ExtensionId + ": rutas no permitidas.");
                    continue;
                }

                if (!Directory.Exists(entry.QuarantinePath))
                    continue;

                if (Directory.Exists(entry.OriginalPath))
                {
                    skipped.Add(entry.ExtensionId + ": el destino original ya existe.");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(entry.OriginalPath)!);
                    Directory.Move(
                        entry.QuarantinePath,
                        entry.OriginalPath);
                    restored++;
                    restoredBytes += entry.DataBytes;
                }
                catch (Exception ex) when (ex is
                    IOException or
                    UnauthorizedAccessException or
                    System.Security.SecurityException)
                {
                    skipped.Add(entry.ExtensionId + ": " + ex.GetType().Name);
                }
            }

            if (skipped.Count == 0)
            {
                try
                {
                    Directory.Delete(batch.Value.Path, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return new BrowserExtensionRestoreResult(
                skipped.Count == 0,
                restored > 0 ? "RESTORED" : "NO_CHANGES",
                restored,
                restoredBytes,
                batch.Value.Id,
                skipped);
        });

    private bool TryBuildSourcePath(
        BrowserExtensionOrphanCandidate candidate,
        out string source)
    {
        source = string.Empty;
        if (!ExtensionIdPattern.IsMatch(candidate.ExtensionId) ||
            string.IsNullOrWhiteSpace(candidate.Profile) ||
            candidate.Profile.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return false;
        }

        source = Path.GetFullPath(Path.Combine(
            edgeUserDataRoot,
            candidate.Profile,
            "Local Extension Settings",
            candidate.ExtensionId));

        return IsUnderRoot(source, edgeUserDataRoot);
    }

    private (string Id, string Path)? FindLatestBatch()
    {
        try
        {
            if (!Directory.Exists(quarantineRoot))
                return null;

            var path = Directory.EnumerateDirectories(quarantineRoot)
                .OrderByDescending(
                    value => Path.GetFileName(value),
                    StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return path is null
                ? null
                : (Path.GetFileName(path), path);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return null;
        }
    }

    private static BrowserExtensionQuarantineManifest? LoadManifest(string batchRoot)
    {
        try
        {
            var path = Path.Combine(batchRoot, "manifest.json");
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<BrowserExtensionQuarantineManifest>(
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

    private static void PersistManifest(
        string batchRoot,
        string batchId,
        IReadOnlyList<BrowserExtensionQuarantineEntry> entries)
    {
        var payload = new BrowserExtensionQuarantineManifest(
            batchId,
            DateTimeOffset.UtcNow,
            entries.ToArray());

        var path = Path.Combine(batchRoot, "manifest.json");
        var temp = path + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(payload, HostBridge.JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static bool IsEdgeRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("msedge");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        catch
        {
            return true;
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

            return string.Equals(
                    normalizedPath,
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is
            IOException or
            ArgumentException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private sealed record BrowserExtensionQuarantineManifest(
        string BatchId,
        DateTimeOffset CreatedAt,
        IReadOnlyList<BrowserExtensionQuarantineEntry> Entries);
}

public sealed record BrowserExtensionOrphanCandidate(
    string Profile,
    string ExtensionId,
    long DataBytes);

public sealed record BrowserExtensionOrphanPreview(
    int CandidateCount,
    long TotalBytes,
    bool EdgeRunning,
    bool RestoreAvailable,
    IReadOnlyList<BrowserExtensionOrphanCandidate> Candidates);

public sealed record BrowserExtensionQuarantineEntry(
    string Profile,
    string ExtensionId,
    long DataBytes,
    string OriginalPath,
    string QuarantinePath);

public sealed record BrowserExtensionQuarantineResult(
    bool Success,
    string Status,
    int MovedCount,
    long MovedBytes,
    int SkippedCount,
    string? BatchId,
    IReadOnlyList<string> Skipped);

public sealed record BrowserExtensionRestoreResult(
    bool Success,
    string Status,
    int RestoredCount,
    long RestoredBytes,
    string? BatchId,
    IReadOnlyList<string> Skipped);
