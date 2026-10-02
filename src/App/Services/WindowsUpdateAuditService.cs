using System.Diagnostics.Eventing.Reader;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class WindowsUpdateAuditService
{
    private const long ThirtyDaysMs = 30L * 24 * 60 * 60 * 1000;

    public AuditResult Analyze()
    {
        var items = new List<AuditItem>();
        const string provider = "Microsoft-Windows-WindowsUpdateClient";
        var xPath =
            $"*[System[Provider[@Name='{provider}'] and " +
            "(EventID=19 or EventID=20 or EventID=31) and " +
            $"TimeCreated[timediff(@SystemTime) <= {ThirtyDaysMs}]]]";

        try
        {
            var query = new EventLogQuery("System", PathType.LogName, xPath)
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };
            using var reader = new EventLogReader(query);

            while (items.Count < 30)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;

                var status = record.Id == 19
                    ? "INSTALLED"
                    : record.Id == 20 ? "FAILED" : "OBSERVED";
                items.Add(new AuditItem(
                    "Windows Update",
                    $"Event {record.Id}",
                    record.TimeCreated?.ToString("O") ?? "Unknown",
                    SafeDescription(record),
                    status));
            }
        }
        catch (EventLogException)
        {
            // Event log may be unavailable.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only access can be policy restricted.
        }

        return new AuditResult(
            items.Count == 0 ? "NO_DATA" : "OK",
            items);
    }

    private static string SafeDescription(EventRecord record)
    {
        try
        {
            var description = record.FormatDescription();
            if (string.IsNullOrWhiteSpace(description))
                return "Evento de Windows Update.";
            return description.Length <= 500
                ? description
                : description[..497] + "...";
        }
        catch (EventLogException)
        {
            return "Evento de Windows Update registrado.";
        }
    }
}
