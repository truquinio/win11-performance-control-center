using System.Diagnostics.Eventing.Reader;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class BootSleepAuditService
{
    private const long ThirtyDaysMs = 30L * 24 * 60 * 60 * 1000;

    public AuditResult AnalyzeBoot()
    {
        var items = Read(
            "Microsoft-Windows-Diagnostics-Performance/Operational",
            "Microsoft-Windows-Diagnostics-Performance",
            [100],
            12,
            "Boot");
        return new AuditResult(
            items.Count == 0 ? "NO_DATA" : "OK",
            items);
    }

    public AuditResult AnalyzeSleepResume()
    {
        var items = new List<AuditItem>();
        items.AddRange(Read(
            "System",
            "Microsoft-Windows-Kernel-Power",
            [42],
            15,
            "Sleep"));
        items.AddRange(Read(
            "System",
            "Microsoft-Windows-Power-Troubleshooter",
            [1],
            15,
            "Resume"));

        return new AuditResult(
            items.Count == 0 ? "NO_DATA" : "OK",
            [.. items
                .OrderByDescending(ParseTimestamp)
                .Take(20)]);
    }
    private static IReadOnlyList<AuditItem> Read(
        string log,
        string provider,
        int[] eventIds,
        int limit,
        string category)
    {
        var items = new List<AuditItem>();
        var idExpression = string.Join(
            " or ",
            eventIds.Select(id => $"EventID={id}"));
        var xPath =
            $"*[System[Provider[@Name='{provider}'] and ({idExpression}) and " +
            $"TimeCreated[timediff(@SystemTime) <= {ThirtyDaysMs}]]]";

        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xPath)
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };
            using var reader = new EventLogReader(query);

            while (items.Count < limit)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;

                var time = record.TimeCreated is { } value
                    ? new DateTimeOffset(value)
                    : DateTimeOffset.MinValue;

                items.Add(new AuditItem(
                    category,
                    $"Event {record.Id}",
                    time.ToString("O"),
                    SafeDescription(record),
                    "HECHO"));
            }
        }
        catch (EventLogException)
        {
            // Optional diagnostics channel may be disabled.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only channel can be restricted.
        }

        return items;
    }
    private static string SafeDescription(EventRecord record)
    {
        try
        {
            var description = record.FormatDescription();
            if (string.IsNullOrWhiteSpace(description))
                return "Evento sin descripción disponible.";
            return description.Length <= 500
                ? description
                : description[..497] + "...";
        }
        catch (EventLogException)
        {
            return "Evento registrado; recursos de formato no disponibles.";
        }
    }

    private static DateTimeOffset ParseTimestamp(AuditItem item)
    {
        return DateTimeOffset.TryParse(item.Value, out var value)
            ? value
            : DateTimeOffset.MinValue;
    }
}
