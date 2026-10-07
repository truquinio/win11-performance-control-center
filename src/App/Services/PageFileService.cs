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
            var settings = new Dictionary<
                string,
                (ulong InitialMb, ulong MaximumMb)>(
                    StringComparer.OrdinalIgnoreCase);

            try
            {
                using var computerSearcher = new ManagementObjectSearcher(
                    "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
                using var computerResults = computerSearcher.Get();
                foreach (var raw in computerResults)
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
                using var settingSearcher = new ManagementObjectSearcher(
                    "SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting");
                using var settingResults = settingSearcher.Get();
                foreach (var raw in settingResults)
                {
                    using (raw)
                    {
                        if (raw is not ManagementObject item)
                            continue;

                        var name = Convert.ToString(item["Name"]);
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        settings[name] = (
                            ToUInt64(item["InitialSize"]),
                            ToUInt64(item["MaximumSize"]));
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

            try
            {
                using var usageSearcher = new ManagementObjectSearcher(
                    "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");

                using var usageResults = usageSearcher.Get();
                foreach (var raw in usageResults)
                {
                    using (raw)
                    {
                        if (raw is not ManagementObject item)
                            continue;

                        var name =
                            Convert.ToString(item["Name"]) ?? "Unknown";
                        settings.TryGetValue(
                            name,
                            out var configured);

                        entries.Add(new PageFileEntry(
                            name,
                            ToUInt64(item["AllocatedBaseSize"]),
                            ToUInt64(item["CurrentUsage"]),
                            ToUInt64(item["PeakUsage"]),
                            settings.ContainsKey(name)
                                ? configured.InitialMb
                                : null,
                            settings.ContainsKey(name)
                                ? configured.MaximumMb
                                : null));
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
