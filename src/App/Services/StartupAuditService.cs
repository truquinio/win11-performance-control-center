using System.Management;
using System.Runtime.InteropServices;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class StartupAuditService
{
    private const int MaxItems = 80;

    public Task<AuditResult> AnalyzeAsync() =>
        Task.Run(() =>
        {
            var items = new List<AuditItem>();
            ReadStartupCommands(items);
            ReadAutomaticServices(items);
            ReadScheduledTasks(items);

            return new AuditResult(
                items.Count == 0 ? "NO_DATA" : "OK",
                [.. items.Take(MaxItems)]);
        });

    private static void ReadStartupCommands(
        ICollection<AuditItem> items)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Command, Location, User FROM Win32_StartupCommand");

            foreach (var raw in searcher.Get())
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    if (items.Count >= MaxItems) return;
                    items.Add(new AuditItem(
                        "Startup",
                        Convert.ToString(item["Name"]) ?? "Unknown",
                        Convert.ToString(item["Location"]) ?? "Unknown",
                        Convert.ToString(item["Command"]),
                        "REGISTERED"));
                }
            }
        }
        catch (ManagementException)
        {
            // WMI startup inventory is optional.
        }
        catch (COMException)
        {
            // WMI provider rejected enumeration.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only policy restriction.
        }
    }

    private static void ReadScheduledTasks(
        ICollection<AuditItem> items)
    {
        try
        {
            var scope = new ManagementScope(
                @"\\.\root\Microsoft\Windows\TaskScheduler");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery(
                    "SELECT TaskName, TaskPath, State FROM MSFT_ScheduledTask"));

            foreach (var raw in searcher.Get())
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    if (items.Count >= MaxItems) return;

                    var taskName = Convert.ToString(item["TaskName"]);
                    if (string.IsNullOrWhiteSpace(taskName)) continue;

                    items.Add(new AuditItem(
                        "Scheduled task",
                        taskName,
                        Convert.ToString(item["State"]) ?? "Unknown",
                        Convert.ToString(item["TaskPath"]),
                        "REGISTERED"));
                }
            }
        }
        catch (ManagementException)
        {
            // Scheduled task WMI provider can be unavailable.
        }
        catch (COMException)
        {
            // WMI provider rejected enumeration.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only task inventory can be policy restricted.
        }
    }

    private static void ReadAutomaticServices(
        ICollection<AuditItem> items)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, State, StartMode FROM Win32_Service " +
                "WHERE StartMode='Auto'");

            foreach (var raw in searcher.Get())
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    if (items.Count >= MaxItems) return;

                    var state = Convert.ToString(item["State"]) ?? "Unknown";
                    items.Add(new AuditItem(
                        "Automatic service",
                        Convert.ToString(item["DisplayName"]) ??
                            Convert.ToString(item["Name"]) ??
                            "Unknown",
                        state,
                        Convert.ToString(item["Name"]),
                        state.Equals("Running", StringComparison.OrdinalIgnoreCase)
                            ? "RUNNING"
                            : "NOT_RUNNING"));
                }
            }
        }
        catch (ManagementException)
        {
            // WMI service inventory is optional.
        }
        catch (COMException)
        {
            // WMI provider rejected enumeration.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only policy restriction.
        }
    }
}
