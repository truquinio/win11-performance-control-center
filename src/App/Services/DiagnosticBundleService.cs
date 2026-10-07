using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class DiagnosticBundleService
{
    private const int MaxBundles = 10;

    private readonly string exportRoot;
    private readonly SystemSnapshotService snapshotService;
    private readonly StorageWatchService storageWatchService;
    private readonly DriverService driverService;
    private readonly UsbDiagnosticsService usbDiagnosticsService;
    private readonly CrashIntelligenceService crashIntelligenceService;
    private readonly ServiceStartupRemediationService serviceStartupService;
    private readonly ScheduledTaskRemediationService scheduledTaskService;
    private readonly StartupEntryRemediationService startupEntryService;
    private readonly OutcomeAuditService outcomeAuditService;
    private readonly HealthHistoryService healthHistoryService;
    private readonly WindowsEvidenceService windowsEvidenceService;
    private readonly RollbackCenterService rollbackCenterService;

    public DiagnosticBundleService(
        SystemSnapshotService snapshotService,
        StorageWatchService storageWatchService,
        DriverService driverService,
        UsbDiagnosticsService usbDiagnosticsService,
        CrashIntelligenceService crashIntelligenceService,
        ServiceStartupRemediationService serviceStartupService,
        ScheduledTaskRemediationService scheduledTaskService,
        StartupEntryRemediationService startupEntryService,
        OutcomeAuditService outcomeAuditService,
        HealthHistoryService healthHistoryService,
        WindowsEvidenceService windowsEvidenceService,
        RollbackCenterService rollbackCenterService,
        string? exportRoot = null)
    {
        AppPaths.EnsureDirectories();
        this.exportRoot = string.IsNullOrWhiteSpace(exportRoot)
            ? AppPaths.DiagnosticExports
            : Path.GetFullPath(exportRoot);
        this.snapshotService = snapshotService;
        this.storageWatchService = storageWatchService;
        this.driverService = driverService;
        this.usbDiagnosticsService = usbDiagnosticsService;
        this.crashIntelligenceService = crashIntelligenceService;
        this.serviceStartupService = serviceStartupService;
        this.scheduledTaskService = scheduledTaskService;
        this.startupEntryService = startupEntryService;
        this.outcomeAuditService = outcomeAuditService;
        this.healthHistoryService = healthHistoryService;
        this.windowsEvidenceService = windowsEvidenceService;
        this.rollbackCenterService = rollbackCenterService;

        Directory.CreateDirectory(this.exportRoot);
    }

    public async Task<DiagnosticBundleResult> CreateAsync()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var snapshotTask = snapshotService.CaptureAsync();
        var storageTask = storageWatchService.AuditAsync();
        var driversTask = driverService.AnalyzeAsync();
        var usbTask = usbDiagnosticsService.AnalyzeAsync();
        var crashTask = crashIntelligenceService.AnalyzeAsync();
        var servicesTask = serviceStartupService.PreviewAsync();
        var tasksTask = scheduledTaskService.PreviewAsync();
        var entriesTask = startupEntryService.PreviewAsync();

        await Task.WhenAll(
            snapshotTask,
            storageTask,
            driversTask,
            usbTask,
            crashTask,
            servicesTask,
            tasksTask,
            entriesTask);

        var snapshot = await snapshotTask;
        var storage = await storageTask;
        var drivers = await driversTask;
        var usb = await usbTask;
        var crash = await crashTask;
        var services = await servicesTask;
        var tasks = await tasksTask;
        var entries = await entriesTask;

        var outcomes = outcomeAuditService.Analyze();
        var changes = healthHistoryService.ReadChanges();
        var registry = windowsEvidenceService.AnalyzeRegistry();
        var com = windowsEvidenceService.AnalyzeCom();
        var certificates = windowsEvidenceService.AnalyzeCertificates();
        var rollback = rollbackCenterService.Analyze();

        var token = Guid.NewGuid().ToString("N")[..8];
        var baseName =
            $"Win11PCC-diagnostic-{generatedAt:yyyyMMdd-HHmmss}-{token}";
        var staging = Path.Combine(
            exportRoot,
            ".staging-" + token);
        var zipPath = Path.Combine(
            exportRoot,
            baseName + ".zip");

        Directory.CreateDirectory(staging);
        try
        {
            WriteJson(
                staging,
                "manifest.json",
                new
                {
                    schemaVersion = 1,
                    generatedAt,
                    appVersion = Assembly
                        .GetExecutingAssembly()
                        .GetName()
                        .Version?
                        .ToString(),
                    sanitized = true,
                    note = "Bundle local sanitizado: no incluye comandos de autoarranque, argumentos de tareas, IDs PnP/USB, claves de producto, cookies, sesiones ni logs crudos."
                });

            WriteJson(
                staging,
                "system.json",
                new
                {
                    snapshot.CapturedAt,
                    snapshot.CpuPercent,
                    snapshot.MemoryTotalBytes,
                    snapshot.MemoryUsedBytes,
                    snapshot.MemoryAvailableBytes,
                    snapshot.DiskDrive,
                    snapshot.DiskTotalBytes,
                    snapshot.DiskFreeBytes,
                    network = snapshot.Network is null
                        ? null
                        : new
                        {
                            snapshot.Network.Name,
                            snapshot.Network.LinkMbps
                        },
                    snapshot.IntegrityStatus,
                    snapshot.DriverStatus,
                    snapshot.ActivationStatus,
                    snapshot.RebootRequired,
                    snapshot.UptimeSeconds,
                    snapshot.OperationState
                });

            WriteJson(
                staging,
                "storage.json",
                new
                {
                    storage.CapturedAt,
                    storage.WarningCount,
                    volumes = storage.Volumes.Select(volume => new
                    {
                        volume.Name,
                        volume.Label,
                        volume.FileSystem,
                        volume.TotalBytes,
                        volume.FreeBytes,
                        volume.FreePercent,
                        volume.Status
                    })
                });

            WriteJson(
                staging,
                "drivers-usb.json",
                new
                {
                    drivers.Status,
                    drivers.ProblemCount,
                    driverProblems = drivers.Problems.Select(item => new
                    {
                        item.Name,
                        item.ErrorCode,
                        item.Manufacturer
                    }),
                    usb.DeviceCount,
                    usb.ProblemCount,
                    usb.RestartEligibleCount,
                    usbDevices = usb.Devices.Select(item => new
                    {
                        item.Name,
                        item.Manufacturer,
                        item.PnpClass,
                        item.ProblemCode,
                        item.Status,
                        item.RestartEligible,
                        item.Reason
                    })
                });

            WriteJson(
                staging,
                "reliability.json",
                new
                {
                    crash.EventsRead,
                    crash.HighSeverityCount,
                    crash.MediumSeverityCount,
                    insights = crash.Insights.Select(item => new
                    {
                        item.Category,
                        item.Severity,
                        item.Title,
                        item.Occurrences,
                        item.LatestAt,
                        item.RecommendedActionId,
                        item.Rationale
                    })
                });

            WriteJson(
                staging,
                "startup.json",
                new
                {
                    services.AutomaticCount,
                    services.EligibleCount,
                    services.ProtectedCount,
                    services = services.Services.Select(item => new
                    {
                        item.ServiceName,
                        item.DisplayName,
                        item.State,
                        item.StartMode,
                        item.Protected,
                        item.Reason,
                        item.RestoreAvailable,
                        item.DependentServiceCount,
                        dependents = item.Dependents
                    }),
                    tasks.TaskCount,
                    tasks.EligibleCount,
                    tasks.ProtectedCount,
                    tasks.RestoreAvailableCount,
                    scheduledTasks = tasks.Tasks.Select(item => new
                    {
                        item.FullName,
                        item.State,
                        item.Protected,
                        item.Reason,
                        item.RestoreAvailable
                    }),
                    startupEntries = entries.Entries.Select(item => new
                    {
                        item.Name,
                        item.Scope,
                        item.View,
                        item.Enabled,
                        item.Protected,
                        item.Reason,
                        item.RestoreAvailable
                    })
                });

            WriteJson(
                staging,
                "health-history.json",
                new
                {
                    changes.PreviousCapturedAt,
                    changes.CurrentCapturedAt,
                    changes.BaselineRequired,
                    changes.Changes,
                    changes.Trend
                });

            WriteJson(
                staging,
                "outcomes.json",
                new
                {
                    outcomes.WriteActionCount,
                    outcomes.ClassifiedCount,
                    outcomes.PostcheckedCount,
                    outcomes.CoveragePercent,
                    outcomes.IncompleteRuns,
                    actions = outcomes.Actions.Select(item => new
                    {
                        item.ActionId,
                        item.VerificationLevel,
                        item.Reversible,
                        item.RollbackExpected,
                        item.CompletedRuns,
                        item.FailedRuns,
                        item.RejectedRuns,
                        item.IncompleteRuns,
                        item.LastTerminalAt
                    })
                });

            WriteJson(
                staging,
                "windows-evidence.json",
                new
                {
                    registry = SummarizeEvidence(registry),
                    com = SummarizeEvidence(com),
                    certificates = SummarizeEvidence(certificates)
                });

            WriteJson(
                staging,
                "recovery.json",
                new
                {
                    incompleteOperations = rollback.IncompleteOperations,
                    rollbackEntries = rollback.Entries.Select(item => new
                    {
                        item.Id,
                        item.Title,
                        item.Detail,
                        item.ActionId,
                        item.RequiresAdmin,
                        item.Risk
                    })
                });

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            ZipFile.CreateFromDirectory(
                staging,
                zipPath,
                CompressionLevel.Optimal,
                includeBaseDirectory: false);

            var info = new FileInfo(zipPath);
            RetainNewestBundles();

            return new DiagnosticBundleResult(
                true,
                generatedAt,
                zipPath,
                info.Length,
                9,
                true,
                "Bundle diagnóstico sanitizado creado localmente.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is
                IOException or
                UnauthorizedAccessException)
            {
            }
        }
    }

    private static object SummarizeEvidence(
        WindowsEvidenceReport report) =>
        new
        {
            report.Area,
            report.Status,
            report.Partial,
            report.AttentionCount,
            byStatus = report.Items
                .GroupBy(item => item.Status)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.OrdinalIgnoreCase)
        };

    private static void WriteJson(
        string directory,
        string name,
        object value)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                value,
                HostBridge.JsonOptions));
    }

    private void RetainNewestBundles()
    {
        try
        {
            var old = new DirectoryInfo(exportRoot)
                .GetFiles("Win11PCC-diagnostic-*.zip")
                .OrderByDescending(file => file.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Skip(MaxBundles);

            foreach (var file in old)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (ex is
                    IOException or
                    UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException)
        {
        }
    }
}

public sealed record DiagnosticBundleResult(
    bool Success,
    DateTimeOffset GeneratedAt,
    string Path,
    long Bytes,
    int JsonFiles,
    bool Sanitized,
    string Message);
