using System.Diagnostics;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class ExplorerAuditService
{
    public AuditResult Analyze()
    {
        var items = new List<AuditItem>();

        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    items.Add(new AuditItem(
                        "Explorer",
                        "explorer.exe",
                        $"{process.WorkingSet64 / 1024d / 1024d:F1} MB",
                        $"PID {process.Id} · Threads {process.Threads.Count} · Handles {process.HandleCount}",
                        process.Responding ? "RESPONDING" : "NOT_RESPONDING"));
                }
                catch (InvalidOperationException)
                {
                    // Process exited during the snapshot.
                }
            }
        }

        return new AuditResult(
            items.Count == 0 ? "NO_DATA" : "OK",
            items);
    }
}
