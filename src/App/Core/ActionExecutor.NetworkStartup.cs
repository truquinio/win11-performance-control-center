using System.Text.Json;
using System.Diagnostics;
using System.IO;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace Win11PerformanceControlCenter.App.Core;

public sealed partial class ActionExecutor
{
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

    private async Task<ActionResult> ServiceDependenciesAsync(
        JsonElement? parameters)
    {
        var serviceName = GetRequiredString(
            parameters,
            "serviceName");
        var report =
            await serviceStartupRemediationService
                .AnalyzeDependenciesAsync(serviceName);

        return new ActionResult(
            true,
            false,
            report.DependentCount == 0
                ? $"Servicio {report.ServiceName}: no se detectaron servicios dependientes."
                : $"Servicio {report.ServiceName}: {report.DependentCount} servicio(s) dependiente(s); los cambios quedan protegidos.",
            report);
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

    private async Task<ActionResult> StartupEntriesPreviewAsync()
    {
        var preview = await startupEntryRemediationService.PreviewAsync();
        return new ActionResult(
            true,
            true,
            $"Autoarranque Run/RunOnce: {preview.EligibleCount} revisable(s), {preview.ProtectedCount} protegido(s), {preview.DisabledByAppCount} con rollback.",
            new
            {
                preview.RegisteredCount,
                preview.EligibleCount,
                preview.ProtectedCount,
                preview.DisabledByAppCount,
                entries = preview.Entries,
                note = "Deshabilitar una entrada impide su próximo autoarranque; no fuerza el cierre del proceso actual."
            });
    }

    private async Task<ActionResult> StartupEntryDisableAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma la desactivación de la entrada de autoarranque.");
        var entryId = GetRequiredString(parameters, "entryId");
        var result = await startupEntryRemediationService.DisableAsync(
            entryId);
        return new ActionResult(
            result.Success,
            false,
            result.Success
                ? $"Autoarranque {result.Name}: deshabilitado con rollback disponible. El proceso actual no fue cerrado."
                : $"Autoarranque {result.Name}: no se pudo verificar la desactivación.",
            result);
    }

    private async Task<ActionResult> StartupEntryRestoreAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma el rollback de la entrada de autoarranque.");
        var entryId = GetRequiredString(parameters, "entryId");
        var result = await startupEntryRemediationService.RestoreAsync(
            entryId);
        return new ActionResult(
            result.Success,
            false,
            result.Status == "NO_SNAPSHOT"
                ? "Autoarranque: no existe snapshot para esa entrada."
                : result.Success
                    ? $"Autoarranque {result.Name}: restaurado y verificado."
                    : $"Autoarranque {result.Name}: rollback no verificado.",
            result);
    }

    private async Task<ActionResult> ScheduledTasksPreviewAsync()
    {
        var preview =
            await scheduledTaskRemediationService.PreviewAsync();
        return new ActionResult(
            true,
            true,
            $"Tareas programadas: {preview.EligibleCount} revisable(s), " +
            $"{preview.ProtectedCount} protegida(s), " +
            $"{preview.RestoreAvailableCount} con rollback.",
            new
            {
                preview.TaskCount,
                preview.EligibleCount,
                preview.ProtectedCount,
                preview.RestoreAvailableCount,
                tasks = preview.Tasks,
                note = "Deshabilitar una tarea afecta futuras ejecuciones; no se termina ningún proceso actual."
            });
    }

    private async Task<ActionResult> ScheduledTaskDisableAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma la desactivación de la tarea programada.");
        var entryId = GetRequiredString(parameters, "entryId");
        var result =
            await scheduledTaskRemediationService.DisableAsync(
                entryId);

        return new ActionResult(
            result.Success,
            false,
            result.Success
                ? $"Tarea {result.FullName}: deshabilitada y verificada; rollback disponible."
                : $"Tarea {result.FullName}: no se pudo verificar la desactivación.",
            result);
    }

    private async Task<ActionResult> ScheduledTaskRestoreAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma el rollback de la tarea programada.");
        var entryId = GetRequiredString(parameters, "entryId");
        var result =
            await scheduledTaskRemediationService.RestoreAsync(
                entryId);

        return new ActionResult(
            result.Success,
            false,
            result.Status == "NO_SNAPSHOT"
                ? "Tarea programada: no existe snapshot guardado."
                : result.Success
                    ? $"Tarea {result.FullName}: restaurada y verificada."
                    : $"Tarea {result.FullName}: rollback no verificado ({result.Status}).",
            result);
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

    private static int GetRequiredInt32(
        JsonElement? parameters,
        string name)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } args ||
            !args.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result) ||
            result <= 0)
        {
            throw new InvalidOperationException(
                "Falta parámetro entero positivo: " + name);
        }

        return result;
    }

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

}
