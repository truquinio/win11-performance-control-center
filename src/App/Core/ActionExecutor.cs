using System.Text.Json;
using System.Diagnostics;
using System.IO;
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
    StorageWatchService storageWatchService,
    ServiceStartupRemediationService serviceStartupRemediationService,
    SystemRemediationService systemRemediationService,
    PowerPlanTuningService powerPlanTuningService,
    PageFileTuningService pageFileTuningService,
    ProcessAnalysisService processService,
    ProcessTuningService processTuningService,
    PageFileService pageFileService,
    IntegrityService integrityService,
    DriverService driverService,
    ActivationService activationService,
    BrowserInventoryService browserService,
    BrowserExtensionHealthService browserExtensionHealthService,
    BrowserExtensionRemediationService browserExtensionRemediationService,
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
    RollbackCenterService rollbackCenterService,
    ActionPlanService actionPlanService,
    WorkloadGuardService workloadGuardService,
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

        if (workloadGuardService.ShouldBlock(id))
        {
            var guard = workloadGuardService.GetStatus();
            await logger.TryWriteAsync(id, "REJECTED", new
            {
                reason = "WORKLOAD_GUARD",
                guard.ConfiguredMode,
                guard.EffectiveMode,
                guard.IdleSeconds,
                guard.DFreePercent,
                guard.HeavyActionsAllowed
            });
            return new ActionResult(
                false,
                false,
                "Acción diferida por modo PC en uso. Cambia a MANTENIMIENTO cuando quieras permitir tareas pesadas o disruptivas.",
                new
                {
                    blocked = true,
                    workload = guard
                });
        }

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
                result = await elevatedActionClient.ExecuteAsync(
                    action,
                    parameters);
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
                    node.ValueKind == JsonValueKind.String &&
                    node.GetString() is { Length: > 0 and <= 256 },
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
            "system.actionplan.preview" => await ActionPlanAsync(operationId),
            "system.workload.status" => await Task.Run(WorkloadStatus),
            "system.workload.inuse" => await Task.Run(() => SetWorkloadMode("IN_USE", parameters)),
            "system.workload.auto" => await Task.Run(() => SetWorkloadMode("AUTO", parameters)),
            "system.workload.maintenance" => await Task.Run(() => SetWorkloadMode("MAINTENANCE", parameters)),
            "system.health.scan" => await HealthScanAsync(),
            "system.integrity.check" => await IntegrityCheckAsync(),
            "system.integrity.repair" => await IntegrityRepairAsync(parameters),
            "system.reliability.analyze" => await ReliabilityAsync(),
            "memory.analyze" => await MemoryAsync(),
            "memory.trim.preview" => await Task.Run(MemoryTrimPreview),
            "memory.trim" => await MemoryTrimAsync(parameters),
            "memory.pagefile.analyze" => await PageFileAsync(),
            "memory.pagefile.capped" => await PageFileCappedAsync(parameters),
            "memory.pagefile.restore" => await PageFileRestoreAsync(parameters),
            "cpu.ecoqos.analyze" => await Task.Run(EcoQoSAnalyze),
            "cpu.ecoqos.apply" => await EcoQoSApplyAsync(parameters),
            "cpu.ecoqos.restore" => await EcoQoSRestoreAsync(parameters),
            "disk.scan" => await StorageAsync(false),
            "disk.cleanup.safe" => await StorageAsync(true),
            "disk.cleanup.execute" => await StorageCleanupAsync(parameters),
            "disk.appdata.rank" => await AppDataRankAsync(),
            "disk.volumes.audit" => await StorageVolumesAuditAsync(),
            "disk.storage.watch" => await StorageWatchAsync(),
            "disk.hotspots.scan" => await StorageHotspotsAsync(),
            "disk.hibernate.status" => await HibernateAsync(false, null),
            "disk.hibernate.reduce" => await HibernateAsync(true, parameters),
            "network.test" => await NetworkAsync(),
            "network.flushdns" => await NetworkFlushDnsAsync(parameters),
            "network.winsock.reset" => await NetworkWinsockResetAsync(parameters),
            "drivers.analyze" => await DriversAsync(),
            "drivers.rescan" => await DriversRescanAsync(parameters),
            "system.activation.analyze" => await ActivationAsync(),
            "browsers.inventory" => await Task.Run(BrowserInventory),
            "browsers.extensions.health" => await Task.Run(BrowserExtensionHealth),
            "browsers.extensions.orphans.preview" => await Task.Run(BrowserExtensionOrphansPreview),
            "browsers.extensions.orphans.quarantine" => await BrowserExtensionOrphansQuarantineAsync(parameters),
            "browsers.extensions.orphans.restore" => await BrowserExtensionOrphansRestoreAsync(parameters),
            "multimedia.inventory" => await MultimediaAsync(),
            "multimedia.audio.restart" => await AudioRestartAsync(parameters),
            "startup.audit" => await StartupAuditAsync(),
            "startup.services.preview" => await ServicesPreviewAsync(),
            "startup.service.setmode" => await ServiceSetModeAsync(parameters),
            "startup.service.restore" => await ServiceRestoreAsync(parameters),
            "windows.update.audit" => await Task.Run(WindowsUpdateAudit),
            "windows.update.services.restart" => await WindowsUpdateServicesRestartAsync(parameters),
            "apps.inventory" => await Task.Run(InstalledAppsInventory),
            "privacy.audit" => await Task.Run(PrivacyAudit),
            "developer.audit" => await Task.Run(DeveloperAudit),
            "lab.reliability.status" => await Task.Run(ReliabilityLabStatus),
            "thermal.audit" => await Task.Run(ThermalAudit),
            "thermal.power.balanced" => await PowerPlanBalancedAsync(parameters),
            "thermal.power.performance" => await PowerPlanPerformanceAsync(parameters),
            "thermal.power.restore" => await PowerPlanRestoreAsync(parameters),
            "boot.audit" => await Task.Run(BootAudit),
            "sleepresume.audit" => await Task.Run(SleepResumeAudit),
            "explorer.audit" => await Task.Run(ExplorerAudit),
            "explorer.restart" => await ExplorerRestartAsync(parameters),
            "backup.status" => await Task.Run(() => BackupStatus(operationId)),
            "backup.rollback.center" => await Task.Run(() => RollbackCenter(operationId)),
            _ => throw new InvalidOperationException("Action ID no implementada.")
        };
    }

    private async Task<ActionResult> ActionPlanAsync(
        string operationId)
    {
        var report = await actionPlanService.BuildAsync(operationId);
        return new ActionResult(
            true,
            false,
            report.Items.Count == 0
                ? "Plan de acción: no se detectaron tareas prioritarias con las señales rápidas disponibles."
                : $"Plan de acción: {report.ActionableCount} acción(es) sugeridas; {report.DeferredCount} diferida(s) por modo de uso.",
            report);
    }

    private ActionResult WorkloadStatus()
    {
        var status = workloadGuardService.GetStatus();
        return new ActionResult(
            true,
            false,
            status.HeavyActionsAllowed
                ? $"Modo {status.EffectiveMode}: mantenimiento pesado permitido."
                : $"Modo {status.EffectiveMode}: mantenimiento pesado protegido.",
            status);
    }

    private ActionResult SetWorkloadMode(
        string mode,
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma el cambio de modo de mantenimiento.");
        var status = workloadGuardService.SetMode(mode);
        return new ActionResult(
            true,
            false,
            $"Modo de mantenimiento configurado como {status.ConfiguredMode}. {status.Reason}",
            status);
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

    private async Task<ActionResult> IntegrityRepairAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma la reparación DISM + SFC.");
        var repair = await systemRemediationService.RepairIntegrityAsync();
        var verification = await integrityService.CheckHealthAsync();
        healthState.SetIntegrity(verification.Status);

        var success = repair.Success && verification.Status == "OK";
        return new ActionResult(
            success,
            false,
            success
                ? "Integridad reparada y verificada: DISM + SFC completados y CheckHealth informa OK."
                : $"Reparación de integridad finalizada con estado {repair.Status}; verificación: {verification.Status}.",
            new
            {
                repair.Status,
                repair.RebootRequired,
                steps = repair.Steps,
                verification = new
                {
                    verification.Status,
                    verification.ExitCode,
                    durationMs =
                        (long)verification.Duration.TotalMilliseconds
                }
            });
    }

    private async Task<ActionResult> NetworkFlushDnsAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el vaciado de caché DNS.");
        var result = await systemRemediationService.FlushDnsAsync();
        return RemediationActionResult(
            result,
            result.Success
                ? "Red: caché DNS vaciada correctamente."
                : "Red: no se pudo vaciar la caché DNS.");
    }

    private async Task<ActionResult> NetworkWinsockResetAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el reset de Winsock.");
        var result = await systemRemediationService.ResetWinsockAsync();
        return RemediationActionResult(
            result,
            result.Success
                ? "Winsock restablecido. Reinicia Windows para completar el cambio."
                : "Winsock no pudo restablecerse.");
    }

    private async Task<ActionResult> DriversRescanAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el reescaneo Plug and Play.");
        var result = await systemRemediationService.RescanDevicesAsync();
        var verification = await driverService.AnalyzeAsync();
        healthState.SetDrivers(verification.Status);
        return new ActionResult(
            result.Success,
            false,
            result.Success
                ? verification.ProblemCount == 0
                    ? "Hardware reescaneado. Windows ya no reporta dispositivos con código de problema."
                    : $"Hardware reescaneado; siguen apareciendo {verification.ProblemCount} dispositivo(s) con problema."
                : "El reescaneo de hardware falló.",
            new
            {
                result.Status,
                result.RebootRequired,
                steps = result.Steps,
                remainingProblems = verification.ProblemCount,
                verificationStatus = verification.Status,
                verification.Problems
            });
    }

    private async Task<ActionResult> AudioRestartAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el reinicio de Windows Audio.");
        var result = await systemRemediationService.RestartAudioAsync();
        var verification = await multimediaService.AnalyzeAsync();
        return new ActionResult(
            result.Success,
            false,
            result.Success
                ? "Windows Audio reiniciado y el inventario multimedia volvió a responder."
                : "No se pudo reiniciar Windows Audio.",
            new
            {
                result.Status,
                steps = result.Steps,
                devices = verification.Devices
            });
    }

    private async Task<ActionResult> ServicesPreviewAsync()
    {
        var preview = await serviceStartupRemediationService.PreviewAsync();
        return new ActionResult(
            true,
            true,
            $"Servicios automáticos: {preview.EligibleCount} revisable(s), {preview.ProtectedCount} protegido(s). No se modificó ninguno.",
            new
            {
                preview.AutomaticCount,
                preview.EligibleCount,
                preview.ProtectedCount,
                services = preview.Services,
                note = "Cambiar a Manual no detiene el servicio actual; afecta el próximo arranque. Disabled debe usarse solo con conocimiento del servicio."
            });
    }

    private async Task<ActionResult> ServiceSetModeAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el cambio de inicio del servicio.");
        var serviceName = GetRequiredString(parameters, "serviceName");
        var targetMode = GetRequiredString(parameters, "targetMode");
        var result = await serviceStartupRemediationService.ChangeModeAsync(
            serviceName,
            targetMode);

        return new ActionResult(
            result.Success,
            false,
            result.Success
                ? $"Servicio {result.ServiceName}: inicio {result.BeforeMode} → {result.AfterMode}. El proceso actual no fue forzado a detenerse."
                : $"Servicio {result.ServiceName}: no se pudo verificar el nuevo modo.",
            new
            {
                result.ServiceName,
                result.BeforeMode,
                result.AfterMode,
                result.CurrentState,
                result.RestoreAvailable,
                result.Status
            });
    }

    private async Task<ActionResult> ServiceRestoreAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el rollback del servicio.");
        var serviceName = GetRequiredString(parameters, "serviceName");
        var result = await serviceStartupRemediationService.RestoreAsync(
            serviceName);

        return new ActionResult(
            result.Success,
            false,
            result.Status == "NO_SNAPSHOT"
                ? $"Servicio {serviceName}: no hay snapshot de inicio guardado."
                : result.Success
                    ? $"Servicio {serviceName}: modo de inicio restaurado a {result.AfterMode}."
                    : $"Servicio {serviceName}: rollback no verificado.",
            new
            {
                result.ServiceName,
                result.BeforeMode,
                result.AfterMode,
                result.CurrentState,
                result.RestoreAvailable,
                result.Status
            });
    }

    private async Task<ActionResult> WindowsUpdateServicesRestartAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma el reinicio de BITS y Windows Update.");
        var result =
            await systemRemediationService.RestartWindowsUpdateServicesAsync();
        var verification = windowsUpdateAuditService.Analyze();
        return new ActionResult(
            result.Success,
            false,
            result.Success
                ? "Servicios de Windows Update reiniciados. No se borró SoftwareDistribution ni el historial."
                : "El reinicio de servicios de Windows Update quedó incompleto.",
            new
            {
                result.Status,
                steps = result.Steps,
                recentEvents = verification.Items.Take(10).ToArray()
            });
    }

    private async Task<ActionResult> PowerPlanBalancedAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el cambio al plan Equilibrado.");
        var result = await powerPlanTuningService.SetBalancedAsync();
        return PowerPlanActionResult(result, "Equilibrado");
    }

    private async Task<ActionResult> PowerPlanPerformanceAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el cambio a Alto rendimiento.");
        var result = await powerPlanTuningService.SetPerformanceAsync();
        return PowerPlanActionResult(result, "Alto rendimiento");
    }

    private async Task<ActionResult> PowerPlanRestoreAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el rollback del plan de energía.");
        var result = await powerPlanTuningService.RestoreAsync();
        return PowerPlanActionResult(result, "plan anterior");
    }

    private async Task<ActionResult> ExplorerRestartAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters, "Confirma el reinicio de Explorer.");
        var result = await systemRemediationService.RestartExplorerAsync();
        var verification = explorerAuditService.Analyze();
        return new ActionResult(
            result.Success,
            false,
            result.Success
                ? "Explorer reiniciado y verificado."
                : "Explorer no pudo reiniciarse o verificarse.",
            new
            {
                result.Status,
                verificationStatus = verification.Status,
                items = verification.Items
            });
    }

    private static ActionResult RemediationActionResult(
        SystemRemediationResult result,
        string message) =>
        new(
            result.Success,
            false,
            message,
            new
            {
                result.Status,
                result.RebootRequired,
                steps = result.Steps
            });

    private static ActionResult PowerPlanActionResult(
        PowerPlanChangeResult result,
        string label) =>
        new(
            result.Success,
            false,
            result.Status == "NO_SNAPSHOT"
                ? "No hay un plan anterior guardado por la app para restaurar."
                : result.Success
                    ? $"Plan de energía aplicado/restaurado: {label}."
                    : $"No se pudo verificar el cambio de energía ({result.Status}).",
            new
            {
                result.Status,
                result.BeforeGuid,
                result.AfterGuid,
                result.RestoreAvailable
            });

    private static string GetRequiredString(
        JsonElement? parameters,
        string name)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } args ||
            !args.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                "Falta parámetro obligatorio: " + name);
        }

        return value.GetString()!;
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

    private async Task<ActionResult> PageFileCappedAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma el perfil C: 512 MB + D: 4–8 GB.");
        var result = await pageFileTuningService.ApplyCappedProfileAsync();
        var verification = await pageFileService.AnalyzeAsync();

        return new ActionResult(
            result.Success,
            false,
            result.Status == "ALREADY_CAPPED"
                ? "Pagefile: ya está en el perfil recomendado C: 512 MB + D: 4–8 GB. No se cambió nada."
                : result.Success
                    ? "Pagefile configurado: C: 512 MB + D: 4 GB inicial / 8 GB máximo. Reinicia para aplicar completamente."
                    : $"Pagefile: no se pudo verificar el perfil limitado ({result.Status}).",
            new
            {
                result.Status,
                result.BeforeAutomatic,
                result.AfterAutomatic,
                result.RestoreAvailable,
                result.RebootRequired,
                verification.AutomaticallyManaged,
                entries = verification.Entries
            });
    }

    private async Task<ActionResult> PageFileRestoreAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma el rollback del pagefile.");
        var result = await pageFileTuningService.RestoreAsync();
        var verification = await pageFileService.AnalyzeAsync();

        return new ActionResult(
            result.Success,
            false,
            result.Status == "NO_SNAPSHOT"
                ? "Pagefile: no hay configuración previa guardada por la app."
                : result.Success
                    ? "Pagefile: configuración previa restaurada. Reinicia para completar el rollback."
                    : $"Pagefile: rollback no verificado ({result.Status}).",
            new
            {
                result.Status,
                result.BeforeAutomatic,
                result.AfterAutomatic,
                result.RestoreAvailable,
                result.RebootRequired,
                verification.AutomaticallyManaged,
                entries = verification.Entries
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
        var rank = await storageService.RankLocalAsync();
        var scope = rank.Complete
            ? "completo"
            : "parcial (límite de tiempo alcanzado)";
        return new ActionResult(true, false,
            $"Ranking {scope} de AppData\\Local: {rank.Folders.Count} carpetas mostradas en {rank.ElapsedMilliseconds} ms.",
            new
            {
                folders = rank.Folders,
                rank.Complete,
                rank.DiscoveredFolders,
                rank.ElapsedMilliseconds,
                readOnly = true
            });
    }

    private async Task<ActionResult> StorageVolumesAuditAsync()
    {
        var audit = await storageWatchService.AuditAsync();
        return new ActionResult(
            true,
            false,
            audit.WarningCount == 0
                ? $"Volúmenes: {audit.Volumes.Count} unidades fijas sin presión de espacio."
                : $"Volúmenes: {audit.WarningCount} unidad(es) requieren atención por espacio libre.",
            new
            {
                audit.CapturedAt,
                audit.WarningCount,
                volumes = audit.Volumes,
                readOnly = true
            });
    }

    private async Task<ActionResult> StorageWatchAsync()
    {
        var report = await storageWatchService.WatchAsync();
        return new ActionResult(
            true,
            false,
            report.BaselineCreated
                ? $"Storage Watch: baseline creado para {report.Volumes.Count} volumen(es)."
                : report.AlertCount == 0
                    ? $"Storage Watch: sin crecimiento anormal en {report.Volumes.Count} volumen(es)."
                    : $"Storage Watch: {report.AlertCount} alerta(s) de espacio o crecimiento.",
            new
            {
                report.CapturedAt,
                report.PreviousCapturedAt,
                report.BaselineCreated,
                report.AlertCount,
                volumes = report.Volumes,
                stateIsAppLocalOnly = true
            });
    }

    private async Task<ActionResult> StorageHotspotsAsync()
    {
        var report = await storageWatchService.ScanLowSpaceHotspotsAsync();
        return new ActionResult(
            true,
            false,
            report.Volumes.Count == 0
                ? "Hotspots: ninguna unidad fija está por debajo del umbral de vigilancia (15%)."
                : report.Partial
                    ? $"Hotspots: escaneo parcial y acotado en {report.Volumes.Count} unidad(es); no se borró nada."
                    : $"Hotspots: escaneo completado en {report.Volumes.Count} unidad(es); no se borró nada.",
            new
            {
                report.CapturedAt,
                report.BudgetSeconds,
                report.ElapsedMilliseconds,
                report.Partial,
                volumes = report.Volumes,
                readOnly = true
            });
    }

    private static async Task<ActionResult> HibernateAsync(
        bool reduce,
        JsonElement? parameters)
    {
        if (reduce)
            RequireConfirmed(parameters, "Confirma la reducción de hibernación.");

        // The elevated runner validates administrative privileges for writes.
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "powercfg.exe"),
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
        var orphanBytes = health.Items
            .Where(item => item.Status == "DATA_WITHOUT_INSTALLATION")
            .Sum(item => item.DataBytes);

        var message = health.Status switch
        {
            "NO_DATA" => "Edge: no se detectaron perfiles analizables.",
            _ when health.BrokenCount > 0 =>
                $"Edge: {health.BrokenCount} extensión(es) tienen una instalación incompleta o código ausente. " +
                $"Además hay {health.DataOnlyCount} residuo(s) de extensiones ya desinstaladas.",
            _ when health.DataOnlyCount > 0 =>
                $"Edge: las extensiones instaladas están íntegras. Hay {health.DataOnlyCount} residuo(s) de extensiones ya desinstaladas " +
                $"({orphanBytes / 1048576d:F1} MiB). No están ejecutándose; limpiarlos es opcional.",
            _ => "Edge: las extensiones instaladas están íntegras y no se detectaron residuos relevantes."
        };

        return new ActionResult(
            true,
            false,
            message,
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
                orphanBytes,
                recommendedNextStep = health.BrokenCount > 0
                    ? "Revisa las extensiones rotas antes de limpiar residuos. La app no eliminará automáticamente una instalación dañada."
                    : health.DataOnlyCount > 0
                        ? "Puedes dejar los residuos sin riesgo funcional o previsualizar una cuarentena reversible para recuperar espacio."
                        : "No necesitas hacer nada.",
                items = health.Items
            });
    }

    private ActionResult BrowserExtensionOrphansPreview()
    {
        var preview = browserExtensionRemediationService.Preview();
        return new ActionResult(
            true,
            true,
            preview.CandidateCount == 0
                ? "Edge: no hay residuos de extensiones desinstaladas para limpiar."
                : preview.EdgeRunning
                    ? $"Edge: {preview.CandidateCount} residuo(s), {preview.TotalBytes / 1048576d:F1} MiB. Cierra Edge para habilitar la cuarentena reversible."
                    : $"Edge: {preview.CandidateCount} residuo(s), {preview.TotalBytes / 1048576d:F1} MiB. Puedes ponerlos en cuarentena de forma reversible.",
            new
            {
                preview.CandidateCount,
                preview.TotalBytes,
                preview.EdgeRunning,
                preview.RestoreAvailable,
                candidates = preview.Candidates,
                scope = "Solo Local Extension Settings de extensiones que ya no están instaladas.",
                reversible = true
            });
    }

    private async Task<ActionResult> BrowserExtensionOrphansQuarantineAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters,
            "Confirma la cuarentena después de revisar los residuos de Edge.");
        var result = await browserExtensionRemediationService.QuarantineAsync();

        return new ActionResult(
            result.Success,
            false,
            result.Status switch
            {
                "EDGE_RUNNING" => "Edge está abierto. Ciérralo antes de poner residuos en cuarentena.",
                "NOTHING_TO_DO" => "No hay residuos de extensiones para poner en cuarentena.",
                "QUARANTINED" => $"Edge: {result.MovedCount} residuo(s) movidos a cuarentena ({result.MovedBytes / 1048576d:F1} MiB). Rollback disponible.",
                _ => $"Edge: la cuarentena quedó incompleta. Movidos: {result.MovedCount}; omitidos: {result.SkippedCount}."
            },
            new
            {
                result.Status,
                result.MovedCount,
                result.MovedBytes,
                result.SkippedCount,
                result.BatchId,
                result.Skipped,
                rollbackAvailable = result.MovedCount > 0
            });
    }

    private async Task<ActionResult> BrowserExtensionOrphansRestoreAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(parameters,
            "Confirma el rollback de la última cuarentena de Edge.");
        var result = await browserExtensionRemediationService.RestoreLatestAsync();

        return new ActionResult(
            result.Success,
            false,
            result.Status switch
            {
                "EDGE_RUNNING" => "Edge está abierto. Ciérralo antes de restaurar la cuarentena.",
                "NO_QUARANTINE" => "No existe una cuarentena de extensiones Edge para restaurar.",
                "RESTORED" => $"Edge: {result.RestoredCount} residuo(s) restaurados ({result.RestoredBytes / 1048576d:F1} MiB).",
                _ => $"Edge: rollback incompleto. Restaurados: {result.RestoredCount}; incidencias: {result.Skipped.Count}."
            },
            new
            {
                result.Status,
                result.RestoredCount,
                result.RestoredBytes,
                result.BatchId,
                result.Skipped
            });
    }

    private static void RequireConfirmed(
        JsonElement? parameters,
        string message)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } args ||
            !args.TryGetProperty("confirmed", out var flag) ||
            flag.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException(message);
        }
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

    private static ActionResult ReliabilityLabStatus()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "evals",
            "regressions");
        var required = new[]
        {
            "claude-mcp-survives-cleanup",
            "running-app-skips-cache",
            "recent-vs-old-cache"
        };

        var scenarios = new List<object>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidJsonCount = 0;

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    var root = document.RootElement;
                    var id = root.TryGetProperty("id", out var idNode)
                        ? idNode.GetString()
                        : null;
                    var kind = root.TryGetProperty("kind", out var kindNode)
                        ? kindNode.GetString()
                        : null;
                    var description = root.TryGetProperty("description", out var descriptionNode)
                        ? descriptionNode.GetString()
                        : null;

                    if (!string.IsNullOrWhiteSpace(id))
                        ids.Add(id);

                    scenarios.Add(new
                    {
                        id = id ?? Path.GetFileNameWithoutExtension(file),
                        kind = kind ?? "UNKNOWN",
                        description = description ?? "Sin descripción",
                        file = Path.GetFileName(file)
                    });
                }
                catch (JsonException)
                {
                    invalidJsonCount++;
                    scenarios.Add(new
                    {
                        id = Path.GetFileNameWithoutExtension(file),
                        kind = "INVALID_JSON",
                        description = "Manifest inválido",
                        file = Path.GetFileName(file)
                    });
                }
            }
        }

        var missing = required
            .Where(id => !ids.Contains(id))
            .ToArray();

        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var guardChecks = new[]
        {
            new
            {
                name = "Claude Extensions",
                protectedPath = StorageAnalysisService.IsProtectedPath(
                    Path.Combine(local, "Claude", "Claude Extensions"))
            },
            new
            {
                name = ".venv",
                protectedPath = StorageAnalysisService.IsProtectedPath(
                    Path.Combine(local, "Synthetic", ".venv"))
            },
            new
            {
                name = "WhatsApp",
                protectedPath = StorageAnalysisService.IsProtectedPath(
                    Path.Combine(local, "Synthetic", "WhatsApp"))
            }
        };

        var broadTarget = new StorageAnalysisService.CacheTarget(
            "future.broad-cleanup",
            "Target amplio desconocido",
            local);
        var broadTargetBlocked =
            !StorageAnalysisService.IsProductionApprovedTarget(
                broadTarget,
                local);

        var allGuardsPass =
            guardChecks.All(check => check.protectedPath) &&
            broadTargetBlocked;
        var manifestsValid =
            scenarios.Count >= required.Length &&
            missing.Length == 0 &&
            invalidJsonCount == 0;
        var success = allGuardsPass && manifestsValid;

        return new ActionResult(
            success,
            false,
            success
                ? $"Reliability Lab OK: {scenarios.Count} regresiones empaquetadas y barreras críticas activas."
                : $"Reliability Lab WARNING: faltan {missing.Length} regresiones o alguna barrera crítica no está activa.",
            new
            {
                status = success ? "OK" : "WARNING",
                regressionScenarioCount = scenarios.Count,
                requiredScenarioCount = required.Length,
                invalidManifestCount = invalidJsonCount,
                missingScenarios = missing,
                runtimeGuards = guardChecks,
                broadUnknownTargetBlocked = broadTargetBlocked,
                scenarios,
                destructiveEvalsRunHere = false,
                note = "Los evals destructivos se ejecutan únicamente sobre fixtures/CI, Sandbox o VM disposable."
            });
    }

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

    private ActionResult RollbackCenter(
        string operationId)
    {
        var report = rollbackCenterService.Analyze(operationId);
        return new ActionResult(
            true,
            false,
            report.Entries.Count == 0
                ? "Rollback Center: no hay cambios reversibles pendientes."
                : $"Rollback Center: {report.Entries.Count} cambio(s) con rollback disponible.",
            report);
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
