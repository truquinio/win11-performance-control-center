using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public static class ProcessSafetyPolicy
{
    private static readonly HashSet<string> BlockedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "System",
            "Registry",
            "Idle",
            "smss",
            "csrss",
            "wininit",
            "winlogon",
            "lsass",
            "services",
            "svchost",
            "dwm",
            "explorer",
            "fontdrvhost",
            "audiodg",
            "MsMpEng",
            "SecurityHealthService",
            "Memory Compression",
            "SearchHost",
            "SearchIndexer",
            "StartMenuExperienceHost",
            "ShellExperienceHost",
            "ShellHost",
            "RuntimeBroker",
            "TextInputHost",
            "ctfmon",
            "sihost",
            "taskhostw",
            "dllhost",
            "ApplicationFrameHost",
            "SystemSettings",
            "Widgets",
            "WidgetService",
            "PhoneExperienceHost",
            "msedgewebview2",
            // Interactive browsers and automation runtimes must never be selected
            // by generic MemoryTrim/EcoQoS merely because their windows are hidden.
            "msedge",
            "chrome",
            "chrome-headless-shell",
            "chromium",
            "firefox",
            "brave",
            "helium",
            "node",
            "nodew",
            "pm2",
            "python",
            "pythonw",
            "uv",
            "cloudflared",
            "ollama",
            "claude",
            "codex",
            "java",
            "javaw",
            "powershell",
            "pwsh",
            "cmd",
            "conhost",
            "WindowsTerminal",
            "adb",
            "emulator",
            "qemu-system-x86_64",
            "qemu-system-x86_64-headless",
            "qemu-system-aarch64",
            "qemu-system-aarch64-headless",
            "qemu-system-i386"
        };

    public static bool IsEligible(
        ProcessCandidate candidate,
        bool requireBackground)
    {
        if (candidate.ProcessId <= 4 ||
            candidate.ProcessId == Environment.ProcessId ||
            BlockedNames.Contains(candidate.Name))
        {
            return false;
        }

        if (requireBackground && candidate.HasMainWindow)
            return false;

        return true;
    }
}
