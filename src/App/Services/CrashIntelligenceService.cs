using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class CrashIntelligenceService
{
    private readonly Func<int, Task<IReadOnlyList<ReliabilityEventDto>>> reader;

    public CrashIntelligenceService(ReliabilityService reliability)
        : this(reliability.GetRecentAsync)
    {
    }

    internal CrashIntelligenceService(
        Func<int, Task<IReadOnlyList<ReliabilityEventDto>>> reader)
    {
        this.reader = reader;
    }

    public async Task<CrashIntelligenceReport> AnalyzeAsync()
    {
        var events = await reader(100);
        var groups = events
            .GroupBy(item => Classify(item))
            .Where(group => group.Key is not null)
            .Select(group =>
            {
                var key = group.Key!;
                var ordered = group
                    .OrderByDescending(item => item.Time)
                    .ToArray();
                return new CrashInsight(
                    key.Category,
                    key.Severity,
                    key.Title,
                    ordered.Length,
                    ordered[0].Time,
                    key.RecommendedActionId,
                    key.Rationale,
                    ordered[0].Message);
            })
            .OrderBy(item => SeverityRank(item.Severity))
            .ThenByDescending(item => item.Occurrences)
            .ThenByDescending(item => item.LatestAt)
            .Take(24)
            .ToArray();

        return new CrashIntelligenceReport(
            events.Count,
            groups,
            groups.Count(item => item.Severity == "HIGH"),
            groups.Count(item => item.Severity == "MEDIUM"));
    }

    private static InsightKey? Classify(
        ReliabilityEventDto item)
    {
        var provider = item.Provider;
        var message = item.Message;

        if (provider.Equals(
                "Microsoft-Windows-WHEA-Logger",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "Hardware",
                "HIGH",
                "Eventos WHEA",
                "drivers.analyze",
                "Windows registró evidencia WHEA; conviene revisar hardware/drivers antes de atribuir la causa.");
        }

        if ((provider.Equals(
                    "Application Error",
                    StringComparison.OrdinalIgnoreCase) ||
                provider.Equals(
                    "Windows Error Reporting",
                    StringComparison.OrdinalIgnoreCase)) &&
            (message.Contains(
                    "qemu-system",
                    StringComparison.OrdinalIgnoreCase) ||
                message.Contains(
                    "Android Emulator",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new(
                "Desarrollo Android",
                "MEDIUM",
                "Android Emulator / QEMU crash",
                "memory.analyze",
                "Windows registró un fallo del emulador/QEMU. Se recomienda revisar presión de memoria y recursos antes de modificar el AVD; el evento por sí solo no demuestra una causa raíz.");
        }

        if (provider.Equals(
                "Application Error",
                StringComparison.OrdinalIgnoreCase) &&
            item.Id == 1000)
        {
            var action = message.Contains(
                    "msedge",
                    StringComparison.OrdinalIgnoreCase)
                ? "browsers.extensions.health"
                : "memory.analyze";
            return new(
                "Aplicaciones",
                "MEDIUM",
                "Fallos de aplicación",
                action,
                "Windows registró cierres por error. La acción sugerida busca contexto; no asume automáticamente la causa.");
        }

        if (provider.Equals(
                "Application Hang",
                StringComparison.OrdinalIgnoreCase) &&
            item.Id == 1002)
        {
            var action = message.Contains(
                    "explorer",
                    StringComparison.OrdinalIgnoreCase)
                ? "explorer.audit"
                : "memory.analyze";
            return new(
                "Aplicaciones",
                "MEDIUM",
                "Aplicaciones sin responder",
                action,
                "Los hangs suelen requerir correlacionar memoria/proceso antes de reparar.");
        }

        if ((provider.Equals(
                    "Microsoft-Windows-Kernel-Power",
                    StringComparison.OrdinalIgnoreCase) &&
                item.Id == 41) ||
            (provider.Equals(
                    "EventLog",
                    StringComparison.OrdinalIgnoreCase) &&
                item.Id == 6008))
        {
            return new(
                "Estabilidad",
                "HIGH",
                "Apagados o reinicios no limpios",
                "system.health.scan",
                "El evento confirma un cierre no limpio, pero no identifica por sí solo la causa.");
        }

        if (provider.Equals(
                "Windows Error Reporting",
                StringComparison.OrdinalIgnoreCase) ||
            provider.Equals(
                "Microsoft-Windows-WER-SystemErrorReporting",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "WER",
                "LOW",
                "Informes de error de Windows",
                "system.reliability.analyze",
                "WER aporta contexto histórico; la app lo presenta como evidencia, no como diagnóstico definitivo.");
        }

        return null;
    }

    private static int SeverityRank(string severity) =>
        severity switch
        {
            "HIGH" => 0,
            "MEDIUM" => 1,
            _ => 2
        };

    private sealed record InsightKey(
        string Category,
        string Severity,
        string Title,
        string RecommendedActionId,
        string Rationale);
}

public sealed record CrashInsight(
    string Category,
    string Severity,
    string Title,
    int Occurrences,
    DateTimeOffset LatestAt,
    string RecommendedActionId,
    string Rationale,
    string LatestEvidence);

public sealed record CrashIntelligenceReport(
    int EventsRead,
    IReadOnlyList<CrashInsight> Insights,
    int HighSeverityCount,
    int MediumSeverityCount);
