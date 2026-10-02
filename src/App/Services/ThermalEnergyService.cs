using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class ThermalEnergyService
{
    public AuditResult Analyze()
    {
        var items = new List<AuditItem>();
        ReadPowerPlan(items);
        ReadThermalZones(items);
        ReadNvidiaTemperatures(items);

        return new AuditResult(
            items.Count == 0 ? "NO_DATA" : "OK",
            items);
    }

    private static void ReadPowerPlan(ICollection<AuditItem> items)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2\power");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery(
                    "SELECT ElementName, InstanceID FROM Win32_PowerPlan WHERE IsActive=True"));

            foreach (var raw in searcher.Get())
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    items.Add(new AuditItem(
                        "Power",
                        "Active power plan",
                        Convert.ToString(item["ElementName"]) ?? "Unknown",
                        Convert.ToString(item["InstanceID"]),
                        "ACTIVE"));
                }
                break;
            }
        }
        catch (ManagementException)
        {
            // Power WMI namespace can be unavailable.
        }
        catch (COMException)
        {
            // Firmware/WMI provider can reject enumeration.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only WMI can be policy restricted.
        }
    }

    private static void ReadThermalZones(ICollection<AuditItem> items)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery(
                    "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));

            foreach (var baseObject in searcher.Get())
            {
                using (baseObject)
                {
                    if (baseObject is not ManagementObject item)
                        continue;

                    var raw = ToDouble(item["CurrentTemperature"]);
                    var celsius = raw > 0 ? raw / 10d - 273.15d : double.NaN;
                    items.Add(new AuditItem(
                        "Thermal",
                        Convert.ToString(item["InstanceName"]) ?? "Thermal zone",
                        double.IsNaN(celsius) ? "UNAVAILABLE" : $"{celsius:F1} °C",
                        "ACPI thermal zone; sensor availability depends on firmware.",
                        double.IsNaN(celsius) ? "NO_DATA" : "OBSERVED"));
                }
            }
        }
        catch (ManagementException)
        {
            // Many PCs do not expose ACPI thermal zones to WMI.
        }
        catch (COMException)
        {
            // Firmware/WMI provider can reject enumeration.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only WMI can be restricted.
        }
    }

    private static void ReadNvidiaTemperatures(ICollection<AuditItem> items)
    {
        var executable = ResolveNvidiaSmi();
        if (executable is null)
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--query-gpu=name,temperature.gpu --format=csv,noheader,nounits",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));

            try
            {
                process.WaitForExitAsync(cts.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                return;
            }

            var output = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
                return;

            foreach (var rawLine in output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = rawLine.LastIndexOf(',');
                if (separator <= 0 || separator >= rawLine.Length - 1)
                    continue;

                var name = rawLine[..separator].Trim();
                var temperatureText = rawLine[(separator + 1)..].Trim();
                if (!double.TryParse(
                    temperatureText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var celsius))
                    continue;

                if (celsius is < -20 or > 150)
                    continue;

                items.Add(new AuditItem(
                    "Thermal",
                    string.IsNullOrWhiteSpace(name) ? "NVIDIA GPU" : name,
                    $"{celsius:F1} °C",
                    "GPU temperature reported by the installed NVIDIA driver via nvidia-smi.",
                    "OBSERVED"));
            }
        }
        catch (Win32Exception)
        {
            // nvidia-smi is not installed or not available on PATH.
        }
        catch (InvalidOperationException)
        {
            // Process could not be started/read safely.
        }
        catch (IOException)
        {
            // Driver utility output was unavailable.
        }
    }

    /// <summary>
    /// The utility is only launched from the locations the NVIDIA driver
    /// installs it to. A bare file name would let the working directory or a
    /// user-writable PATH entry decide which binary runs.
    /// </summary>
    public static string? ResolveNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "nvidia-smi.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvidia-smi.exe")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed environment-derived path; try the next location.
            }
        }

        return null;
    }

    private static double ToDouble(object? value)
    {
        if (value is null) return 0;
        try { return Convert.ToDouble(value); }
        catch (FormatException) { return 0; }
        catch (InvalidCastException) { return 0; }
        catch (OverflowException) { return 0; }
    }
}
