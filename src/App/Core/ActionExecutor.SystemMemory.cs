using System.Text.Json;
using System.Diagnostics;
using System.IO;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace Win11PerformanceControlCenter.App.Core;

public sealed partial class ActionExecutor
{
    private async Task<ActionResult> ReliabilityAsync()
    {
        var events = await reliabilityService.GetRecentAsync();
        return new ActionResult(
            true,
            false,
            $"Reliability Analyzer completado: {events.Count} eventos relevantes en la ventana analizada.",
            new { events });
    }

    private async Task<ActionResult> CrashIntelligenceAsync()
    {
        var report = await crashIntelligenceService.AnalyzeAsync();
        return new ActionResult(
            true,
            false,
            report.Insights.Count == 0
                ? "Crash Intelligence: no se agruparon patrones accionables en la ventana analizada."
                : $"Crash Intelligence: {report.Insights.Count} patrón(es), {report.HighSeverityCount} de prioridad alta.",
            report);
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

    private ActionResult ProcessHygieneAnalyze()
    {
        var report = processHygieneService.Analyze();
        return new ActionResult(
            true,
            true,
            report.StoppableCount == 0
                ? $"Process Hygiene: no se detectaron residuos detenibles; {report.ProtectedCount} proceso(s) sensibles quedaron protegidos."
                : $"Process Hygiene: {report.StoppableCount} residuo(s) detenible(s), ~{report.EstimatedReclaimMb:F1} MiB potenciales. No se detuvo nada.",
            report);
    }

    private async Task<ActionResult> ProcessHygieneStopAsync(
        JsonElement? parameters)
    {
        RequireConfirmed(
            parameters,
            "Confirma la detención del residuo de laboratorio seleccionado.");
        var processId = GetRequiredInt32(
            parameters,
            "processId");
        var fingerprint = GetRequiredString(
            parameters,
            "fingerprint");

        var result = await processHygieneService.StopAsync(
            processId,
            fingerprint);

        return new ActionResult(
            result.Success,
            false,
            result.Success
                ? $"Process Hygiene: {result.Identity} detenido y verificado (~{result.EstimatedReclaimMb:F1} MiB observados en la previsualización)."
                : $"Process Hygiene: la detención de {result.Identity} no pudo verificarse ({result.Status}).",
            result);
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

}
