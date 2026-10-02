using System.Management;
using System.Runtime.InteropServices;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class DriverService
{
    private const int MaxProblems = 25;

    public Task<DriverAnalysis> AnalyzeAsync() =>
        Task.Run(() =>
        {
            var issues = new List<DriverIssue>();
            var problemCount = 0;

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, ConfigManagerErrorCode, Manufacturer, PNPDeviceID " +
                    "FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");

                foreach (var raw in searcher.Get())
                {
                    using (raw)
                    {
                        if (raw is not ManagementObject item)
                            continue;

                        problemCount++;
                        if (issues.Count >= MaxProblems)
                            continue;

                        var code = ConvertToUInt32(
                            item["ConfigManagerErrorCode"]);

                        issues.Add(new DriverIssue(
                            Convert.ToString(item["Name"]) ?? "Unknown device",
                            code,
                            Convert.ToString(item["Manufacturer"]),
                            Convert.ToString(item["PNPDeviceID"])));
                    }
                }

                return new DriverAnalysis(
                    problemCount == 0 ? "OK" : "WARNING",
                    problemCount,
                    issues);
            }
            catch (ManagementException)
            {
                return Unknown();
            }
            catch (COMException)
            {
                return Unknown();
            }
            catch (UnauthorizedAccessException)
            {
                return Unknown();
            }
        });

    private static DriverAnalysis Unknown() =>
        new(
            "UNKNOWN",
            0,
            []);

    private static uint ConvertToUInt32(object? value)
    {
        if (value is null) return 0;
        try { return Convert.ToUInt32(value); }
        catch (FormatException) { return 0; }
        catch (InvalidCastException) { return 0; }
        catch (OverflowException) { return 0; }
    }
}
