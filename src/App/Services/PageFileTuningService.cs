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
    private readonly StorageMediaService storageMediaService;

    public PageFileTuningService(
        string? statePath = null,
        StorageMediaService? storageMediaService = null)
    {
        AppPaths.EnsureDirectories();
        this.storageMediaService =
            storageMediaService ?? new StorageMediaService();
        this.statePath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.PageFileState
            : Path.GetFullPath(statePath);
        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public async Task<PageFileTuningResult> ApplyCappedProfileAsync()
    {
        var media = await storageMediaService.AnalyzeAsync();
        var recommendation = media.PageFileRecommendation;

        if (!recommendation.Available ||
            recommendation.PagingFiles.Count == 0)
        {
            return new PageFileTuningResult(
                false,
                recommendation.Status,
                null,
                null,
                false,
                false,
                recommendation.TargetDrive,
                recommendation.TargetMediaType,
                [.. recommendation.PagingFiles],
                recommendation.Reason);
        }

        return await Task.Run(() =>
        {
            var desired = recommendation.PagingFiles.ToArray();
            var before = CaptureCurrent();
            if (before is null)
            {
                return new PageFileTuningResult(
                    false,
                    "STATE_UNKNOWN",
                    null,
                    null,
                    false,
                    false,
                    recommendation.TargetDrive,
                    recommendation.TargetMediaType,
                    desired,
                    recommendation.Reason);
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
                    false,
                    recommendation.TargetDrive,
                    recommendation.TargetMediaType,
                    desired,
                    recommendation.Reason);
            }

            if (LoadSnapshot() is null)
                PersistSnapshot(before);

            if (!SetAutomaticManaged(false))
            {
                return new PageFileTuningResult(
                    false,
                    "DISABLE_AUTOMATIC_FAILED",
                    before.AutomaticManaged,
                    null,
                    true,
                    false,
                    recommendation.TargetDrive,
                    recommendation.TargetMediaType,
                    desired,
                    recommendation.Reason);
            }

            if (!WritePagingFiles(desired))
            {
                return new PageFileTuningResult(
                    false,
                    "PAGINGFILES_WRITE_FAILED",
                    before.AutomaticManaged,
                    false,
                    true,
                    false,
                    recommendation.TargetDrive,
                    recommendation.TargetMediaType,
                    desired,
                    recommendation.Reason);
            }

            var after = CaptureCurrent();
            var success =
                after is not null &&
                !after.AutomaticManaged &&
                PagingFilesEqual(after.PagingFiles, desired);

            return new PageFileTuningResult(
                success,
                success
                    ? "CAPPED_4_8_GB_MEDIA_AWARE"
                    : "VERIFY_FAILED",
                before.AutomaticManaged,
                after?.AutomaticManaged,
                true,
                success,
                recommendation.TargetDrive,
                recommendation.TargetMediaType,
                desired,
                recommendation.Reason);
        });
    }

    public Task<PageFileTuningResult> RestoreAsync() =>
        Task.Run(() =>
        {
            var snapshot = LoadSnapshot();
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

            var current = CaptureCurrent();
            if (!SetAutomaticManaged(snapshot.AutomaticManaged))
            {
                return new PageFileTuningResult(
                    false,
                    "RESTORE_AUTO_FAILED",
                    current?.AutomaticManaged,
                    null,
                    true,
                    false);
            }

            if (!snapshot.AutomaticManaged &&
                !WritePagingFiles(snapshot.PagingFiles))
            {
                return new PageFileTuningResult(
                    false,
                    "RESTORE_REGISTRY_FAILED",
                    current?.AutomaticManaged,
                    snapshot.AutomaticManaged,
                    true,
                    false);
            }

            var after = CaptureCurrent();
            var success =
                after?.AutomaticManaged == snapshot.AutomaticManaged;
            if (success)
            {
                try { File.Delete(statePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return new PageFileTuningResult(
                success,
                success ? "RESTORED" : "VERIFY_FAILED",
                current?.AutomaticManaged,
                after?.AutomaticManaged,
                !success,
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

    private PageFileTuningSnapshot? LoadSnapshot()
    {
        try
        {
            if (!File.Exists(statePath))
                return null;
            return JsonSerializer.Deserialize<PageFileTuningSnapshot>(
                File.ReadAllText(statePath),
                HostBridge.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void PersistSnapshot(PageFileTuningSnapshot snapshot)
    {
        var temp = statePath + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(snapshot, HostBridge.JsonOptions));
        File.Move(temp, statePath, overwrite: true);
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
    bool RebootRequired,
    string? TargetDrive = null,
    string? TargetMediaType = null,
    string[]? DesiredPagingFiles = null,
    string? RecommendationReason = null);
