using System.IO;
using Microsoft.Win32;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class BrowserInventoryService
{
    private static readonly BrowserSpec[] Specs =
    [
        new("Microsoft Edge", "msedge.exe", Environment.SpecialFolder.LocalApplicationData, @"Microsoft\Edge\User Data"),
        new("Google Chrome", "chrome.exe", Environment.SpecialFolder.LocalApplicationData, @"Google\Chrome\User Data"),
        new("Brave", "brave.exe", Environment.SpecialFolder.LocalApplicationData, @"BraveSoftware\Brave-Browser\User Data"),
        new("Mozilla Firefox", "firefox.exe", Environment.SpecialFolder.ApplicationData, @"Mozilla\Firefox\Profiles"),
        new("Helium", "helium.exe", Environment.SpecialFolder.LocalApplicationData, @"Helium\User Data")
    ];

    public BrowserInventory Analyze()
    {
        var browsers = new List<BrowserInstallation>();

        foreach (var spec in Specs)
        {
            var executable = FindAppPath(spec.ExecutableName);
            var profile = Path.Combine(
                Environment.GetFolderPath(spec.ProfileFolder),
                spec.ProfileRelativePath);
            if (executable is null && !Directory.Exists(profile))
                continue;

            browsers.Add(new BrowserInstallation(
                spec.Name,
                GetFileVersion(executable),
                executable,
                Directory.Exists(profile)));
        }

        return new BrowserInventory(browsers);
    }

    private static string? FindAppPath(string executableName)
    {
        foreach (var hive in new[]
                 {
                     RegistryHive.CurrentUser,
                     RegistryHive.LocalMachine
                 })
        {
            foreach (var view in new[]
                     {
                         RegistryView.Registry64,
                         RegistryView.Registry32
                     })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executableName}");
                    var value = key?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(value) &&
                        File.Exists(value))
                    {
                        return value;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Read-only discovery is best-effort.
                }
                catch (System.Security.SecurityException)
                {
                    // Registry discovery can be policy restricted.
                }
            }
        }

        return null;
    }

    private static string? GetFileVersion(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) ||
            !File.Exists(executable))
            return null;

        try
        {
            return System.Diagnostics.FileVersionInfo
                .GetVersionInfo(executable!)
                .ProductVersion;
        }
        catch
        {
            return null;
        }
    }

    private sealed record BrowserSpec(
        string Name,
        string ExecutableName,
        Environment.SpecialFolder ProfileFolder,
        string ProfileRelativePath);
}
