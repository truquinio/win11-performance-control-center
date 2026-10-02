using Microsoft.Win32;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class PrivacyAuditService
{
    private static readonly RegistryObservation[] Observations =
    [
        new(RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
            "Enabled", "Advertising ID"),
        new(RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Search",
            "BingSearchEnabled", "Web search integration"),
        new(RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
            "AllowTelemetry", "Telemetry policy"),
        new(RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            "Start_TrackProgs", "App launch tracking")
    ];

    public AuditResult Analyze()
    {
        var items = new List<AuditItem>();

        foreach (var observation in Observations)
        {
            var result = ReadValue(observation);
            var value = !result.Accessible
                ? "UNAVAILABLE"
                : !result.Exists
                    ? "NOT_SET"
                    : Convert.ToString(result.Value) ?? "UNKNOWN";
            var status = !result.Accessible
                ? "UNAVAILABLE"
                : result.Exists ? "OBSERVED" : "NOT_SET";

            items.Add(new AuditItem(
                "Privacy setting",
                observation.Label,
                value,
                observation.ValueName,
                status));
        }

        var overallStatus = items.Any(item =>
            item.Status == "UNAVAILABLE")
            ? "PARTIAL"
            : "OK";

        return new AuditResult(overallStatus, items);
    }
    private static RegistryReadResult ReadValue(
        RegistryObservation observation)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                observation.Hive,
                RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(observation.Path);
            if (key is null)
                return new RegistryReadResult(true, false, null);

            var value = key.GetValue(
                observation.ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new RegistryReadResult(
                true,
                value is not null,
                value);
        }
        catch (UnauthorizedAccessException)
        {
            return new RegistryReadResult(false, false, null);
        }
        catch (System.Security.SecurityException)
        {
            return new RegistryReadResult(false, false, null);
        }
    }

    private sealed record RegistryObservation(
        RegistryHive Hive,
        string Path,
        string ValueName,
        string Label);

    private sealed record RegistryReadResult(
        bool Accessible,
        bool Exists,
        object? Value);
}
