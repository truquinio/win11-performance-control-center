using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class ActionPlanService
{
    private readonly ActionCatalog catalog;
    private readonly SystemSnapshotService snapshotService;
    private readonly StorageWatchService storageWatch;
    private readonly PageFileService pageFileService;
    private readonly ServiceStartupRemediationService serviceStartup;
    private readonly BrowserExtensionRemediationService browserRemediation;
    private readonly ProcessHygieneService processHygiene;
    private readonly RollbackCenterService rollbackCenter;
    private readonly WorkloadGuardService workloadGuard;

    public ActionPlanService(
        ActionCatalog catalog,
        SystemSnapshotService snapshotService,
        StorageWatchService storageWatch,
        PageFileService pageFileService,
        ServiceStartupRemediationService serviceStartup,
        BrowserExtensionRemediationService browserRemediation,
        ProcessHygieneService processHygiene,
        RollbackCenterService rollbackCenter,
        WorkloadGuardService workloadGuard)
    {
        this.catalog = catalog;
        this.snapshotService = snapshotService;
        this.storageWatch = storageWatch;
        this.pageFileService = pageFileService;
        this.serviceStartup = serviceStartup;
        this.browserRemediation = browserRemediation;
        this.processHygiene = processHygiene;
        this.rollbackCenter = rollbackCenter;
        this.workloadGuard = workloadGuard;
    }

    public async Task<ActionPlanReport> BuildAsync(
        string? excludeOperationId = null)
    {
        var snapshotTask = snapshotService.CaptureAsync();
        var storageTask = storageWatch.AuditAsync();
        var pageFileTask = pageFileService.AnalyzeAsync();
        var servicesTask = serviceStartup.PreviewAsync();
        var browserTask = Task.Run(browserRemediation.Preview);
        var hygieneTask = Task.Run(processHygiene.Analyze);

        await Task.WhenAll(
            snapshotTask,
            storageTask,
            pageFileTask,
            servicesTask,
            browserTask,
            hygieneTask);

        var snapshot = await snapshotTask;
        var storage = await storageTask;
        var pageFile = await pageFileTask;
        var services = await servicesTask;
        var browser = await browserTask;
        var hygiene = await hygieneTask;
        var rollback = rollbackCenter.Analyze(excludeOperationId);
        var workload = workloadGuard.GetStatus();

        var items = new List<ActionPlanItem>();

        foreach (var volume in storage.Volumes)
        {
            if (volume.Status == "CRITICAL" ||
                volume.Status == "LOW" ||
                volume.Status == "WATCH")
            {
                Add(
                    items,
                    volume.Status is "CRITICAL" or "LOW"
                        ? "HIGH"
                        : "MEDIUM",
                    $"Espacio bajo en {volume.Name}",
                    $"{volume.FreePercent:F1}% libre ({FormatBytes(volume.FreeBytes)}).",
                    "disk.hotspots.scan",
                    workload);
            }
        }

        var memoryPercent = snapshot.MemoryTotalBytes == 0
            ? 0d
            : snapshot.MemoryUsedBytes * 100d /
              snapshot.MemoryTotalBytes;
        if (memoryPercent >= 85d)
        {
            Add(
                items,
                "HIGH",
                "Presión alta de memoria",
                $"RAM usada: {memoryPercent:F0}%. Primero identifica candidatos; no se recorta memoria automáticamente.",
                "memory.trim.preview",
                workload);
        }
        else if (memoryPercent >= 75d)
        {
            Add(
                items,
                "MEDIUM",
                "Memoria elevada",
                $"RAM usada: {memoryPercent:F0}%. Conviene revisar procesos con working set alto.",
                "memory.trim.preview",
                workload);
        }

        if (snapshot.CpuPercent >= 90d)
        {
            Add(
                items,
                "MEDIUM",
                "CPU muy alta",
                $"Uso total medido: {snapshot.CpuPercent:F0}%. Revisa candidatos antes de aplicar EcoQoS.",
                "cpu.ecoqos.analyze",
                workload);
        }

        if (snapshot.Network is null)
        {
            Add(
                items,
                "MEDIUM",
                "Conectividad sin adaptador activo",
                "No se detectó un adaptador de red activo en el snapshot.",
                "network.test",
                workload);
        }

        if (string.Equals(
                snapshot.IntegrityStatus,
                "REPAIRABLE",
                StringComparison.OrdinalIgnoreCase))
        {
            Add(
                items,
                "HIGH",
                "Windows reporta integridad reparable",
                "Hay evidencia previa de corrupción reparable.",
                "system.integrity.repair",
                workload);
        }

        var dEntry = pageFile.Entries.FirstOrDefault(entry =>
            entry.Name.StartsWith(
                @"D:\",
                StringComparison.OrdinalIgnoreCase));
        if (dEntry?.MaximumSizeMb is ulong maxMb &&
            maxMb > 10UL * 1024UL)
        {
            Add(
                items,
                "MEDIUM",
                "Pagefile de D: supera el techo configurado",
                $"Máximo actual: {maxMb / 1024d:F1} GiB. El perfil de la app limita D: a 8 GiB.",
                "memory.pagefile.capped",
                workload);
        }

        if (services.EligibleCount > 0)
        {
            Add(
                items,
                "LOW",
                "Servicios automáticos revisables",
                $"{services.EligibleCount} servicio(s) de terceros pueden revisarse. No se cambia ninguno automáticamente.",
                "startup.services.preview",
                workload);
        }

        if (browser.CandidateCount > 0)
        {
            Add(
                items,
                "LOW",
                "Residuos de extensiones Edge",
                $"{browser.CandidateCount} residuo(s), {FormatBytes(browser.TotalBytes)}. No están ejecutándose.",
                "browsers.extensions.orphans.preview",
                workload);
        }

        if (hygiene.StoppableCount > 0)
        {
            Add(
                items,
                hygiene.EstimatedReclaimMb >= 1024d
                    ? "HIGH"
                    : "MEDIUM",
                "Residuos de laboratorio en ejecución",
                $"{hygiene.StoppableCount} residuo(s) preclasificado(s), ~{hygiene.EstimatedReclaimMb:F0} MiB observados. Bots y automatizaciones sensibles permanecen protegidos.",
                "processes.hygiene.analyze",
                workload);
        }

        if (rollback.IncompleteOperations.Count > 0)
        {
            Add(
                items,
                "HIGH",
                "Operaciones incompletas",
                $"{rollback.IncompleteOperations.Count} operación(es) WRITE quedaron sin estado terminal.",
                "backup.rollback.center",
                workload);
        }
        else if (rollback.Entries.Count > 0)
        {
            Add(
                items,
                "LOW",
                "Rollbacks disponibles",
                $"{rollback.Entries.Count} cambio(s) tienen estado previo recuperable.",
                "backup.rollback.center",
                workload);
        }

        if (snapshot.RebootRequired == true)
        {
            items.Add(new ActionPlanItem(
                "MEDIUM",
                "Reinicio pendiente",
                "Windows indica que hay un reinicio pendiente. No se reinicia automáticamente.",
                null,
                "READ",
                "SAFE",
                false,
                false,
                null));
        }

        var ordered = items
            .OrderBy(item => PriorityRank(item.Priority))
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ActionPlanReport(
            DateTimeOffset.UtcNow,
            workload,
            ordered,
            ordered.Count(item => item.Priority == "HIGH"),
            ordered.Count(item => item.Priority == "MEDIUM"),
            ordered.Count(item => item.ActionId is not null),
            ordered.Count(item => item.Deferred));
    }

    private void Add(
        ICollection<ActionPlanItem> items,
        string priority,
        string title,
        string reason,
        string actionId,
        WorkloadGuardStatus workload)
    {
        var action = catalog.GetRequired(actionId);
        var deferred =
            WorkloadGuardService.IsHeavyOrDisruptive(actionId) &&
            !workload.HeavyActionsAllowed;

        items.Add(new ActionPlanItem(
            priority,
            title,
            reason,
            action.Id,
            action.Mode.ToString(),
            action.Risk.ToString(),
            action.RequiresAdmin,
            deferred,
            deferred ? workload.Reason : null));
    }

    private static int PriorityRank(string priority) =>
        priority switch
        {
            "HIGH" => 0,
            "MEDIUM" => 1,
            _ => 2
        };

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1073741824d:F1} GiB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / 1048576d:F1} MiB";
        return $"{bytes / 1024d:F1} KiB";
    }
}

public sealed record ActionPlanItem(
    string Priority,
    string Title,
    string Reason,
    string? ActionId,
    string Mode,
    string Risk,
    bool RequiresAdmin,
    bool Deferred,
    string? DeferReason);

public sealed record ActionPlanReport(
    DateTimeOffset CapturedAt,
    WorkloadGuardStatus Workload,
    IReadOnlyList<ActionPlanItem> Items,
    int HighPriorityCount,
    int MediumPriorityCount,
    int ActionableCount,
    int DeferredCount);
