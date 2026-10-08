using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class StorageMediaService
{
    private readonly Func<IReadOnlyList<StorageVolumeMedia>>? fixtureReader;
    private readonly string systemDrive;

    public StorageMediaService()
        : this(null, Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\")
    {
    }

    internal StorageMediaService(
        Func<IReadOnlyList<StorageVolumeMedia>>? fixtureReader,
        string systemDrive)
    {
        this.fixtureReader = fixtureReader;
        this.systemDrive = NormalizeDrive(systemDrive);
    }

    public Task<StorageMediaReport> AnalyzeAsync() =>
        Task.Run(() =>
        {
            var volumes = fixtureReader?.Invoke() ?? ReadVolumes();
            var recommendation =
                PageFilePlacementPolicy.Recommend(
                    volumes,
                    systemDrive);

            return new StorageMediaReport(
                DateTimeOffset.UtcNow,
                systemDrive,
                volumes,
                recommendation);
        });

    private IReadOnlyList<StorageVolumeMedia> ReadVolumes()
    {
        var disks = ReadDiskMetadata();
        var result = new List<StorageVolumeMedia>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
            using var rows = searcher.Get();

            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var deviceId = Convert.ToString(item["DeviceID"]);
                    if (string.IsNullOrWhiteSpace(deviceId))
                        continue;

                    var drive = NormalizeDrive(deviceId);
                    var diskIndex = ResolveDiskIndex(deviceId);
                    disks.TryGetValue(diskIndex ?? -1, out var disk);

                    var size = ToInt64(item["Size"]);
                    var free = ToInt64(item["FreeSpace"]);
                    var freePercent = size > 0
                        ? Math.Round(free * 100d / size, 1)
                        : 0d;

                    result.Add(new StorageVolumeMedia(
                        drive,
                        diskIndex,
                        disk?.Model,
                        disk?.MediaType ?? "UNKNOWN",
                        size,
                        free,
                        freePercent,
                        drive.Equals(
                            systemDrive,
                            StringComparison.OrdinalIgnoreCase)));
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

        return result
            .OrderBy(item => item.Drive, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<int, DiskMetadata> ReadDiskMetadata()
    {
        var result = new Dictionary<int, DiskMetadata>();
        var storageMedia = ReadStorageMediaTypes();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, Model FROM Win32_DiskDrive");
            using var rows = searcher.Get();
            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var index = ToInt32(item["Index"]);
                    if (index is null)
                        continue;

                    var model =
                        Convert.ToString(item["Model"])?.Trim();
                    var media = storageMedia.TryGetValue(
                        index.Value,
                        out var detected)
                        ? detected
                        : InferMediaType(model);

                    result[index.Value] = new DiskMetadata(
                        model,
                        media);
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

        return result;
    }

    private static Dictionary<int, string> ReadStorageMediaTypes()
    {
        var result = new Dictionary<int, string>();

        try
        {
            var scope = new ManagementScope(
                @"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery(
                    "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"));
            using var rows = searcher.Get();

            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    if (!int.TryParse(
                            Convert.ToString(item["DeviceId"]),
                            out var index))
                    {
                        continue;
                    }

                    var mediaType = ToInt32(item["MediaType"]);
                    result[index] = mediaType switch
                    {
                        3 => "HDD",
                        4 => "SSD",
                        5 => "SCM",
                        _ => "UNKNOWN"
                    };
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

        return result;
    }

    private static int? ResolveDiskIndex(string logicalDrive)
    {
        try
        {
            var escaped = logicalDrive.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{escaped}'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition");
            using var rows = searcher.Get();

            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var index = ToInt32(item["DiskIndex"]);
                    if (index is not null)
                        return index;
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

        return null;
    }

    private static string InferMediaType(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "UNKNOWN";

        return model.Contains(
                   "SSD",
                   StringComparison.OrdinalIgnoreCase) ||
               model.Contains(
                   "NVME",
                   StringComparison.OrdinalIgnoreCase) ||
               model.Contains(
                   "SOLID STATE",
                   StringComparison.OrdinalIgnoreCase)
            ? "SSD"
            : "UNKNOWN";
    }

    private static string NormalizeDrive(string value)
    {
        var root = Path.GetPathRoot(value.Trim()) ?? value.Trim();
        return root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static int? ToInt32(object? value)
    {
        if (value is null)
            return null;
        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static long ToInt64(object? value)
    {
        if (value is null)
            return 0;
        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return 0;
        }
    }

    private sealed record DiskMetadata(
        string? Model,
        string MediaType);
}

public static class PageFilePlacementPolicy
{
    public const int InitialMb = 4096;
    public const int MaximumMb = 8192;
    private const long GiB = 1024L * 1024 * 1024;
    private const long MinimumFreeAfterMaxBytes = 12L * GiB;
    private const double MinimumFreeAfterMaxPercent = 12d;

    public static PageFileRecommendation Recommend(
        IReadOnlyList<StorageVolumeMedia> volumes,
        string systemDrive)
    {
        var normalizedSystem = systemDrive
            .TrimEnd('\', '/');

        var eligible = volumes
            .Where(volume => IsEligible(volume))
            .OrderByDescending(volume =>
                volume.MediaType.Equals(
                    "SSD",
                    StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(volume =>
                volume.Drive.Equals(
                    normalizedSystem,
                    StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(volume => volume.FreeBytes)
            .ToArray();

        var selected = eligible.FirstOrDefault();
        if (selected is null)
        {
            return new PageFileRecommendation(
                false,
                "NO_SAFE_TARGET",
                null,
                null,
                [],
                null,
                null,
                "No hay una unidad fija con margen suficiente para un pagefile máximo de 8 GB.");
        }

        var freeAfterBytes =
            selected.FreeBytes - MaximumMb * 1024L * 1024L;
        var freeAfterPercent = selected.SizeBytes > 0
            ? Math.Round(
                freeAfterBytes * 100d / selected.SizeBytes,
                1)
            : 0d;

        var targetEntry =
            $@"{selected.Drive}\pagefile.sys {InitialMb} {MaximumMb}";
        string[] entries;
        if (selected.Drive.Equals(
                normalizedSystem,
                StringComparison.OrdinalIgnoreCase))
        {
            entries = [targetEntry];
        }
        else
        {
            entries =
            [
                $@"{normalizedSystem}\pagefile.sys 512 512",
                targetEntry
            ];
        }

        var reason = selected.MediaType.Equals(
                "SSD",
                StringComparison.OrdinalIgnoreCase)
            ? $"Se prioriza {selected.Drive} porque está en SSD y conserva {freeAfterPercent:F1}% libre incluso con el máximo de 8 GB."
            : $"No se detectó un SSD elegible; {selected.Drive} conserva margen suficiente y se mantiene el máximo en 8 GB.";

        return new PageFileRecommendation(
            true,
            selected.MediaType.Equals(
                "SSD",
                StringComparison.OrdinalIgnoreCase)
                ? "SSD_PREFERRED"
                : "SAFE_FALLBACK",
            selected.Drive,
            selected.MediaType,
            entries,
            freeAfterBytes,
            freeAfterPercent,
            reason);
    }

    private static bool IsEligible(StorageVolumeMedia volume)
    {
        if (volume.SizeBytes <= 0 ||
            volume.FreeBytes <= MaximumMb * 1024L * 1024L)
        {
            return false;
        }

        var freeAfter =
            volume.FreeBytes - MaximumMb * 1024L * 1024L;
        var percentAfter =
            freeAfter * 100d / volume.SizeBytes;

        return freeAfter >= MinimumFreeAfterMaxBytes &&
               percentAfter >= MinimumFreeAfterMaxPercent;
    }
}

public sealed record StorageVolumeMedia(
    string Drive,
    int? DiskIndex,
    string? Model,
    string MediaType,
    long SizeBytes,
    long FreeBytes,
    double FreePercent,
    bool SystemDrive);

public sealed record PageFileRecommendation(
    bool Available,
    string Status,
    string? TargetDrive,
    string? TargetMediaType,
    IReadOnlyList<string> PagingFiles,
    long? FreeAfterMaxBytes,
    double? FreeAfterMaxPercent,
    string Reason);

public sealed record StorageMediaReport(
    DateTimeOffset CapturedAt,
    string SystemDrive,
    IReadOnlyList<StorageVolumeMedia> Volumes,
    PageFileRecommendation PageFileRecommendation);
