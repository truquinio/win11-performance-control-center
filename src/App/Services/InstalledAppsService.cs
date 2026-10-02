using System.IO;
using Microsoft.Win32;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class InstalledAppsService
{
    private const int MaxItems = 160;
    private const string UninstallPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public AuditResult Analyze()
    {
        var items = new List<AuditItem>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                ReadHive(hive, view, items);
        }

        var distinct = items
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxItems)
            .ToArray();

        return new AuditResult(
            distinct.Length == 0 ? "NO_DATA" : "OK",
            distinct);
    }

    private static void ReadHive(
        RegistryHive hive,
        RegistryView view,
        ICollection<AuditItem> items)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall is null) return;
            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                if (items.Count >= MaxItems * 2) return;

                using var key = uninstall.OpenSubKey(subKeyName);
                var name = key?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var version = key?.GetValue("DisplayVersion") as string ?? "—";
                var publisher = key?.GetValue("Publisher") as string;

                items.Add(new AuditItem(
                    "Installed app",
                    name,
                    version,
                    publisher,
                    "INSTALLED"));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Registry inventory is best-effort.
        }
        catch (System.Security.SecurityException)
        {
            // Registry access can be restricted by local policy.
        }
        catch (IOException)
        {
            // A transient registry/hive I/O error must not break inventory.
        }
    }
}
