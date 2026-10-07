using System.IO;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class MaintenanceAutomationService
{
    private static readonly string[] FixedReadOnlyScope =
    [
        "system.health.snapshot",
        "disk.volumes.audit",
        "drivers.analyze",
        "system.crash.analyze",
        "drivers.usb.analyze",
        "browsers.extensions.health",
        "startup.services.preview",
        "startup.entries.preview"
    ];

    private readonly string statePath;
    private readonly WorkloadGuardService workloadGuard;
    private readonly SystemSnapshotService snapshotService;
    private readonly StorageWatchService storageWatchService;
    private readonly DriverService driverService;
    private readonly CrashIntelligenceService crashIntelligenceService;
    private readonly UsbDiagnosticsService usbDiagnosticsService;
    private readonly BrowserExtensionHealthService browserExtensionHealthService;
    private readonly ServiceStartupRemediationService serviceStartupService;
    private readonly StartupEntryRemediationService startupEntryService;

    public MaintenanceAutomationService(
        WorkloadGuardService workloadGuard,
        SystemSnapshotService snapshotService,
        StorageWatchService storageWatchService,
        DriverService driverService,
        CrashIntelligenceService crashIntelligenceService,
        UsbDiagnosticsService usbDiagnosticsService,
        BrowserExtensionHealthService browserExtensionHealthService,
        ServiceStartupRemediationService serviceStartupService,
        StartupEntryRemediationService startupEntryService,
        string? statePath = null)
    {
        AppPaths.EnsureDirectories();
        this.statePath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.MaintenancePolicyState
            : Path.GetFullPath(statePath);
        this.workloadGuard = workloadGuard;
        this.snapshotService = snapshotService;
        this.storageWatchService = storageWatchService;
        this.driverService = driverService;
        this.crashIntelligenceService = crashIntelligenceService;
        this.usbDiagnosticsService = usbDiagnosticsService;
        this.browserExtensionHealthService = browserExtensionHealthService;
        this.serviceStartupService = serviceStartupService;
        this.startupEntryService = startupEntryService;

        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public MaintenancePolicyStatus GetStatus()
    {
        var state = LoadState();
        var workload = workloadGuard.GetStatus();
        var enabled = state.Mode == "READ_ONLY_IDLE";
        var canRun = enabled &&
            workload.EffectiveMode is "IDLE" or "MAINTENANCE" &&
            !workload.StorageCritical;

        var reason = !enabled
            ? "Automatización desactivada. Es el valor por defecto."
            : workload.StorageCritical
                ? "D: está en umbral crítico; la automatización queda bloqueada."
                : workload.EffectiveMode == "IN_USE"
                    ? "El equipo está en uso; el lote read-only queda diferido."
                    : "Lote read-only elegible. No contiene acciones WRITE.";

        return new MaintenancePolicyStatus(
            state.Mode,
            canRun,
            workload,
            FixedReadOnlyScope,
            state.UpdatedAt,
            reason);
    }

    public MaintenancePolicyStatus SetMode(string mode)
    {
        var normalized = NormalizeMode(mode);
        Persist(new MaintenancePolicyState(
            normalized,
            DateTimeOffset.UtcNow));
        return GetStatus();
    }

    public async Task<MaintenanceRunReport> RunAsync()
    {
        var policy = GetStatus();
        if (!policy.CanRunNow)
        {
            return new MaintenanceRunReport(
                DateTimeOffset.UtcNow,
                false,
                policy.Reason,
                policy,
                []);
        }

        var items = new List<MaintenanceDiagnosticItem>();

        await CaptureAsync(
            items,
            "system.health.snapshot",
            async () =>
            {
                var snapshot = await snapshotService.CaptureAsync();
                var memoryPercent = snapshot.MemoryTotalBytes == 0
                    ? 0d
                    : snapshot.MemoryUsedBytes * 100d /
                      snapshot.MemoryTotalBytes;
                return (
                    "OK",
                    $"CPU {snapshot.CpuPercent:F0}% · RAM {memoryPercent:F0}% · {snapshot.DiskDrive} libre {snapshot.DiskFreeBytes / 1073741824d:F1} GiB");
            });

        await CaptureAsync(
            items,
            "disk.volumes.audit",
            async () =>
            {
                var report = await storageWatchService.AuditAsync();
                return (
                    report.WarningCount == 0 ? "OK" : "WARNING",
                    $"{report.Volumes.Count} volumen(es), {report.WarningCount} con presión de espacio.");
            });

        await CaptureAsync(
            items,
            "drivers.analyze",
            async () =>
            {
                var report = await driverService.AnalyzeAsync();
                return (
                    report.Status,
                    $"{report.ProblemCount} dispositivo(s) con código de problema.");
            });

        await CaptureAsync(
            items,
            "system.crash.analyze",
            async () =>
            {
                var report = await crashIntelligenceService.AnalyzeAsync();
                return (
                    report.HighSeverityCount > 0
                        ? "WARNING"
                        : "OK",
                    $"{report.Insights.Count} patrón(es), {report.HighSeverityCount} de prioridad alta.");
            });

        await CaptureAsync(
            items,
            "drivers.usb.analyze",
            async () =>
            {
                var report = await usbDiagnosticsService.AnalyzeAsync();
                return (
                    report.ProblemCount > 0
                        ? "WARNING"
                        : "OK",
                    $"{report.DeviceCount} USB, {report.ProblemCount} con problema, {report.RestartEligibleCount} reiniciable(s).");
            });

        await CaptureAsync(
            items,
            "browsers.extensions.health",
            () => Task.Run(() =>
            {
                var report = browserExtensionHealthService.AnalyzeEdge();
                return (
                    report.BrokenCount > 0 ? "WARNING" : "OK",
                    $"{report.InstalledCount} instalada(s), {report.BrokenCount} rota(s), {report.DataOnlyCount} residuo(s).");
            }));

        await CaptureAsync(
            items,
            "startup.services.preview",
            async () =>
            {
                var report = await serviceStartupService.PreviewAsync();
                return (
                    "OK",
                    $"{report.EligibleCount} servicio(s) revisable(s), {report.ProtectedCount} protegido(s).");
            });

        await CaptureAsync(
            items,
            "startup.entries.preview",
            async () =>
            {
                var report = await startupEntryService.PreviewAsync();
                return (
                    "OK",
                    $"{report.EligibleCount} entrada(s) revisable(s), {report.ProtectedCount} protegida(s).");
            });

        return new MaintenanceRunReport(
            DateTimeOffset.UtcNow,
            true,
            "Mantenimiento read-only completado. No se ejecutó ninguna acción WRITE.",
            policy,
            items);
    }

    private static async Task CaptureAsync(
        ICollection<MaintenanceDiagnosticItem> items,
        string actionId,
        Func<Task<(string Status, string Summary)>> work)
    {
        try
        {
            var result = await work();
            items.Add(new MaintenanceDiagnosticItem(
                actionId,
                result.Status,
                result.Summary,
                null));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            items.Add(new MaintenanceDiagnosticItem(
                actionId,
                "ERROR",
                "El diagnóstico falló de forma aislada; el lote continuó.",
                ex.GetType().Name));
        }
    }

    private MaintenancePolicyState LoadState()
    {
        try
        {
            if (!File.Exists(statePath))
                return new MaintenancePolicyState(
                    "OFF",
                    null);

            var state = JsonSerializer.Deserialize<MaintenancePolicyState>(
                File.ReadAllText(statePath),
                HostBridge.JsonOptions);
            return state is null
                ? new MaintenancePolicyState("OFF", null)
                : state with { Mode = NormalizeMode(state.Mode) };
        }
        catch
        {
            return new MaintenancePolicyState(
                "OFF",
                null);
        }
    }

    private void Persist(MaintenancePolicyState state)
    {
        var temp = statePath + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(
                state,
                HostBridge.JsonOptions));
        File.Move(temp, statePath, overwrite: true);
    }

    private static string NormalizeMode(string mode) =>
        mode.Trim().ToUpperInvariant() switch
        {
            "OFF" => "OFF",
            "READ_ONLY_IDLE" => "READ_ONLY_IDLE",
            _ => throw new InvalidOperationException(
                "Modo de automatización no permitido.")
        };

    private sealed record MaintenancePolicyState(
        string Mode,
        DateTimeOffset? UpdatedAt);
}

public sealed record MaintenancePolicyStatus(
    string Mode,
    bool CanRunNow,
    WorkloadGuardStatus Workload,
    IReadOnlyList<string> FixedReadOnlyScope,
    DateTimeOffset? UpdatedAt,
    string Reason);

public sealed record MaintenanceDiagnosticItem(
    string ActionId,
    string Status,
    string Summary,
    string? ErrorType);

public sealed record MaintenanceRunReport(
    DateTimeOffset CompletedAt,
    bool Executed,
    string Message,
    MaintenancePolicyStatus Policy,
    IReadOnlyList<MaintenanceDiagnosticItem> Items);
