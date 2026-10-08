using System.Management;
using System.Runtime.InteropServices;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class StorageMediaService
{
    private readonly Func<IReadOnlyList<StorageVolumeMedia>>? volumeReader;
    private readonly Func<IReadOnlyList<PageFileSettingInfo>>? pageFileReader;
    private readonly string systemDrive;

    public StorageMediaService()
        : this(null, null, Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\")
    {
    }

    internal StorageMediaService(
        Func<IReadOnlyList<StorageVolumeMedia>>? volumeReader,
        Func<IReadOnlyList<PageFileSettingInfo>>? pageFileReader,
        string systemDrive)
    {
        this.volumeReader = volumeReader;
        this.pageFileReader = pageFileReader;
        this.systemDrive = NormalizeDrive(systemDrive);
    }

    public Task<StorageMediaReport> AnalyzeAsync() =>
        Task.Run(() =>
        {
            var volumes = volumeReader?.Invoke() ?? ReadVolumes();
            var pageFiles = pageFileReader?.Invoke() ?? ReadPageFiles();
            return new StorageMediaReport(
                DateTimeOffset.UtcNow,
                systemDrive,
                volumes,
                AssessPageFile(volumes, pageFiles));
        });

    private IReadOnlyList<StorageVolumeMedia> ReadVolumes()
    {
        var disks = ReadDiskMetadata();
        var result = new List<StorageVolumeMedia>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace " +
                "FROM Win32_LogicalDisk WHERE DriveType=3");
            using var rows = searcher.Get();

            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var id = Convert.ToString(item["DeviceID"]);
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var drive = NormalizeDrive(id);
                    var diskIndex = ResolveDiskIndex(id);
                    disks.TryGetValue(diskIndex ?? -1, out var disk);

                    var size = ToInt64(item["Size"]);
                    var free = ToInt64(item["FreeSpace"]);
                    result.Add(new StorageVolumeMedia(
                        drive,
                        diskIndex,
                        disk?.Model,
                        disk?.MediaType ?? "UNKNOWN",
                        Convert.ToString(item["VolumeName"]),
                        Convert.ToString(item["FileSystem"]),
                        size,
                        free,
                        size > 0
                            ? Math.Round(free * 100d / size, 1)
                            : 0d,
                        drive.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)));
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

                    var model = Convert.ToString(item["Model"])?.Trim();
                    result[index.Value] = new DiskMetadata(
                        model,
                        storageMedia.TryGetValue(index.Value, out var media)
                            ? media
                            : InferMediaType(model));
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
                    if (raw is not ManagementObject item ||
                        !int.TryParse(
                            Convert.ToString(item["DeviceId"]),
                            out var index))
                        continue;

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
            using var partitionSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{escaped}'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition");
            using var partitions = partitionSearcher.Get();

            foreach (var rawPartition in partitions)
            {
                using (rawPartition)
                {
                    if (rawPartition is not ManagementObject partition)
                        continue;

                    var partitionId = Convert.ToString(partition["DeviceID"]);
                    if (string.IsNullOrWhiteSpace(partitionId))
                        continue;

                    var partitionEscaped = partitionId.Replace("'", "''");
                    using var diskSearcher = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionEscaped}'}} " +
                        "WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                    using var disks = diskSearcher.Get();

                    foreach (var rawDisk in disks)
                    {
                        using (rawDisk)
                        {
                            if (rawDisk is not ManagementObject disk)
                                continue;
                            var index = ToInt32(disk["Index"]);
                            if (index is not null)
                                return index;
                        }
                    }
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

    private static IReadOnlyList<PageFileSettingInfo> ReadPageFiles()
    {
        var result = new List<PageFileSettingInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting");
            using var rows = searcher.Get();
            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;
                    var name = Convert.ToString(item["Name"]);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    result.Add(new PageFileSettingInfo(
                        name,
                        ToInt32(item["InitialSize"]) ?? 0,
                        ToInt32(item["MaximumSize"]) ?? 0));
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

    internal static PageFilePlacementAssessment AssessPageFile(
        IReadOnlyList<StorageVolumeMedia> volumes,
        IReadOnlyList<PageFileSettingInfo> pageFiles)
    {
        const int userCeilingMb = 10 * 1024;

        var totalMaximumMb = pageFiles.Sum(item => Math.Max(0, item.MaximumSizeMb));
        var withinCeiling =
            pageFiles.Count > 0 &&
            totalMaximumMb <= userCeilingMb;

        var details = pageFiles
            .Select(item =>
            {
                var drive = NormalizeDrive(Path.GetPathRoot(item.Name) ?? string.Empty);
                var volume = volumes.FirstOrDefault(candidate =>
                    candidate.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase));
                return new PageFilePlacementItem(
                    item.Name,
                    item.InitialSizeMb,
                    item.MaximumSizeMb,
                    drive,
                    volume?.MediaType ?? "UNKNOWN",
                    volume?.FreePercent);
            })
            .ToArray();

        var lowSpacePlacement = details.Any(item =>
            item.FreePercent is double free && free < 6d);

        string status;
        string recommendation;
        if (pageFiles.Count == 0)
        {
            status = "NOT_CONFIGURED";
            recommendation =
                "No se encontró una configuración manual de pagefile. La app no crea ni mueve uno automáticamente desde este diagnóstico.";
        }
        else if (!withinCeiling)
        {
            status = "OVER_USER_CEILING";
            recommendation =
                $"El máximo configurado suma {totalMaximumMb / 1024d:F1} GiB, por encima del techo de 10 GiB. Revisar antes de cambiar.";
        }
        else if (lowSpacePlacement)
        {
            status = "WITHIN_CAP_LOW_SPACE";
            recommendation =
                "El pagefile está dentro del techo acordado, pero alguna unidad tiene poco espacio. Conservar la ubicación por defecto; liberar datos primero. No mover pagefile automáticamente.";
        }
        else
        {
            status = "CURRENT_ACCEPTABLE";
            recommendation =
                "La configuración actual está dentro del techo acordado. No hay motivo suficiente para mover el pagefile solo por el tipo de medio.";
        }

        return new PageFilePlacementAssessment(
            status,
            withinCeiling,
            totalMaximumMb,
            lowSpacePlacement,
            false,
            recommendation,
            details);
    }

    private static string InferMediaType(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "UNKNOWN";
        return model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("NVME", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("SOLID STATE", StringComparison.OrdinalIgnoreCase)
            ? "SSD"
            : "UNKNOWN";
    }

    private static string NormalizeDrive(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var root = Path.GetPathRoot(value.Trim()) ?? value.Trim();
        return root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static int? ToInt32(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static long ToInt64(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToInt64(value);
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

public sealed record StorageVolumeMedia(
    string Drive,
    int? DiskIndex,
    string? Model,
    string MediaType,
    string? Label,
    string? FileSystem,
    long SizeBytes,
    long FreeBytes,
    double FreePercent,
    bool SystemDrive);

public sealed record PageFileSettingInfo(
    string Name,
    int InitialSizeMb,
    int MaximumSizeMb);

public sealed record PageFilePlacementItem(
    string Name,
    int InitialSizeMb,
    int MaximumSizeMb,
    string Drive,
    string MediaType,
    double? FreePercent);

public sealed record PageFilePlacementAssessment(
    string Status,
    bool WithinUserCeiling,
    int TotalMaximumMb,
    bool LowSpacePlacement,
    bool RelocationRecommended,
    string Recommendation,
    IReadOnlyList<PageFilePlacementItem> Entries);

public sealed record StorageMediaReport(
    DateTimeOffset CapturedAt,
    string SystemDrive,
    IReadOnlyList<StorageVolumeMedia> Volumes,
    PageFilePlacementAssessment PageFile);
