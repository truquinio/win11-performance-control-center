using System.Diagnostics.Eventing.Reader;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class ReliabilityService
{
    private const int SevenDaysMs = 7 * 24 * 60 * 60 * 1000;
    private const int PerSourceLimit = 16;

    private static readonly QuerySpec[] Queries =
    [
        new("System", "Microsoft-Windows-Kernel-Power", [41, 42, 109]),
        new("System", "Microsoft-Windows-Kernel-General", [12, 13]),
        new("System", "EventLog", [6008]),
        new("System", "User32", [1074]),
        new("System", "Microsoft-Windows-WER-SystemErrorReporting", [1001]),
        new("System", "Microsoft-Windows-WHEA-Logger", [1, 17, 18, 19, 20, 46, 47]),
        new("System", "Microsoft-Windows-WindowsUpdateClient", [19, 20]),
        new("System", "Microsoft-Windows-Kernel-Boot", []),
        new("Application", "Application Error", [1000]),
        new("Application", "Application Hang", [1002]),
        new("Application", "Windows Error Reporting", [1001])
    ];

    public Task<IReadOnlyList<ReliabilityEventDto>> GetRecentAsync(int limit = 40)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        return Task.Run<IReadOnlyList<ReliabilityEventDto>>(() =>
        {
            var events = new List<ReliabilityEventDto>();

            foreach (var spec in Queries)
                ReadQuery(spec, events);

            return [.. events
                .OrderByDescending(item => item.Time)
                .DistinctBy(item => (
                    item.Time.UtcTicks,
                    item.Id,
                    item.Provider,
                    item.Message))
                .Take(limit)];
        });
    }

    private static void ReadQuery(
        QuerySpec spec,
        ICollection<ReliabilityEventDto> destination)
    {
        try
        {
            var query = new EventLogQuery(
                spec.LogName,
                PathType.LogName,
                BuildXPath(spec))
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };

            using var reader = new EventLogReader(query);
            var accepted = 0;

            while (accepted < PerSourceLimit)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;

                var provider = record.ProviderName ?? "Unknown";
                if (!ReliabilityEvidenceClassifier.TryClassify(
                        provider,
                        record.Id,
                        out var classification))
                {
                    continue;
                }

                destination.Add(new ReliabilityEventDto(
                    record.TimeCreated is { } time
                        ? new DateTimeOffset(time)
                        : DateTimeOffset.MinValue,
                    record.Id,
                    provider,
                    classification,
                    SafeMessage(record, provider, record.Id)));
                accepted++;
            }
        }
        catch (EventLogNotFoundException)
        {
            // This evidence source is optional on some Windows editions.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only source may still require additional privileges.
        }
        catch (EventLogException)
        {
            // A corrupt/unavailable channel must not break the full diagnosis.
        }
    }

    private static string BuildXPath(QuerySpec spec)
    {
        var provider = $"Provider[@Name='{spec.Provider}']";
        var eventFilter = spec.EventIds.Length > 0
            ? "(" + string.Join(
                " or ",
                spec.EventIds.Select(id => $"EventID={id}")) + ")"
            : "(Level=1 or Level=2 or Level=3)";

        return
            $"*[System[{provider} and {eventFilter} and " +
            $"TimeCreated[timediff(@SystemTime) <= {SevenDaysMs}]]]";
    }

    private static string SafeMessage(
        EventRecord record,
        string provider,
        int id)
    {
        try
        {
            var description = record.FormatDescription();
            if (!string.IsNullOrWhiteSpace(description))
                return Compact(description);
        }
        catch (EventLogException)
        {
            // Provider resources may be unavailable; use factual fallback text.
        }

        return FallbackMessage(provider, id);
    }
    private static string FallbackMessage(string provider, int id)
    {
        if (provider.Equals(
                "Microsoft-Windows-Kernel-Power",
                StringComparison.OrdinalIgnoreCase) &&
            id == 41)
        {
            return "Windows detectó un arranque posterior a un apagado no limpio; el evento no identifica por sí solo la causa.";
        }

        if (provider.Equals(
                "Microsoft-Windows-WHEA-Logger",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Windows registró un evento WHEA de hardware. Es evidencia del evento, no una atribución automática de causa.";
        }

        if (provider.Equals(
                "Microsoft-Windows-Kernel-Boot",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Kernel-Boot registró una advertencia o error durante el arranque. El evento aislado no determina la causa.";
        }

        return (provider, id) switch
        {
            ("EventLog", 6008) =>
                "El cierre anterior fue registrado como inesperado.",
            ("User32", 1074) =>
                "Un proceso o usuario solicitó un apagado o reinicio.",
            ("Application Error", 1000) =>
                "Windows registró un fallo de aplicación.",
            ("Application Hang", 1002) =>
                "Windows registró una aplicación que dejó de responder.",
            _ => $"Evento {id} registrado por {provider}."
        };
    }

    private static string Compact(string value)
    {
        var compact = string.Join(
            " ",
            value
                .Split(
                    ['\r', '\n', '\t'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));

        return compact.Length <= 420
            ? compact
            : compact[..417] + "...";
    }

    private sealed record QuerySpec(
        string LogName,
        string Provider,
        int[] EventIds);
}
