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
    public static string WebView2UserData =>
        Path.Combine(Root, "WebView2");

    public static string AppLog => Path.Combine(Logs, "app.jsonl");
    public static string EcoQosState =>
        Path.Combine(State, "ecoqos.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(WebView2UserData);
    }
}
