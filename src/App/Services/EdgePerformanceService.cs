using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class EdgePerformanceService
{
    private const string EdgePolicySubKey =
        @"Software\Policies\Microsoft\Edge";
    private const string RecommendedSubKey =
        EdgePolicySubKey + @"\Recommended";
    private const string RunSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly IReadOnlyDictionary<string, int>
        RecommendedValues = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["BackgroundModeEnabled"] = 0,
            ["StartupBoostEnabled"] = 0,
            ["SleepingTabsEnabled"] = 1,
            ["SleepingTabsTimeout"] = 300,
            ["AutoDiscardSleepingTabsEnabled"] = 1,
            ["EfficiencyModeEnabled"] = 1,
            ["EfficiencyMode"] = 0
        };

    private readonly string statePath;
    private readonly bool evaluationMode;
    private bool evaluationOptimized;

    public EdgePerformanceService(
        string? statePath = null,
        bool evaluationMode = false)
    {
        AppPaths.EnsureDirectories();
        this.statePath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.EdgePerformanceState
            : Path.GetFullPath(statePath);
        this.evaluationMode = evaluationMode;

        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public EdgePerformanceReport Analyze()
    {
        if (evaluationMode)
            return AnalyzeSynthetic();

        var processes = Process.GetProcessesByName("msedge");
        try
        {
            var workingSet = processes.Sum(process =>
            {
                try
                {
                    return process.WorkingSet64;
                }
                catch
                {
                    return 0L;
                }
            });
            var privateBytes = processes.Sum(process =>
            {
                try
                {
                    return process.PrivateMemorySize64;
                }
                catch
                {
                    return 0L;
                }
            });
            var visible = processes.Count(process =>
            {
                try
                {
                    return process.MainWindowHandle != IntPtr.Zero;
                }
                catch
                {
                    return false;
                }
            });

            var effective = ReadEffectivePolicy();
            var autoLaunch = ReadAutoLaunchEntries();

            var optimized =
                effective.BackgroundModeEnabled == 0 &&
                effective.StartupBoostEnabled == 0 &&
                effective.SleepingTabsEnabled == 1 &&
                effective.SleepingTabsTimeout == 300 &&
                effective.AutoDiscardSleepingTabsEnabled == 1 &&
                effective.EfficiencyModeEnabled == 1 &&
                effective.EfficiencyMode == 0 &&
                effective.LaunchEdgeOnWindowsStartupEnabled == 0 &&
                autoLaunch.Count == 0;

            return new EdgePerformanceReport(
                processes.Length,
                visible,
                workingSet,
                privateBytes,
                effective,
                autoLaunch.Count,
                File.Exists(statePath),
                optimized,
                optimized
                    ? "Edge tiene el perfil de rendimiento recomendado por la app."
                    : BuildGuidance(effective, autoLaunch.Count));
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public EdgePerformanceChangeResult Optimize()
    {
        if (evaluationMode)
        {
            if (!File.Exists(statePath))
                PersistState(SyntheticSnapshot());
            evaluationOptimized = true;
            var evaluationReport = AnalyzeSynthetic();
            return new EdgePerformanceChangeResult(
                true,
                "OPTIMIZED",
                evaluationReport,
                true,
                "EVALUATION");
        }

        if (!File.Exists(statePath))
            PersistState(CaptureSnapshot());

        using (var currentUser = RegistryKey.OpenBaseKey(
                   RegistryHive.CurrentUser,
                   RegistryView.Registry64))
        {
            using var recommended = currentUser.CreateSubKey(
                RecommendedSubKey,
                writable: true) ??
                throw new InvalidOperationException(
                    "No se pudo abrir la política Recommended de Edge.");

            foreach (var pair in RecommendedValues)
            {
                recommended.SetValue(
                    pair.Key,
                    pair.Value,
                    RegistryValueKind.DWord);
            }

            using var policy = currentUser.CreateSubKey(
                EdgePolicySubKey,
                writable: true) ??
                throw new InvalidOperationException(
                    "No se pudo abrir la política de Edge.");

            policy.SetValue(
                "LaunchEdgeOnWindowsStartupEnabled",
                0,
                RegistryValueKind.DWord);

            using var run = currentUser.OpenSubKey(
                RunSubKey,
                writable: true);
            if (run is not null)
            {
                foreach (var name in run.GetValueNames()
                             .Where(name => name.StartsWith(
                                 "MicrosoftEdgeAutoLaunch_",
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    run.DeleteValue(
                        name,
                        throwOnMissingValue: false);
                }
            }
        }

        var report = Analyze();
        return new EdgePerformanceChangeResult(
            report.Optimized,
            report.Optimized
                ? "OPTIMIZED"
                : "VERIFY_FAILED",
            report,
            true,
            report.Optimized
                ? "Perfil aplicado y verificado. No se cerró Edge ni se modificaron perfiles, sesiones o extensiones."
                : "Se escribieron las preferencias, pero la política efectiva todavía no coincide con el perfil esperado.");
    }

    public EdgePerformanceChangeResult Restore()
    {
        if (!File.Exists(statePath))
        {
            return new EdgePerformanceChangeResult(
                true,
                "NO_SNAPSHOT",
                Analyze(),
                false,
                "No existe snapshot de Edge Performance.");
        }

        var snapshot = LoadState() ??
            throw new InvalidOperationException(
                "El snapshot de Edge Performance está corrupto.");

        if (evaluationMode)
        {
            evaluationOptimized = false;
            File.Delete(statePath);
            return new EdgePerformanceChangeResult(
                true,
                "RESTORED",
                AnalyzeSynthetic(),
                false,
                "EVALUATION");
        }

        using (var currentUser = RegistryKey.OpenBaseKey(
                   RegistryHive.CurrentUser,
                   RegistryView.Registry64))
        {
            using var recommended = currentUser.CreateSubKey(
                RecommendedSubKey,
                writable: true) ??
                throw new InvalidOperationException(
                    "No se pudo abrir la política Recommended de Edge.");

            foreach (var pair in snapshot.Recommended)
                RestoreRegistryValue(recommended, pair.Key, pair.Value);

            using var policy = currentUser.CreateSubKey(
                EdgePolicySubKey,
                writable: true) ??
                throw new InvalidOperationException(
                    "No se pudo abrir la política de Edge.");

            RestoreRegistryValue(
                policy,
                "LaunchEdgeOnWindowsStartupEnabled",
                snapshot.LaunchOnStartup);

            using var run = currentUser.CreateSubKey(
                RunSubKey,
                writable: true) ??
                throw new InvalidOperationException(
                    "No se pudo abrir Run para restaurar Edge.");

            foreach (var entry in snapshot.AutoLaunchEntries)
            {
                var current = Convert.ToString(
                    run.GetValue(
                        entry.Name,
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames));
                if (current is not null &&
                    !string.Equals(
                        current,
                        entry.Value,
                        StringComparison.Ordinal))
                {
                    return new EdgePerformanceChangeResult(
                        false,
                        "RESTORE_CONFLICT",
                        Analyze(),
                        true,
                        "Una entrada MicrosoftEdgeAutoLaunch cambió después de la optimización. No se sobrescribió; el snapshot se conserva.");
                }

                if (current is null)
                {
                    run.SetValue(
                        entry.Name,
                        entry.Value,
                        RegistryValueKind.String);
                }
            }
        }

        var verified = SnapshotMatchesCurrent(snapshot);
        if (verified)
            File.Delete(statePath);

        return new EdgePerformanceChangeResult(
            verified,
            verified ? "RESTORED" : "VERIFY_FAILED",
            Analyze(),
            !verified,
            verified
                ? "Configuración anterior restaurada y verificada. Edge no fue cerrado ni reiniciado."
                : "El rollback se aplicó parcialmente, pero no coincide con el snapshot. Se conserva el snapshot para revisión.");
    }

    private EdgePerformanceReport AnalyzeSynthetic()
    {
        var effective = evaluationOptimized
            ? new EdgeEffectivePolicy(
                0,
                0,
                1,
                300,
                1,
                1,
                0,
                0,
                false)
            : new EdgeEffectivePolicy(
                1,
                1,
                null,
                null,
                null,
                null,
                null,
                null,
                false);

        return new EdgePerformanceReport(
            evaluationOptimized ? 4 : 8,
            1,
            evaluationOptimized
                ? 700L * 1024 * 1024
                : 1600L * 1024 * 1024,
            evaluationOptimized
                ? 900L * 1024 * 1024
                : 2100L * 1024 * 1024,
            effective,
            evaluationOptimized ? 0 : 1,
            File.Exists(statePath),
            evaluationOptimized,
            evaluationOptimized
                ? "EVALUATION optimized"
                : "EVALUATION needs optimization");
    }

    private EdgePerformanceSnapshot CaptureSnapshot()
    {
        using var currentUser = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser,
            RegistryView.Registry64);
        using var recommended = currentUser.OpenSubKey(
            RecommendedSubKey);
        using var policy = currentUser.OpenSubKey(
            EdgePolicySubKey);
        using var run = currentUser.OpenSubKey(
            RunSubKey);

        var values = RecommendedValues.Keys
            .ToDictionary(
                key => key,
                key => CaptureRegistryValue(
                    recommended,
                    key),
                StringComparer.OrdinalIgnoreCase);

        var autoLaunch = run is null
            ? []
            : run.GetValueNames()
                .Where(name => name.StartsWith(
                    "MicrosoftEdgeAutoLaunch_",
                    StringComparison.OrdinalIgnoreCase))
                .Select(name => new EdgeAutoLaunchEntry(
                    name,
                    Convert.ToString(
                        run.GetValue(
                            name,
                            string.Empty,
                            RegistryValueOptions.DoNotExpandEnvironmentNames))
                    ?? string.Empty))
                .ToArray();

        return new EdgePerformanceSnapshot(
            DateTimeOffset.UtcNow,
            values,
            CaptureRegistryValue(
                policy,
                "LaunchEdgeOnWindowsStartupEnabled"),
            autoLaunch);
    }

    private EdgeEffectivePolicy ReadEffectivePolicy()
    {
        using var currentUser = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser,
            RegistryView.Registry64);
        using var policy = currentUser.OpenSubKey(
            EdgePolicySubKey);
        using var recommended = currentUser.OpenSubKey(
            RecommendedSubKey);

        int? Effective(string name)
        {
            var mandatory = ReadDword(policy, name);
            return mandatory ?? ReadDword(recommended, name);
        }

        return new EdgeEffectivePolicy(
            Effective("BackgroundModeEnabled"),
            Effective("StartupBoostEnabled"),
            Effective("SleepingTabsEnabled"),
            Effective("SleepingTabsTimeout"),
            Effective("AutoDiscardSleepingTabsEnabled"),
            Effective("EfficiencyModeEnabled"),
            Effective("EfficiencyMode"),
            ReadDword(
                policy,
                "LaunchEdgeOnWindowsStartupEnabled"),
            policy is not null &&
            (policy.GetValue(
                 "BackgroundModeEnabled") is not null ||
             policy.GetValue(
                 "StartupBoostEnabled") is not null));
    }

    private static IReadOnlyList<EdgeAutoLaunchEntry>
        ReadAutoLaunchEntries()
    {
        using var currentUser = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser,
            RegistryView.Registry64);
        using var run = currentUser.OpenSubKey(RunSubKey);
        if (run is null)
            return [];

        return run.GetValueNames()
            .Where(name => name.StartsWith(
                "MicrosoftEdgeAutoLaunch_",
                StringComparison.OrdinalIgnoreCase))
            .Select(name => new EdgeAutoLaunchEntry(
                name,
                Convert.ToString(
                    run.GetValue(
                        name,
                        string.Empty,
                        RegistryValueOptions.DoNotExpandEnvironmentNames))
                ?? string.Empty))
            .ToArray();
    }

    private static EdgeRegistryValue CaptureRegistryValue(
        RegistryKey? key,
        string name)
    {
        if (key is null)
            return new EdgeRegistryValue(false, null);

        var value = key.GetValue(name);
        return value is null
            ? new EdgeRegistryValue(false, null)
            : new EdgeRegistryValue(
                true,
                Convert.ToInt32(value));
    }

    private static void RestoreRegistryValue(
        RegistryKey key,
        string name,
        EdgeRegistryValue snapshot)
    {
        if (!snapshot.Exists)
        {
            key.DeleteValue(
                name,
                throwOnMissingValue: false);
            return;
        }

        key.SetValue(
            name,
            snapshot.Value ?? 0,
            RegistryValueKind.DWord);
    }

    private static int? ReadDword(
        RegistryKey? key,
        string name)
    {
        if (key is null)
            return null;
        var value = key.GetValue(name);
        if (value is null)
            return null;
        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private bool SnapshotMatchesCurrent(
        EdgePerformanceSnapshot snapshot)
    {
        if (evaluationMode)
            return !evaluationOptimized;

        using var currentUser = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser,
            RegistryView.Registry64);
        using var recommended = currentUser.OpenSubKey(
            RecommendedSubKey);
        using var policy = currentUser.OpenSubKey(
            EdgePolicySubKey);
        using var run = currentUser.OpenSubKey(RunSubKey);

        foreach (var pair in snapshot.Recommended)
        {
            var current = CaptureRegistryValue(
                recommended,
                pair.Key);
            if (current != pair.Value)
                return false;
        }

        if (CaptureRegistryValue(
                policy,
                "LaunchEdgeOnWindowsStartupEnabled") !=
            snapshot.LaunchOnStartup)
        {
            return false;
        }

        foreach (var entry in snapshot.AutoLaunchEntries)
        {
            var current = Convert.ToString(
                run?.GetValue(
                    entry.Name,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames));
            if (!string.Equals(
                    current,
                    entry.Value,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private EdgePerformanceSnapshot? LoadState()
    {
        try
        {
            return JsonSerializer.Deserialize<EdgePerformanceSnapshot>(
                File.ReadAllText(statePath),
                HostBridge.JsonOptions);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            return null;
        }
    }

    private void PersistState(
        EdgePerformanceSnapshot snapshot)
    {
        var temp = statePath + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(
                snapshot,
                HostBridge.JsonOptions));
        File.Move(
            temp,
            statePath,
            overwrite: true);
    }

    private static EdgePerformanceSnapshot SyntheticSnapshot() =>
        new(
            DateTimeOffset.UtcNow,
            RecommendedValues.Keys.ToDictionary(
                key => key,
                _ => new EdgeRegistryValue(false, null),
                StringComparer.OrdinalIgnoreCase),
            new EdgeRegistryValue(false, null),
            [new EdgeAutoLaunchEntry(
                "MicrosoftEdgeAutoLaunch_DEMO",
                "msedge.exe --no-startup-window")]);

    private static string BuildGuidance(
        EdgeEffectivePolicy policy,
        int autoLaunchCount)
    {
        var missing = new List<string>();
        if (policy.BackgroundModeEnabled != 0)
            missing.Add("background mode");
        if (policy.StartupBoostEnabled != 0)
            missing.Add("startup boost");
        if (policy.SleepingTabsEnabled != 1)
            missing.Add("sleeping tabs");
        if (policy.SleepingTabsTimeout != 300)
            missing.Add("timeout 5 min");
        if (policy.AutoDiscardSleepingTabsEnabled != 1)
            missing.Add("auto-discard");
        if (policy.EfficiencyModeEnabled != 1 ||
            policy.EfficiencyMode != 0)
            missing.Add("efficiency mode");
        if (policy.LaunchEdgeOnWindowsStartupEnabled != 0)
            missing.Add("Windows startup");
        if (autoLaunchCount > 0)
            missing.Add("Run auto-launch");

        return missing.Count == 0
            ? "No hay cambios pendientes."
            : "Pendiente: " + string.Join(", ", missing) + ".";
    }
}

public sealed record EdgeRegistryValue(
    bool Exists,
    int? Value);

public sealed record EdgeAutoLaunchEntry(
    string Name,
    string Value);

public sealed record EdgePerformanceSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, EdgeRegistryValue> Recommended,
    EdgeRegistryValue LaunchOnStartup,
    IReadOnlyList<EdgeAutoLaunchEntry> AutoLaunchEntries);

public sealed record EdgeEffectivePolicy(
    int? BackgroundModeEnabled,
    int? StartupBoostEnabled,
    int? SleepingTabsEnabled,
    int? SleepingTabsTimeout,
    int? AutoDiscardSleepingTabsEnabled,
    int? EfficiencyModeEnabled,
    int? EfficiencyMode,
    int? LaunchEdgeOnWindowsStartupEnabled,
    bool MandatoryPerformanceOverridesPresent);

public sealed record EdgePerformanceReport(
    int ProcessCount,
    int VisibleWindowCount,
    long WorkingSetBytes,
    long PrivateBytes,
    EdgeEffectivePolicy Policy,
    int AutoLaunchEntryCount,
    bool RestoreAvailable,
    bool Optimized,
    string Guidance);

public sealed record EdgePerformanceChangeResult(
    bool Success,
    string Status,
    EdgePerformanceReport Report,
    bool RestoreAvailable,
    string Detail);
