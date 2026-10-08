using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class AndroidAvdService
{
    private static readonly TimeSpan ScanBudget =
        TimeSpan.FromSeconds(5);

    private readonly string? avdHomeOverride;
    private readonly Func<IReadOnlySet<string>> activeAvdReader;

    public AndroidAvdService()
        : this(null, ReadActiveAvds)
    {
    }

    internal AndroidAvdService(
        string? avdHomeOverride,
        Func<IReadOnlySet<string>> activeAvdReader)
    {
        this.avdHomeOverride = string.IsNullOrWhiteSpace(avdHomeOverride)
            ? null
            : Path.GetFullPath(avdHomeOverride);
        this.activeAvdReader = activeAvdReader;
    }

    public Task<AndroidAvdReport> AnalyzeAsync() =>
        Task.Run(Analyze);

    private AndroidAvdReport Analyze()
    {
        var avdHome = ResolveAvdHome();
        if (string.IsNullOrWhiteSpace(avdHome) ||
            !Directory.Exists(avdHome))
        {
            return new AndroidAvdReport(
                DateTimeOffset.UtcNow,
                avdHome,
                false,
                false,
                0,
                0,
                0,
                []);
        }

        var active = activeAvdReader();
        var stopwatch = Stopwatch.StartNew();
        var partial = false;
        var items = new List<AndroidAvdItem>();

        IEnumerable<string> directories;
        try
        {
            directories = Directory
                .EnumerateDirectories(
                    avdHome,
                    "*.avd",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException)
        {
            return new AndroidAvdReport(
                DateTimeOffset.UtcNow,
                avdHome,
                true,
                true,
                0,
                0,
                0,
                []);
        }

        foreach (var directory in directories)
        {
            if (stopwatch.Elapsed >= ScanBudget)
            {
                partial = true;
                break;
            }

            var name = Path.GetFileNameWithoutExtension(directory);
            var isActive = active.Contains(name);
            var size = MeasureDirectory(
                directory,
                stopwatch,
                out var measurementPartial);
            partial |= measurementPartial;

            var config = ReadConfig(directory);
            var lastWrite = SafeLastWrite(directory);
            var lockPresent = HasLockArtifacts(directory);

            items.Add(new AndroidAvdItem(
                name,
                directory,
                size,
                lastWrite,
                isActive,
                lockPresent,
                config.RamMb,
                config.CpuCores,
                config.GpuMode,
                config.DataPartitionSize,
                isActive
                    ? "ACTIVE_PROTECTED"
                    : "INACTIVE_REVIEW",
                isActive
                    ? "AVD activo detectado en la línea de comandos del emulador/QEMU; nunca es candidato a limpieza."
                    : "AVD inactivo. Puede ocupar espacio, pero requiere revisión explícita antes de eliminarse."));
        }

        return new AndroidAvdReport(
            DateTimeOffset.UtcNow,
            avdHome,
            true,
            partial,
            items.Count,
            items.Count(item => item.Active),
            items
                .Where(item => !item.Active)
                .Sum(item => item.LogicalBytes),
            items);
    }

    private string? ResolveAvdHome()
    {
        if (!string.IsNullOrWhiteSpace(avdHomeOverride))
            return avdHomeOverride;

        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable(
                         "ANDROID_AVD_HOME"),
                     ReadUserEnvironment("ANDROID_AVD_HOME"),
                     Path.Combine(
                         Environment.GetFolderPath(
                             Environment.SpecialFolder.UserProfile),
                         ".android",
                         "avd")
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                return Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(candidate));
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? ReadUserEnvironment(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(
                name,
                EnvironmentVariableTarget.User);
        }
        catch
        {
            return null;
        }
    }

    private static long MeasureDirectory(
        string directory,
        Stopwatch stopwatch,
        out bool partial)
    {
        partial = false;
        long total = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.AllDirectories))
            {
                if (stopwatch.Elapsed >= ScanBudget)
                {
                    partial = true;
                    break;
                }

                try
                {
                    total += new FileInfo(path).Length;
                }
                catch (Exception ex) when (ex is
                    IOException or
                    UnauthorizedAccessException)
                {
                    partial = true;
                }
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException)
        {
            partial = true;
        }

        return total;
    }

    private static AvdConfig ReadConfig(string directory)
    {
        var path = Path.Combine(directory, "config.ini");
        if (!File.Exists(path))
            return new AvdConfig(null, null, null, null);

        int? ram = null;
        int? cores = null;
        string? gpu = null;
        string? dataSize = null;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();

                if (key.Equals(
                        "hw.ramSize",
                        StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, out var parsedRam))
                {
                    ram = parsedRam;
                }
                else if (key.Equals(
                             "hw.cpu.ncore",
                             StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(value, out var parsedCores))
                {
                    cores = parsedCores;
                }
                else if (key.Equals(
                             "hw.gpu.mode",
                             StringComparison.OrdinalIgnoreCase))
                {
                    gpu = value;
                }
                else if (key.Equals(
                             "disk.dataPartition.size",
                             StringComparison.OrdinalIgnoreCase))
                {
                    dataSize = value;
                }
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException)
        {
        }

        return new AvdConfig(
            ram,
            cores,
            gpu,
            dataSize);
    }

    private static DateTimeOffset? SafeLastWrite(string directory)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(directory);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasLockArtifacts(string directory)
    {
        try
        {
            return Directory
                .EnumerateFileSystemEntries(
                    directory,
                    "*lock*",
                    SearchOption.TopDirectoryOnly)
                .Any();
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlySet<string> ReadActiveAvds()
    {
        var result = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, CommandLine FROM Win32_Process");
            using var rows = searcher.Get();

            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var name =
                        Convert.ToString(item["Name"]) ?? string.Empty;
                    if (!name.Contains(
                            "emulator",
                            StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains(
                            "qemu-system",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var commandLine =
                        Convert.ToString(item["CommandLine"]);
                    var avd = TryReadAvdName(commandLine);
                    if (!string.IsNullOrWhiteSpace(avd))
                        result.Add(avd);
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

    internal static string? TryReadAvdName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var tokens = Tokenize(commandLine);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Equals(
                    "-avd",
                    StringComparison.OrdinalIgnoreCase))
            {
                return tokens[i + 1];
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Tokenize(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private sealed record AvdConfig(
        int? RamMb,
        int? CpuCores,
        string? GpuMode,
        string? DataPartitionSize);
}

public sealed record AndroidAvdItem(
    string Name,
    string Path,
    long LogicalBytes,
    DateTimeOffset? LastWriteAt,
    bool Active,
    bool LockArtifactsPresent,
    int? RamMb,
    int? CpuCores,
    string? GpuMode,
    string? DataPartitionSize,
    string Status,
    string Reason);

public sealed record AndroidAvdReport(
    DateTimeOffset CapturedAt,
    string? AvdHome,
    bool Available,
    bool Partial,
    int AvdCount,
    int ActiveCount,
    long InactiveLogicalBytes,
    IReadOnlyList<AndroidAvdItem> Items);
