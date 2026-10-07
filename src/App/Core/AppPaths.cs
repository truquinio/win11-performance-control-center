using System.IO;

namespace Win11PerformanceControlCenter.App.Core;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "Win11PerformanceControlCenter");

    public static string Logs => Path.Combine(Root, "Logs");
    public static string State => Path.Combine(Root, "State");
    public static string Quarantine => Path.Combine(Root, "Quarantine");
    public static string EdgeExtensionQuarantine =>
        Path.Combine(Quarantine, "EdgeExtensions");
    public static string WebView2UserData =>
        Path.Combine(Root, "WebView2");

    public static string AppLog => Path.Combine(Logs, "app.jsonl");
    public static string EcoQosState =>
        Path.Combine(State, "ecoqos.json");
    public static string StorageWatchState =>
        Path.Combine(State, "storage-watch.json");
    public static string ServiceStartupState =>
        Path.Combine(State, "service-startup.json");
    public static string PowerPlanState =>
        Path.Combine(State, "power-plan.json");
    public static string PageFileState =>
        Path.Combine(State, "pagefile.json");
    public static string WorkloadModeState =>
        Path.Combine(State, "workload-mode.json");
    public static string StartupEntryState =>
        Path.Combine(State, "startup-entries.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Quarantine);
        Directory.CreateDirectory(WebView2UserData);
    }
}
