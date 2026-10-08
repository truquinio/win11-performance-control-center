using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class WorkloadGuardService
{
    private const double CriticalDriveFreePercent = 3d;
    private static readonly HashSet<string> HeavyOrDisruptiveActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "system.integrity.repair",
            "disk.cleanup.execute",
            "disk.hotspots.scan",
            "network.winsock.reset",
            "drivers.rescan",
            "drivers.usb.restart",
            "processes.hygiene.stop",
            "multimedia.audio.restart",
            "windows.update.services.restart",
            "explorer.restart"
        };

    private readonly string statePath;
    private readonly bool evaluationMode;
    private readonly Func<TimeSpan>? idleProvider;
    private readonly Func<double?>? dFreeProvider;

    public WorkloadGuardService(
        string? statePath = null,
        bool evaluationMode = false,
        Func<TimeSpan>? idleProvider = null,
        Func<double?>? dFreeProvider = null)
    {
        AppPaths.EnsureDirectories();
        this.statePath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.WorkloadModeState
            : Path.GetFullPath(statePath);
        this.evaluationMode = evaluationMode;
        this.idleProvider = idleProvider;
        this.dFreeProvider = dFreeProvider;

        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public WorkloadGuardStatus GetStatus()
    {
        var configuredMode = LoadMode();
        var idle = evaluationMode
            ? TimeSpan.FromMinutes(15)
            : idleProvider?.Invoke() ?? ReadIdleTime();
        var dFreePercent = evaluationMode
            ? 50d
            : dFreeProvider?.Invoke() ?? ReadDriveFreePercent("D");

        string effectiveMode;
        if (configuredMode == "IN_USE")
        {
            effectiveMode = "IN_USE";
        }
        else if (configuredMode == "MAINTENANCE")
        {
            effectiveMode = "MAINTENANCE";
        }
        else
        {
            effectiveMode = idle >= TimeSpan.FromMinutes(10)
                ? "IDLE"
                : "IN_USE";
        }

        var storageCritical =
            dFreePercent is double free &&
            free < CriticalDriveFreePercent;
        var heavyAllowed =
            !storageCritical &&
            effectiveMode is "IDLE" or "MAINTENANCE";

        var reason = storageCritical
            ? "D: está por debajo del 3% libre; las tareas pesadas quedan bloqueadas."
            : effectiveMode == "IN_USE"
                ? "El equipo está en uso; las tareas pesadas o disruptivas quedan diferidas."
                : effectiveMode == "MAINTENANCE"
                    ? "Modo mantenimiento habilitado explícitamente."
                    : "El equipo lleva suficiente tiempo inactivo para mantenimiento.";

        return new WorkloadGuardStatus(
            configuredMode,
            effectiveMode,
            Math.Max(0, (long)idle.TotalSeconds),
            dFreePercent,
            storageCritical,
            heavyAllowed,
            reason);
    }

    public WorkloadGuardStatus SetMode(string mode)
    {
        var normalized = NormalizeMode(mode);
        Persist(new WorkloadModeState(
            normalized,
            DateTimeOffset.UtcNow));
        return GetStatus();
    }

    public bool ShouldBlock(string actionId) =>
        HeavyOrDisruptiveActions.Contains(actionId) &&
        !GetStatus().HeavyActionsAllowed;

    public static bool IsHeavyOrDisruptive(string actionId) =>
        HeavyOrDisruptiveActions.Contains(actionId);

    private string LoadMode()
    {
        try
        {
            if (!File.Exists(statePath))
                return "AUTO";

            var state = JsonSerializer.Deserialize<WorkloadModeState>(
                File.ReadAllText(statePath),
                HostBridge.JsonOptions);
            return state is null
                ? "AUTO"
                : NormalizeMode(state.Mode);
        }
        catch
        {
            return "AUTO";
        }
    }

    private void Persist(WorkloadModeState state)
    {
        var temp = statePath + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(state, HostBridge.JsonOptions));
        File.Move(temp, statePath, overwrite: true);
    }

    private static string NormalizeMode(string mode) =>
        mode.Trim().ToUpperInvariant() switch
        {
            "AUTO" => "AUTO",
            "IN_USE" => "IN_USE",
            "MAINTENANCE" => "MAINTENANCE",
            _ => throw new InvalidOperationException(
                "Modo de carga no permitido.")
        };

    private static TimeSpan ReadIdleTime()
    {
        try
        {
            var info = new LastInputInfo
            {
                Size = (uint)Marshal.SizeOf<LastInputInfo>()
            };
            if (!GetLastInputInfo(ref info))
                return TimeSpan.Zero;

            var now = unchecked((uint)Environment.TickCount);
            var elapsed = unchecked(now - info.Time);
            return TimeSpan.FromMilliseconds(elapsed);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static double? ReadDriveFreePercent(string driveName)
    {
        try
        {
            var drive = new DriveInfo(driveName + @":\");
            if (!drive.IsReady || drive.TotalSize <= 0)
                return null;
            return Math.Round(
                drive.AvailableFreeSpace * 100d / drive.TotalSize,
                1);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(
        ref LastInputInfo plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    private sealed record WorkloadModeState(
        string Mode,
        DateTimeOffset UpdatedAt);
}

public sealed record WorkloadGuardStatus(
    string ConfiguredMode,
    string EffectiveMode,
    long IdleSeconds,
    double? DFreePercent,
    bool StorageCritical,
    bool HeavyActionsAllowed,
    string Reason);
