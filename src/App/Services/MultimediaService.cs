using System.Management;
using System.Runtime.InteropServices;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class MultimediaService
{
    private const int MaxDevices = 40;

    public Task<MultimediaInventory> AnalyzeAsync() =>
        Task.Run(() =>
        {
            var devices = new List<MultimediaDevice>();
            ReadDevices(
                "SELECT Name, Status FROM Win32_SoundDevice",
                "Audio",
                devices);
            ReadDevices(
                "SELECT Name, Status FROM Win32_VideoController",
                "Video",
                devices);

            return new MultimediaInventory(
                [.. devices.Take(MaxDevices)]);
        });

    private static void ReadDevices(
        string query,
        string kind,
        ICollection<MultimediaDevice> destination)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            foreach (var raw in results)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    if (destination.Count >= MaxDevices)
                        return;

                    destination.Add(new MultimediaDevice(
                        kind,
                        Convert.ToString(item["Name"]) ?? "Unknown device",
                        Convert.ToString(item["Status"]) ?? "Unknown"));
                }
            }
        }
        catch (ManagementException)
        {
            // Missing WMI provider should degrade gracefully.
        }
        catch (COMException)
        {
            // WMI provider rejected enumeration.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only inventory can be restricted by policy.
        }
    }
}
