using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class PageFileTuningService
{
    private const string MemoryManagementPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private readonly string statePath;
    private readonly PageFileTuningBackend backend;

    public PageFileTuningService(
        string? statePath = null,
        PageFileTuningBackend? backend = null)
    {
        if (string.IsNullOrWhiteSpace(statePath))
            AppPaths.EnsureDirectories();
        this.statePath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.PageFileState
            : Path.GetFullPath(statePath);
        this.backend = backend ?? new(
            CaptureCurrent,
            SetAutomaticManaged,
            WritePagingFiles,
            IsFixedReadyDrive);
        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public Task<PageFileTuningResult> ApplyCappedProfileAsync() =>
        Task.Run(() =>
        {
            const string cEntry = @"C:\pagefile.sys 512 512";
            const string dEntry = @"D:\pagefile.sys 4096 8192";
            var desired = new[] { cEntry, dEntry };

            var existingSnapshot = LoadSnapshot(out var unreadableSnapshot);
            if (unreadableSnapshot)
                return new PageFileTuningResult(false, "SNAPSHOT_UNREADABLE", null, null, true, false);
            if (existingSnapshot is { PagingFiles: null })
                return new PageFileTuningResult(false, "SNAPSHOT_INCOMPLETE", null, null, true, false);

            var before = backend.CaptureCurrent();
            // Never change pagefile settings without a restorable registry baseline.
            if (before is null || before.PagingFiles is null)
            {
                return new PageFileTuningResult(
                    false,
                    "STATE_UNKNOWN",
                    null,
                    null,
                    existingSnapshot is not null,
                    false);
            }

            // A prior Restore may have restored the captured baseline but failed
            // to remove its recovery record. Do not treat that retained record as
            // permission for a new profile; require review before another WRITE.
            if (existingSnapshot is not null &&
                before.AutomaticManaged == existingSnapshot.AutomaticManaged &&
                PagingFilesEqual(before.PagingFiles, existingSnapshot.PagingFiles))
            {
                return new PageFileTuningResult(
                    false,
                    "SNAPSHOT_RETAINED_REVIEW_REQUIRED",
                    before.AutomaticManaged,
                    before.AutomaticManaged,
                    true,
                    false);
            }

            if (!backend.IsFixedReadyDrive("D"))
            {
                return new PageFileTuningResult(
                    false,
                    "D_DRIVE_UNAVAILABLE",
                    before.AutomaticManaged,
                    before.AutomaticManaged,
                    false,
                    false);
            }

            if (!before.AutomaticManaged &&
                PagingFilesEqual(before.PagingFiles, desired))
            {
                return new PageFileTuningResult(
                    true,
                    "ALREADY_CAPPED",
                    before.AutomaticManaged,
                    before.AutomaticManaged,
                    false,
                    false);
            }

            if (existingSnapshot is null)
            {
                try { PersistSnapshot(before); }
                catch (IOException)
                {
                    return new PageFileTuningResult(false, "SNAPSHOT_SAVE_FAILED", before.AutomaticManaged, before.AutomaticManaged, File.Exists(statePath), false);
                }
                catch (UnauthorizedAccessException)
                {
                    return new PageFileTuningResult(false, "SNAPSHOT_SAVE_FAILED", before.AutomaticManaged, before.AutomaticManaged, File.Exists(statePath), false);
                }
            }

            if (!backend.SetAutomaticManaged(false))
            {
                return new PageFileTuningResult(
                    false,
                    "DISABLE_AUTOMATIC_FAILED",
                    before.AutomaticManaged,
                    null,
                    true,
                    false);
            }

            if (!backend.WritePagingFiles(desired))
            {
                return new PageFileTuningResult(
                    false,
                    "PAGINGFILES_WRITE_FAILED",
                    before.AutomaticManaged,
                    false,
                    true,
                    false);
            }

            var after = backend.CaptureCurrent();
            var success =
                after is not null &&
                !after.AutomaticManaged &&
                PagingFilesEqual(after.PagingFiles, desired);

            return new PageFileTuningResult(
                success,
                success ? "CAPPED_4_8_GB" : "VERIFY_FAILED",
                before.AutomaticManaged,
                after?.AutomaticManaged,
                true,
                success);
        });

    public Task<PageFileTuningResult> RestoreAsync() =>
        Task.Run(() =>
        {
            var snapshot = LoadSnapshot(out var unreadableSnapshot);
            if (unreadableSnapshot)
                return new PageFileTuningResult(false, "SNAPSHOT_UNREADABLE", null, null, true, false);
            if (snapshot is null)
            {
                return new PageFileTuningResult(
                    true,
                    "NO_SNAPSHOT",
                    null,
                    null,
                    false,
                    false);
            }

            // Older snapshots may lack the original registry value. Never turn
            // that unknown baseline into a destructive registry deletion.
            if (snapshot.PagingFiles is null)
            {
                return new PageFileTuningResult(
                    false,
                    "SNAPSHOT_INCOMPLETE",
                    null,
                    null,
                    true,
                    false);
            }

            var current = backend.CaptureCurrent();
            if (!backend.SetAutomaticManaged(snapshot.AutomaticManaged))
            {
                return new PageFileTuningResult(
                    false,
                    "RESTORE_AUTO_FAILED",
                    current?.AutomaticManaged,
                    null,
                    true,
                    false);
            }

            if (!backend.WritePagingFiles(snapshot.PagingFiles))
            {
                return new PageFileTuningResult(
                    false,
                    "RESTORE_REGISTRY_FAILED",
                    current?.AutomaticManaged,
                    snapshot.AutomaticManaged,
                    true,
                    false);
            }

            var after = backend.CaptureCurrent();
            var success =
                after is not null &&
                after.AutomaticManaged == snapshot.AutomaticManaged &&
                PagingFilesEqual(after.PagingFiles, snapshot.PagingFiles);
            // The Windows settings can be restored even when the recovery
            // record cannot be removed. Never hide that retained evidence.
            var snapshotRemoved = false;
            if (success)
            {
                try
                {
                    File.Delete(statePath);
                    // File.Exists() can mask access errors. Only a definitive
                    // missing-file result proves that cleanup completed.
                    try { _ = File.GetAttributes(statePath); }
                    catch (FileNotFoundException) { snapshotRemoved = true; }
                    catch (DirectoryNotFoundException) { snapshotRemoved = true; }
                }
                catch (Exception ex) when (ex is
                    IOException or
                    UnauthorizedAccessException or
                    System.Security.SecurityException)
                {
                    // Preserve the recovery record; report its presence below.
                }
            }

            return new PageFileTuningResult(
                success,
                success
                    ? snapshotRemoved ? "RESTORED" : "RESTORED_SNAPSHOT_RETAINED"
                    : "VERIFY_FAILED",
                current?.AutomaticManaged,
                after?.AutomaticManaged,
                !snapshotRemoved,
                true);
        });

    private static PageFileTuningSnapshot? CaptureCurrent()
    {
        bool? automatic = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            foreach (var raw in results)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;
                    automatic =
                        item["AutomaticManagedPagefile"] is bool value
                            ? value
                            : null;
                }
                break;
            }
        }
        catch (ManagementException)
        {
        }
        catch (COMException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (automatic is null)
            return null;

        string[]? pagingFiles = null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(
                MemoryManagementPath,
                writable: false);
            pagingFiles = key?.GetValue(
                "PagingFiles",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames)
                as string[];
        }
        catch (Exception ex) when (ex is
            UnauthorizedAccessException or
            System.Security.SecurityException or
            IOException)
        {
        }

        return new PageFileTuningSnapshot(
            automatic.Value,
            pagingFiles,
            DateTimeOffset.UtcNow);
    }

    private static bool SetAutomaticManaged(bool value)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            foreach (var raw in results)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;
                    item["AutomaticManagedPagefile"] = value;
                    item.Put();
                    return true;
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (COMException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool WritePagingFiles(string[]? entries)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(
                MemoryManagementPath,
                writable: true);
            if (key is null)
                return false;

            if (entries is null)
            {
                key.DeleteValue(
                    "PagingFiles",
                    throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(
                    "PagingFiles",
                    entries,
                    RegistryValueKind.MultiString);
            }

            return true;
        }
        catch (Exception ex) when (ex is
            UnauthorizedAccessException or
            System.Security.SecurityException or
            IOException or
            InvalidOperationException)
        {
            return false;
        }
    }

    private static bool PagingFilesEqual(
        string[]? left,
        string[]? right)
    {
        left ??= [];
        right ??= [];
        return left
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                right.OrderBy(
                    value => value,
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsFixedReadyDrive(string driveLetter)
    {
        try
        {
            var drive = new DriveInfo(driveLetter + @":\");
            return drive.IsReady &&
                   drive.DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            return false;
        }
    }

    private PageFileTuningSnapshot? LoadSnapshot(out bool unreadable)
    {
        unreadable = false;
        try
        {
            // Read directly: File.Exists() can also return false for access errors.
            var snapshot = JsonSerializer.Deserialize<PageFileTuningSnapshot>(
                File.ReadAllText(statePath),
                HostBridge.JsonOptions);
            if (snapshot is null)
                unreadable = true;
            return snapshot;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception)
        {
            unreadable = true;
            return null;
        }
    }

    private void PersistSnapshot(PageFileTuningSnapshot snapshot)
    {
        // A unique temporary file avoids destroying an older interrupted write.
        var temp = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(snapshot, HostBridge.JsonOptions));
        // Never replace a recovery snapshot created by another operation.
        File.Move(temp, statePath, overwrite: false);
    }
}

public sealed record PageFileTuningSnapshot(
    bool AutomaticManaged,
    string[]? PagingFiles,
    DateTimeOffset CapturedAt);

public sealed record PageFileTuningResult(
    bool Success,
    string Status,
    bool? BeforeAutomatic,
    bool? AfterAutomatic,
    bool RestoreAvailable,
    bool RebootRequired);


public sealed record PageFileTuningBackend(
    Func<PageFileTuningSnapshot?> CaptureCurrent,
    Func<bool, bool> SetAutomaticManaged,
    Func<string[]?, bool> WritePagingFiles,
    Func<string, bool> IsFixedReadyDrive);
