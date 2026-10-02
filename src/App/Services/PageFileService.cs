using System.Management;
using System.Runtime.InteropServices;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class PageFileService
{
    public Task<PageFileAnalysis> AnalyzeAsync() =>
        Task.Run(() =>
        {
            bool? automaticallyManaged = null;
            var entries = new List<PageFileEntry>();

            try
            {
                using var computerSearcher = new ManagementObjectSearcher(
                    "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
                foreach (var raw in computerSearcher.Get())
                {
                    using (raw)
                    {
                        if (raw is not ManagementObject item)
                            continue;

                        if (item["AutomaticManagedPagefile"] is bool value)
                            automaticallyManaged = value;
                    }
                    break;
                }
            }
            catch (ManagementException)
            {
                automaticallyManaged = null;
            }
            catch (COMException)
            {
                automaticallyManaged = null;
            }
            catch (UnauthorizedAccessException)
            {
                automaticallyManaged = null;
            }

            try
            {
                using var usageSearcher = new ManagementObjectSearcher(
                    "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");

                foreach (var raw in usageSearcher.Get())
                {
                    using (raw)
                    {
                        if (raw is not ManagementObject item)
                            continue;

                        entries.Add(new PageFileEntry(
                            Convert.ToString(item["Name"]) ?? "Unknown",
                            ToUInt64(item["AllocatedBaseSize"]),
                            ToUInt64(item["CurrentUsage"]),
                            ToUInt64(item["PeakUsage"])));
                    }
                }
            }
            catch (ManagementException)
            {
                // Absence of WMI data is represented by an empty collection.
            }
            catch (COMException)
            {
                // WMI provider rejected enumeration.
            }
            catch (UnauthorizedAccessException)
            {
                // Read-only WMI can be restricted by policy.
            }

            return new PageFileAnalysis(
                automaticallyManaged,
                entries);
        });

    private static ulong ToUInt64(object? value)
    {
        if (value is null) return 0;
        try { return Convert.ToUInt64(value); }
        catch (FormatException) { return 0; }
        catch (InvalidCastException) { return 0; }
        catch (OverflowException) { return 0; }
    }
}
