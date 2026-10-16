using System.Text.Json;
using System.Diagnostics;
using System.IO;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace Win11PerformanceControlCenter.App.Core;

public sealed partial class ActionExecutor
{
    private async Task<ActionResult> StorageDuplicatesAsync()
    {
        var forensic = await new StorageForensicsService(
            TimeSpan.FromSeconds(6), 3000, 128L * 1024 * 1024)
            .ScanAsync(StorageForensicsService.DefaultRoots());
        return new ActionResult(true, false,
            forensic.Partial
                ? $"Muestreo parcial: {forensic.ScannedFiles} archivos; {forensic.Duplicates.Count} grupos id\u00E9nticos. No se elimin\u00F3 nada."
                : $"Escaneo completo de las carpetas seleccionadas: {forensic.Duplicates.Count} grupos id\u00E9nticos. No se elimin\u00F3 nada.",
            new
            {
                forensic.ScannedRoots,
                forensic.ScannedFiles,
                forensic.SkippedLinks,
                forensic.Errors,
                forensic.Partial,
                forensic.ElapsedMilliseconds,
                forensic.Duplicates,
                logicalPotentialBytes = forensic.Duplicates.Sum(d => d.ReclaimableLogicalBytes),
                physicallyRecoveredBytes = 0L,
                readOnly = true
            });
    }
    private async Task<ActionResult> StorageAsync(bool cleanupPreview)
    {
        var estimate = await storageService.AnalyzeSafeAsync();
        if (cleanupPreview) lastStorageCleanupPreview = DateTimeOffset.UtcNow;
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
        if (lastStorageCleanupPreview is not { } previewTime ||
            DateTimeOffset.UtcNow - previewTime > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException(
                "Previsualizacion previa requerida antes de enviar caches a la Papelera.");
        lastStorageCleanupPreview = null; // A preview is single use.
        var summary = await storageService.CleanupSafeAsync();
        return new ActionResult(summary.FailedFiles == 0, false,
            $"Limpieza: {summary.DeletedFiles} archivos enviados a Papelera, {summary.DeletedBytes / 1048576d:F1} MiB l\u00F3gicos trasladados (espacio libre no confirmado). " +
            $"Errores: {summary.FailedFiles}; categorías omitidas: {summary.SkippedCategories.Count}.",
            new
            {
                summary.DeletedBytes,
                summary.DeletedFiles,
                summary.FailedFiles,
                summary.SkippedCategories,
                recycleManifestPath = File.Exists(AppPaths.StorageRecycleManifest) ? AppPaths.StorageRecycleManifest : null,
                physicallyRecoveredBytes = (long?)null
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

    private async Task<ActionResult> StorageDirectoryTrendAsync()
    {
        var report = await new StorageDirectoryTrendService().ObserveAsync();
        return new ActionResult(true, false,
            report.BaselineCreated
                ? "Crecimiento por carpetas: baseline creado sin modificar las carpetas."
                : report.Partial
                    ? "Crecimiento por carpetas: muestreo parcial; no se comparan medidas incompletas."
                    : "Crecimiento por carpetas medido y comparado con el baseline previo.",
            new
            {
                report.CapturedAt,
                report.BaselineCreated,
                report.Partial,
                report.Items,
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

    private async Task<ActionResult> HibernateAsync(
        bool reduce,
        JsonElement? parameters)
    {
        if (reduce)
        {
            RequireConfirmed(parameters, "Confirma la reducción de hibernación.");
            var reduction = await systemRemediationService.ReduceHibernateAsync();
            var step = reduction.Steps.Single();
            return new ActionResult(
                reduction.Success,
                false,
                reduction.Success
                    ? "Hibernación reducida aplicada. Inicio rápido disponible; hibernación completa deshabilitada."
                    : "No se pudo reducir la hibernación.",
                new
                {
                    exitCode = step.ExitCode,
                    output = step.Output,
                    error = reduction.Success ? null : step.Output
                });
        }

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
        psi.ArgumentList.Add("/a");

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
}
