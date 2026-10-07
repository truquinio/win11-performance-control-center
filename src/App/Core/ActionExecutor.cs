using System.Text.Json;
using System.Diagnostics;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace Win11PerformanceControlCenter.App.Core;

public sealed class ActionExecutor(
    ActionCatalog catalog,
    OperationCoordinator coordinator,
    AppLogger logger,
    PrivilegeBoundary privilege,
    SystemSnapshotService snapshotService,
    ReliabilityService reliabilityService,
    StorageAnalysisService storageService,
    ProcessAnalysisService processService,
    ProcessTuningService processTuningService,
    PageFileService pageFileService,
    IntegrityService integrityService,
    DriverService driverService,
    ActivationService activationService,
    BrowserInventoryService browserService,
    BrowserExtensionHealthService browserExtensionHealthService,
    MultimediaService multimediaService,
    StartupAuditService startupAuditService,
    WindowsUpdateAuditService windowsUpdateAuditService,
    InstalledAppsService installedAppsService,
    PrivacyAuditService privacyAuditService,
    DeveloperToolingService developerToolingService,
    ThermalEnergyService thermalEnergyService,
    BootSleepAuditService bootSleepAuditService,
    ExplorerAuditService explorerAuditService,
    OperationRecoveryService operationRecoveryService,
    SystemHealthStateStore healthState,
    ElevatedActionClient elevatedActionClient)
{
    private const int MaxProcessSelection = 20;
    private static readonly TimeSpan ReadActionBudget =
        TimeSpan.FromMinutes(4);

    public async Task<ActionResult> ExecuteAsync(
        string id,
        JsonElement? parameters = null)
    {
        var action = catalog.GetRequired(id);
        ValidateParameters(action, parameters);
        var operationId = Guid.NewGuid().ToString("N");

        using var lease = coordinator.Begin(
            id,
            action.Mode == ActionMode.WRITE ? "OPTIMIZING" : "ANALYZING");

        var startedData = new
        {
            action.Risk,
            action.RequiresAdmin,
            action.Mode
        };
        if (action.Mode == ActionMode.WRITE)
        {
            // A WRITE never runs without its audit record on disk first.
            await logger.WriteAsync(id, "STARTED", startedData, operationId);
        }
        else
        {
            // Read-only diagnostics stay available when the log is not
            // writable (disk full, policy, lock held by another process).
            await logger.TryWriteAsync(id, "STARTED", startedData, operationId);
        }

        try
        {
            ActionResult result;

            if (action.RequiresAdmin && !privilege.IsElevated)
            {
                await logger.TryWriteAsync(id, "ELEVATION_REQUESTED", new
                {
                    boundary = "per-action",
                    action.RequiresAdmin
                }, operationId);
                if (parameters is { ValueKind: JsonValueKind.Object } supplied &&
                    supplied.EnumerateObject().Any())
                {
                    throw new InvalidOperationException(
                        "Las acciones elevadas con parámetros no están habilitadas en esta build.");
                }

                result = await elevatedActionClient.ExecuteAsync(action);
                ApplyElevatedOutcome(action.Id, result);
            }
            else
            {
                privilege.Validate(action);
                var work = ExecuteAllowedAsync(
                    action,
                    parameters,
                    operationId);

                // WRITE actions are short and must never be abandoned
                // half-way; only read-only work is put under the watchdog.
                result = action.Mode == ActionMode.WRITE
                    ? await work
                    : await OperationWatchdog.RunAsync(
                        work,
                        ReadActionBudget,
                        action.Title);
            }

            // The action already happened: a log failure here must not turn
            // an applied change into a reported failure.
            await logger.TryWriteAsync(id, "COMPLETED", new
            {
                result.Success,
                result.DryRun,
                result.Message
            }, operationId);
            return result;
        }
        catch (Exception ex)
        {
            await logger.TryWriteAsync(id, "FAILED", new
            {
                errorType = ex.GetType().Name,
                ex.Message
            }, operationId);
            throw;
        }
    }

    private static void ValidateParameters(
        ActionDefinition action,
        JsonElement? parameters)
    {
        var definitions = action.Parameters ??
            [];

        if (definitions.Count == 0)
        {
            if (parameters is { ValueKind: JsonValueKind.Object } supplied &&
                supplied.EnumerateObject().Any())
            {
                throw new InvalidOperationException(
                    "La acción no acepta parámetros.");
            }
            return;
        }

        if (parameters is not { ValueKind: JsonValueKind.Object } value)
        {
            if (definitions.Any(item => item.Required))
                throw new InvalidOperationException(
                    "Faltan parámetros obligatorios para la acción.");
            return;
        }

        var allowed = definitions.ToDictionary(
            item => item.Name,
            StringComparer.Ordinal);

        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.ContainsKey(property.Name))
                throw new InvalidOperationException(
                    "Parámetro no permitido: " + property.Name);
        }

        foreach (var definition in definitions)
        {
            if (!value.TryGetProperty(definition.Name, out var node))
            {
                if (definition.Required)
                    throw new InvalidOperationException(
                        "Falta parámetro obligatorio: " + definition.Name);
                continue;
            }

            var valid = definition.Type switch
            {
                ActionParameterType.STRING =>
                    node.ValueKind == JsonValueKind.String,
                ActionParameterType.INTEGER =>
                    node.ValueKind == JsonValueKind.Number &&
                    node.TryGetInt32(out _),
                ActionParameterType.BOOLEAN =>
                    node.ValueKind is JsonValueKind.True or JsonValueKind.False,
                ActionParameterType.INTEGER_ARRAY =>
                    IsIntegerArray(node),
                _ => false
            };

            if (!valid)
                throw new InvalidOperationException(
                    "Tipo inválido para parámetro: " + definition.Name);
        }
    }

    private static bool IsIntegerArray(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Array)
            return false;

        var count = 0;
        foreach (var item in node.EnumerateArray())
        {
            // TryGetInt32 throws for non-numeric elements instead of
            // returning false, so the kind is checked first.
            if (item.ValueKind != JsonValueKind.Number ||
                !item.TryGetInt32(out _))
            {
                return false;
            }

            count++;
            if (count > MaxProcessSelection)
                return false;
        }

        return count > 0;
    }

    private void ApplyElevatedOutcome(
        string actionId,
        ActionResult result)
    {
        if (!result.Success || result.Data is not JsonElement data)
            return;

        if (actionId == "system.integrity.check" &&
            data.TryGetProperty("status", out var statusNode) &&
            statusNode.ValueKind == JsonValueKind.String)
        {
            healthState.SetIntegrity(
                statusNode.GetString() ?? "UNKNOWN");
        }
    }

    private async Task<ActionResult> ExecuteAllowedAsync(
        ActionDefinition action,
        JsonElement? parameters,
        string operationId)
    {
        return action.Id switch
        {
            "system.health.scan" => await HealthScanAsync(),
            "system.integrity.check" => await IntegrityCheckAsync(),
            "system.reliability.analyze" => await ReliabilityAsync(),
            "memory.analyze" => await MemoryAsync(),
            "memory.trim.preview" => await Task.Run(MemoryTrimPreview),
            "memory.trim" => await MemoryTrimAsync(parameters),
            "memory.pagefile.analyze" => await PageFileAsync(),
            "cpu.ecoqos.analyze" => await Task.Run(EcoQoSAnalyze),
            "cpu.ecoqos.apply" => await EcoQoSApplyAsync(parameters),
            "cpu.ecoqos.restore" => await EcoQoSRestoreAsync(parameters),
            "disk.scan" => await StorageAsync(false),
            "disk.cleanup.safe" => await StorageAsync(true),
            "disk.cleanup.execute" => await StorageCleanupAsync(parameters),
            "disk.appdata.rank" => await AppDataRankAsync(),
            "disk.hibernate.status" => await HibernateAsync(false),
            "disk.hibernate.reduce" => await HibernateAsync(true),
            "network.test" => await NetworkAsync(),
            "drivers.analyze" => await DriversAsync(),
            "system.activation.analyze" => await ActivationAsync(),
            "browsers.inventory" => await Task.Run(BrowserInventory),
            "browsers.extensions.health" => await Task.Run(BrowserExtensionHealth),
            "multimedia.inventory" => await MultimediaAsync(),
            "startup.audit" => await StartupAuditAsync(),
            "windows.update.audit" => await Task.Run(WindowsUpdateAudit),
            "apps.inventory" => await Task.Run(InstalledAppsInventory),
            "privacy.audit" => await Task.Run(PrivacyAudit),
            "developer.audit" => await Task.Run(DeveloperAudit),
            "thermal.audit" => await Task.Run(ThermalAudit),
            "boot.audit" => await Task.Run(BootAudit),
            "sleepresume.audit" => await Task.Run(SleepResumeAudit),
            "explorer.audit" => await Task.Run(ExplorerAudit),
            "backup.status" => await Task.Run(() => BackupStatus(operationId)),
            _ => throw new InvalidOperationException("Action ID no implementada.")
        };
    }

    private async Task<ActionResult> HealthScanAsync()
    {
        var driverTask = driverService.AnalyzeAsync();
        var activationTask = activationService.AnalyzeAsync();
        var reliabilityTask = reliabilityService.GetRecentAsync(20);
        await Task.WhenAll(driverTask, activationTask, reliabilityTask);

        healthState.SetDrivers(driverTask.Result.Status);
        healthState.SetActivation(activationTask.Result.Status);
        var snapshot = await snapshotService.CaptureAsync();

        return new ActionResult(
            true,
            false,
            "Diagnóstico seguro completado. No se modificó Windows.",
            new
            {
                snapshot,
                reliabilityEvents = reliabilityTask.Result.Count,
                drivers = driverTask.Result,
                // The partial product key stays inside the backend, as in
                // the dedicated activation action.
                activation = new
                {
                    activationTask.Result.Status,
                    activationTask.Result.LicenseStatus,
                    activationTask.Result.Description
                }
            });
    }

    private async Task<ActionResult> IntegrityCheckAsync()
    {
        var result = await integrityService.CheckHealthAsync();
        healthState.SetIntegrity(result.Status);
        return new ActionResult(
            result.ExitCode == 0,
            false,
            result.Status switch
            {
                "OK" => "Integridad de Windows: no se detectó corrupción en el almacén de componentes.",
                "REPAIRABLE" => "Integridad de Windows: DISM detectó corrupción reparable. No se aplicaron reparaciones.",
                "UNREPAIRABLE" => "Integridad de Windows: DISM informó corrupción no reparable automáticamente.",
                "ERROR" => "DISM /CheckHealth finalizó con error.",
                _ => "DISM /CheckHealth finalizó sin una clasificación concluyente."
            },
            new { result.Status, result.ExitCode, durationMs = (long)result.Duration.TotalMilliseconds, result.Output });
    }

    private async Task<ActionResult> ReliabilityAsync()
    {
        var events = await reliabilityService.GetRecentAsync();
        return new ActionResult(
            true,
            false,
            $"Reliability Analyzer completado: {events.Count} eventos relevantes en la ventana analizada.",
            new { events });
    }

    private async Task<ActionResult> MemoryAsync()
    {
        var snapshot = await snapshotService.CaptureAsync();
        return new ActionResult(
            true,
            false,
            "Análisis de memoria completado sin modificar working sets.",
            new
            {
                snapshot.MemoryTotalBytes,
                snapshot.MemoryUsedBytes,
                snapshot.MemoryAvailableBytes
            });
    }

    private async Task<ActionResult> PageFileAsync()
    {
        var analysis = await pageFileService.AnalyzeAsync();
        return new ActionResult(
            true,
            false,
            analysis.Entries.Count == 0
                ? "Pagefile: Windows no devolvió datos de uso."
                : $"Pagefile: {analysis.Entries.Count} archivo(s) de paginación detectados.",
            new
            {
                automaticallyManaged = analysis.AutomaticallyManaged,
                entries = analysis.Entries
            });
    }

    private async Task<ActionResult> MemoryTrimAsync(
        JsonElement? parameters)
    {
        var processIds = GetConfirmedProcessIds(parameters);
        var targets = processService.ValidateMemoryTrimSelection(
            processIds);
        var result = await processTuningService.TrimWorkingSetsAsync(
            targets);

        var before = result.Items
            .Where(item => item.Success)
            .Sum(item => item.BeforeWorkingSetBytes ?? 0);
        var after = result.Items
            .Where(item => item.Success)
            .Sum(item => item.AfterWorkingSetBytes ?? 0);

        return new ActionResult(
            result.Succeeded > 0,
            false,
            $"MemoryTrim: {result.Succeeded}/{result.Attempted} procesos recortados. " +
            "La reducción de working set es transitoria, no memoria ganada permanentemente.",
            new
            {
                result.Attempted,
                result.Succeeded,
                result.Failed,
                beforeWorkingSetBytes = before,
                afterWorkingSetBytes = after,
                items = result.Items,
                transient = true
            });
    }

    private async Task<ActionResult> EcoQoSApplyAsync(
        JsonElement? parameters)
    {
        var processIds = GetConfirmedProcessIds(parameters);
        var targets = processService.ValidateEcoQosSelection(
            processIds);
        var result = await processTuningService.ApplyEcoQosAsync(
            targets);

        return new ActionResult(
            result.Succeeded > 0,
            false,
            $"EcoQoS: {result.Succeeded}/{result.Attempted} procesos actualizados. " +
            "Se guardó estado previo para rollback.",
            new
            {
                result.Attempted,
                result.Succeeded,
                result.Failed,
                items = result.Items,
                rollbackAvailable = result.Succeeded > 0
            });
    }

    private async Task<ActionResult> EcoQoSRestoreAsync(
        JsonElement? parameters)
    {
        var processIds = GetConfirmedProcessIds(parameters);
        var result = await processTuningService.RestoreEcoQosAsync(
            processIds);

        return new ActionResult(
            result.Succeeded > 0,
            false,
            $"EcoQoS rollback: {result.Succeeded}/{result.Attempted} procesos restaurados.",
            new
            {
                result.Attempted,
                result.Succeeded,
                result.Failed,
                items = result.Items
            });
    }

    private static int[] GetConfirmedProcessIds(
        JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value)
            throw new InvalidOperationException(
                "Faltan parámetros de proceso.");

        if (!value.TryGetProperty("confirmed", out var confirmed) ||
            confirmed.ValueKind is not JsonValueKind.True)
        {
            throw new InvalidOperationException(
                "La operación WRITE requiere confirmación explícita.");
        }

        if (!value.TryGetProperty("processIds", out var processIds) ||
            processIds.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Falta la selección de procesos.");
        }

        var ids = processIds
            .EnumerateArray()
            .Select(item => item.GetInt32())
            .Distinct()
            .ToArray();

        if (ids.Length is < 1 or > 20 ||
            ids.Any(processId => processId <= 0))
        {
            throw new InvalidOperationException(
                "La selección de procesos debe contener entre 1 y 20 PIDs positivos.");
        }

        return ids;
    }

    private ActionResult MemoryTrimPreview()
    {
        var analysis = processService.AnalyzeMemoryTrimCandidates();
        return new ActionResult(
            true,
            true,
            $"MemoryTrim: {analysis.Candidates.Count} procesos requieren revisión por working set alto. No se modificó memoria.",
            new
            {
                analysis.ObservedProcesses,
                candidates = analysis.Candidates,
                note = "La lista es una previsualización; no implica que todos los procesos deban recortarse."
            });
    }

    private ActionResult EcoQoSAnalyze()
    {
        var analysis = processService.AnalyzeEcoQosCandidates();
        return new ActionResult(
            true,
            true,
            $"EcoQoS: {analysis.Candidates.Count} candidatos de fondo requieren revisión. No se aplicaron políticas.",
            new
            {
                analysis.ObservedProcesses,
                candidates = analysis.Candidates,
                note = "La candidatura es heurística. La aplicación WRITE exige selección explícita, confirmación y snapshot para rollback."
            });
    }

    private async Task<ActionResult> StorageAsync(bool cleanupPreview)
    {
        var estimate = await storageService.AnalyzeSafeAsync();
        return new ActionResult(
            true,
            cleanupPreview,
            cleanupPreview
                ? "Previsualización completada. No se eliminó ningún archivo."
                : "Análisis de almacenamiento seguro completado.",
            new
            {
                estimatedBytes = estimate.EstimatedBytes,
                categories = estimate.Categories
            });
    }

    private async Task<ActionResult> StorageCleanupAsync(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } args ||
            !args.TryGetProperty("confirmed", out var flag) ||
            flag.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Confirmá la limpieza después de previsualizarla.");
        var summary = await storageService.CleanupSafeAsync();
        return new ActionResult(summary.FailedFiles == 0, false,
            $"Limpieza: {summary.DeletedFiles} archivos eliminados, {summary.DeletedBytes / 1048576d:F1} MiB liberados. " +
            $"Errores: {summary.FailedFiles}; categorías omitidas: {summary.SkippedCategories.Count}.",
            new
            {
                summary.DeletedBytes,
                summary.DeletedFiles,
                summary.FailedFiles,
                summary.SkippedCategories
            });
    }

    private async Task<ActionResult> AppDataRankAsync()
    {
        var folders = await storageService.RankLocalAsync();
        return new ActionResult(true, false,
            $"Ranking de AppData\\Local: {folders.Count} carpetas, solo lectura.",
            new { folders, readOnly = true });
    }

    private static async Task<ActionResult> HibernateAsync(bool reduce)
    {
        // The elevated runner validates administrative privileges for writes.
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(reduce ? "/h" : "/a");
        if (reduce) { psi.ArgumentList.Add("/type"); psi.ArgumentList.Add("reduced"); }
        using var process = Process.Start(psi) ??
            throw new InvalidOperationException("No se pudo ejecutar powercfg.exe.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException("powercfg excedió el plazo de 25 segundos.");
        }
        var stdout = await output;
        var stderr = await error;
        var success = process.ExitCode == 0;
        return new ActionResult(success, false, reduce
            ? (success ? "Hibernación reducida aplicada. Inicio rápido disponible; hibernación completa deshabilitada."
                : "No se pudo reducir la hibernación.")
            : "Estado de suspensión consultado sin modificar Windows.",
            new { exitCode = process.ExitCode, output = stdout, error = stderr });
    }

    private async Task<ActionResult> NetworkAsync()
    {
        var snapshot = await snapshotService.CaptureAsync();
        return new ActionResult(
            true,
            false,
            snapshot.Network is null
                ? "No se detectó un adaptador de red activo."
                : "Adaptador activo medido sin cambiar su configuración.",
            new { network = snapshot.Network });
    }

    private async Task<ActionResult> DriversAsync()
    {
        var analysis = await driverService.AnalyzeAsync();
        healthState.SetDrivers(analysis.Status);
        return new ActionResult(
            analysis.Status != "UNKNOWN",
            false,
            analysis.Status switch
            {
                "OK" => "Drivers: Windows no reporta dispositivos con códigos de problema.",
                "WARNING" => $"Drivers: {analysis.ProblemCount} dispositivos requieren revisión.",
                _ => "Drivers: no se pudo determinar el estado mediante WMI."
            },
            new { analysis.Status, analysis.ProblemCount, analysis.Problems });
    }

    private async Task<ActionResult> ActivationAsync()
    {
        var analysis = await activationService.AnalyzeAsync();
        healthState.SetActivation(analysis.Status);
        return new ActionResult(
            analysis.Status != "UNKNOWN",
            false,
            analysis.Status switch
            {
                "LICENSED" => "Activación: Windows informa una licencia activa.",
                "UNLICENSED" => "Activación: Windows informa estado no licenciado.",
                "GRACE_OR_NOTIFICATION" => "Activación: Windows informa un estado de gracia o notificación.",
                _ => "Activación: no se pudo determinar el estado de licencia."
            },
            new { analysis.Status, analysis.LicenseStatus, analysis.Description });
    }

    private ActionResult BrowserInventory()
    {
        var inventory = browserService.Analyze();
        return new ActionResult(
            true,
            false,
            $"Navegadores: {inventory.Browsers.Count} instalaciones o perfiles detectados.",
            new { browsers = inventory.Browsers });
    }

    private ActionResult BrowserExtensionHealth()
    {
        var health = browserExtensionHealthService.AnalyzeEdge();
        return new ActionResult(
            true,
            false,
            health.Status switch
            {
                "OK" => "Edge: no se detectaron extensiones desacopladas en los perfiles analizados.",
                "WARNING" => $"Edge: {health.DataOnlyCount + health.BrokenCount} anomalía(s) de extensión requieren revisión.",
                _ => "Edge: no se detectaron perfiles analizables."
            },
            new
            {
                health.Browser,
                health.Status,
                health.ProfilesScanned,
                health.DeveloperMode,
                health.InstalledCount,
                health.DeveloperLoadedCount,
                health.DataOnlyCount,
                health.BrokenCount,
                items = health.Items
            });
    }

    private async Task<ActionResult> MultimediaAsync()
    {
        var inventory = await multimediaService.AnalyzeAsync();
        return new ActionResult(
            true,
            false,
            $"Multimedia: {inventory.Devices.Count} dispositivos de audio/vídeo detectados.",
            new { devices = inventory.Devices });
    }

    private async Task<ActionResult> StartupAuditAsync() =>
        AuditResultToAction(
            "Inicio y servicios",
            await startupAuditService.AnalyzeAsync());

    private ActionResult WindowsUpdateAudit() =>
        AuditResultToAction(
            "Windows Update",
            windowsUpdateAuditService.Analyze());

    private ActionResult InstalledAppsInventory() =>
        AuditResultToAction(
            "Aplicaciones instaladas",
            installedAppsService.Analyze());

    private ActionResult PrivacyAudit() =>
        AuditResultToAction(
            "Privacidad",
            privacyAuditService.Analyze());

    private ActionResult DeveloperAudit() =>
        AuditResultToAction(
            "Developer tooling",
            developerToolingService.Analyze());

    private ActionResult ThermalAudit() =>
        AuditResultToAction(
            "Energía y temperaturas",
            thermalEnergyService.Analyze());

    private ActionResult BootAudit() =>
        AuditResultToAction(
            "Arranque",
            bootSleepAuditService.AnalyzeBoot());

    private ActionResult SleepResumeAudit() =>
        AuditResultToAction(
            "Suspensión/Reanudación",
            bootSleepAuditService.AnalyzeSleepResume());

    private ActionResult ExplorerAudit() =>
        AuditResultToAction(
            "Explorer",
            explorerAuditService.Analyze());

    private ActionResult BackupStatus(string operationId)
    {
        var status = operationRecoveryService.Analyze(operationId);
        return new ActionResult(
            true,
            false,
            $"Recovery: {status.IncompleteOperations.Count} operación(es) incompleta(s) y {status.RollbackSnapshots.Count} snapshot(s) de rollback.",
            new
            {
                incompleteOperations = status.IncompleteOperations,
                rollbackSnapshots = status.RollbackSnapshots,
                ecoQosTargets = status.EcoQosTargets
            });
    }

    private static ActionResult AuditResultToAction(
        string label,
        AuditResult result)
    {
        return new ActionResult(
            result.Status is "OK" or "NO_DATA",
            false,
            $"{label}: {result.Items.Count} elemento(s) observados. No se modificó Windows.",
            new
            {
                result.Status,
                items = result.Items
            });
    }
}
