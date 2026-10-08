using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

/// <summary>
/// Non-invasive, on-demand snapshots of browser automation. No process is
/// stopped, throttled or launched; no command line is returned or persisted.
/// </summary>
public sealed class BrowserAutomationTelemetryService
{
    private const int MaxHistory = 60;
    private const int MaxProcesses = 240;
    private const long WarningPrivateBytes = 1536L * 1024 * 1024;
    private readonly string statePath;
    private readonly Func<IReadOnlyList<BrowserObservedProcess>> reader;
    private readonly Func<DateTimeOffset> clock;

    public BrowserAutomationTelemetryService(
        string? statePath = null,
        bool evaluationMode = false)
        : this(
            string.IsNullOrWhiteSpace(statePath)
                ? AppPaths.BrowserAutomationHistoryState
                : Path.GetFullPath(statePath),
            evaluationMode
                ? () => Array.Empty<BrowserObservedProcess>()
                : ReadWindowsProcesses,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal BrowserAutomationTelemetryService(
        string statePath,
        Func<IReadOnlyList<BrowserObservedProcess>> reader,
        Func<DateTimeOffset> clock)
    {
        this.statePath = Path.GetFullPath(statePath);
        this.reader = reader;
        this.clock = clock;
        var dir = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }

    public BrowserAutomationReport Analyze()
    {
        var at = clock();
        var observed = reader().Take(MaxProcesses).ToArray();
        var byId = observed.ToDictionary(item => item.ProcessId);
        var processes = observed
            .Where(IsBrowser)
            .Select(item => ToRow(item, byId))
            .OrderByDescending(item => item.WorkingSetBytes)
            .ToArray();

        var previous = LoadHistory()
            .Where(item => item.At >= at.AddDays(-3) && item.At <= at)
            .OrderBy(item => item.At)
            .TakeLast(MaxHistory - 1)
            .ToList();

        var aggregates = processes
            .GroupBy(item => item.Family)
            .ToDictionary(
                group => group.Key,
                group => new BrowserAutomationAggregate(
                    group.Key,
                    group.Count(),
                    group.Sum(item => item.WorkingSetBytes),
                    group.Sum(item => item.PrivateBytes),
                    group.Max(item => item.WorkingSetBytes),
                    group
                        .Where(item => item.Attribution == "CONFIRMED")
                        .Select(item => item.Owner)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()));

        var current = new BrowserAutomationCapture(
            at,
            aggregates.Values
                .OrderBy(item => item.Family)
                .ToArray());

        var history = previous
            .Append(current)
            .TakeLast(MaxHistory)
            .ToArray();

        // Persist only aggregate counters. Never persist PIDs, executable
        // paths, parent lineage, arguments, cookies or user profile details.
        PersistHistory(history);

        var familyNames = new[]
        {
            "PLAYWRIGHT_CHROME",
            "PLAYWRIGHT_HEADLESS",
            "EDGE",
            "OTHER_CHROME"
        };
        var families = familyNames.Select(family =>
        {
            aggregates.TryGetValue(family, out var now);
            var samples = history
                .Select(item => new BrowserAutomationHistoryPoint(
                    item.At,
                    item.Groups.FirstOrDefault(group =>
                        group.Family == family)?.PrivateBytes ?? 0L,
                    item.Groups.FirstOrDefault(group =>
                        group.Family == family)?.WorkingSetBytes ?? 0L,
                    item.Groups.FirstOrDefault(group =>
                        group.Family == family)?.ProcessCount ?? 0))
                .ToArray();

            var high = samples
                .Reverse()
                .TakeWhile(sample =>
                    sample.PrivateBytes >= WarningPrivateBytes)
                .Reverse()
                .ToArray();

            var sustained =
                high.Length >= 3 &&
                high[^1].At - high[0].At >= TimeSpan.FromMinutes(2);

            var previousPeak = samples
                .Take(Math.Max(0, samples.Length - 1))
                .TakeLast(24)
                .Select(sample => sample.PrivateBytes)
                .DefaultIfEmpty(0L)
                .Max();

            var workingSetPeak = samples
                .Take(Math.Max(0, samples.Length - 1))
                .TakeLast(24)
                .Select(sample => sample.WorkingSetBytes)
                .DefaultIfEmpty(0L)
                .Max();
            // SEPE incident: roughly 376 MiB -> 145 MiB within a
            // short-lived headless render. It is transient, not a leak.
            var recovered =
                (previousPeak >= 256L * 1024 * 1024 &&
                 (now?.PrivateBytes ?? 0L) <= previousPeak * 0.4) ||
                (workingSetPeak >= 256L * 1024 * 1024 &&
                 (now?.WorkingSetBytes ?? 0L) <= workingSetPeak * 0.4);

            var status = sustained
                ? "SUSTAINED_REVIEW"
                : recovered
                    ? "TRANSIENT_RECOVERED"
                    : (now?.ProcessCount ?? 0) == 0
                        ? "NOT_RUNNING"
                        : "OBSERVED";

            var detail = status switch
            {
                "SUSTAINED_REVIEW" =>
                    "Consumo privado elevado durante tres o más muestras en al menos dos minutos. Revisar reutilización y cierre por inactividad; no se ha demostrado una fuga.",
                "TRANSIENT_RECOVERED" =>
                    "Un pico anterior disminuyó al menos un 60%. Compatible con trabajo transitorio; no se justifica cerrar procesos.",
                "NOT_RUNNING" =>
                    "No se detectó esta familia en la muestra. El medidor no inicia navegadores ni tareas.",
                _ =>
                    "Muestra puntual. No permite concluir fuga, inactividad prolongada ni oportunidad segura de terminar procesos."
            };

            return new BrowserAutomationFamilyResult(
                family,
                now?.ProcessCount ?? 0,
                now?.WorkingSetBytes ?? 0,
                now?.PrivateBytes ?? 0,
                samples.Max(point => point.PrivateBytes),
                status,
                detail,
                now?.ConfirmedOwners ?? [],
                samples.TakeLast(12).ToArray());
        }).ToArray();

        return new BrowserAutomationReport(
            at,
            processes.Length,
            processes.Sum(item => item.WorkingSetBytes),
            processes.Sum(item => item.PrivateBytes),
            previous.Count + 1,
            "Los working sets sumados pueden contar páginas compartidas más de una vez. PrivateBytes mide compromiso privado, no RAM física exclusiva.",
            "Solo observación: sin lanzar, matar, optimizar ni reiniciar procesos. Las atribuciones sin cadena de padres confirmada se indican como desconocidas.",
            families,
            processes.Take(80).ToArray());
    }

    internal static BrowserAutomationProcess ToRow(
        BrowserObservedProcess item,
        IReadOnlyDictionary<int, BrowserObservedProcess> byId)
    {
        var family = ClassifyFamily(item);
        var owner = "UNKNOWN";
        var attribution = "UNDETERMINED";

        if (family is "PLAYWRIGHT_CHROME" or "PLAYWRIGHT_HEADLESS")
        {
            var node = item;
            var visited = new HashSet<int> { item.ProcessId };
            for (var i = 0; i < 9; i++)
            {
                if (node.ParentProcessId <= 0 ||
                    !byId.TryGetValue(node.ParentProcessId, out var parent) ||
                    !visited.Add(parent.ProcessId))
                    break;

                // Fail closed if Windows recycled a parent PID.
                if (node.StartedAt != DateTimeOffset.MinValue &&
                    parent.StartedAt != DateTimeOffset.MinValue &&
                    parent.StartedAt > node.StartedAt)
                    break;

                var line = parent.CommandLine ?? string.Empty;
                if (ContainsAny(line, "bot_linkedin", "bot-linkedin",
                        "bot_linkedin_engineering", "bot-linkedin-listener"))
                {
                    owner = "LINKEDIN_BOT";
                    attribution = "CONFIRMED";
                    break;
                }

                if (ContainsAny(line, "cita-sepe-agent",
                        "cita_sepe_agent", "cita-sepe", "cita_sepe"))
                {
                    owner = "SEPE_AGENT";
                    attribution = "CONFIRMED";
                    break;
                }

                node = parent;
            }
        }

        var roleMatch = Regex.Match(
            item.CommandLine ?? "",
            @"--type=([a-z-]+)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        return new BrowserAutomationProcess(
            item.ProcessId,
            item.ParentProcessId,
            family,
            roleMatch.Success ? roleMatch.Groups[1].Value : "browser",
            owner,
            attribution,
            Math.Max(0, item.WorkingSetBytes),
            Math.Max(0, item.PrivateBytes));
    }

    internal static string ClassifyFamily(BrowserObservedProcess item)
    {
        var name = item.Name.ToLowerInvariant();
        var path = (item.ExecutablePath ?? "").Replace('/', '\\');
        if (name == "msedge.exe")
            return "EDGE";
        if (name == "chrome-headless-shell.exe")
            return "PLAYWRIGHT_HEADLESS";
        if (name == "chrome.exe" &&
            ContainsAny(path, @"\ms-playwright\", "chrome-for-testing",
                @"\chrome-win64\", @"\chrome-win\"))
            return "PLAYWRIGHT_CHROME";
        return "OTHER_CHROME";
    }

    private static bool IsBrowser(BrowserObservedProcess item) =>
        item.Name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
        item.Name.Equals("chrome-headless-shell.exe", StringComparison.OrdinalIgnoreCase) ||
        item.Name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment =>
            value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<BrowserObservedProcess> ReadWindowsProcesses()
    {
        var result = new List<BrowserObservedProcess>();
        try
        {
            using var query = new ManagementObjectSearcher(
                "SELECT Name, ProcessId, ParentProcessId, ExecutablePath, CommandLine, CreationDate " +
                "FROM Win32_Process WHERE Name='chrome.exe' OR " +
                "Name='chrome-headless-shell.exe' OR Name='msedge.exe' OR " +
                "Name='node.exe' OR Name='nodew.exe' OR Name='pm2.exe' OR " +
                "Name='python.exe' OR Name='pythonw.exe'");
            using var rows = query.Get();
            foreach (var raw in rows)
            {
                if (result.Count >= MaxProcesses)
                    break;
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;
                    var pid = Convert.ToInt32(item["ProcessId"]);
                    var parent = Convert.ToInt32(item["ParentProcessId"]);
                    if (pid <= 0)
                        continue;

                    var started = DateTimeOffset.MinValue;
                    try
                    {
                        var wmiDate = Convert.ToString(item["CreationDate"]);
                        if (!string.IsNullOrWhiteSpace(wmiDate))
                            started = new DateTimeOffset(
                                ManagementDateTimeConverter.ToDateTime(wmiDate));
                    }
                    catch (Exception ex) when (ex is FormatException or
                        ArgumentException or ManagementException)
                    {
                    }

                    long workingSet = 0;
                    long privateBytes = 0;
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        workingSet = process.WorkingSet64;
                        privateBytes = process.PrivateMemorySize64;
                    }
                    catch (Exception ex) when (ex is
                        ArgumentException or
                        InvalidOperationException or
                        System.ComponentModel.Win32Exception)
                    {
                        continue;
                    }

                    result.Add(new BrowserObservedProcess(
                        pid,
                        parent,
                        Convert.ToString(item["Name"]) ?? "",
                        Convert.ToString(item["ExecutablePath"]),
                        Convert.ToString(item["CommandLine"]),
                        started,
                        workingSet,
                        privateBytes));
                }
            }
        }
        catch (Exception ex) when (ex is
            ManagementException or
            UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException)
        {
            // A partial/empty observation must never trigger automatic action.
        }

        return result;
    }

    private IReadOnlyList<BrowserAutomationCapture> LoadHistory()
    {
        try
        {
            if (!File.Exists(statePath))
                return [];
            return JsonSerializer.Deserialize<
                BrowserAutomationCapture[]>(
                    File.ReadAllText(statePath),
                    HostBridge.JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void PersistHistory(
        IReadOnlyList<BrowserAutomationCapture> history)
    {
        try
        {
            var temp = statePath + ".tmp";
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(history, HostBridge.JsonOptions));
            File.Move(temp, statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException)
        {
            // Monitoring remains available if writing aggregate history fails.
        }
    }
}

public sealed record BrowserObservedProcess(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? ExecutablePath,
    string? CommandLine,
    DateTimeOffset StartedAt,
    long WorkingSetBytes,
    long PrivateBytes);

public sealed record BrowserAutomationProcess(
    int ProcessId,
    int ParentProcessId,
    string Family,
    string Role,
    string Owner,
    string Attribution,
    long WorkingSetBytes,
    long PrivateBytes);

public sealed record BrowserAutomationAggregate(
    string Family,
    int ProcessCount,
    long WorkingSetBytes,
    long PrivateBytes,
    long LargestWorkingSetBytes,
    IReadOnlyList<string> ConfirmedOwners);

public sealed record BrowserAutomationCapture(
    DateTimeOffset At,
    IReadOnlyList<BrowserAutomationAggregate> Groups);

public sealed record BrowserAutomationHistoryPoint(
    DateTimeOffset At,
    long PrivateBytes,
    long WorkingSetBytes,
    int ProcessCount);

public sealed record BrowserAutomationFamilyResult(
    string Family,
    int ProcessCount,
    long WorkingSetBytes,
    long PrivateBytes,
    long PeakPrivateBytes,
    string Status,
    string Detail,
    IReadOnlyList<string> ConfirmedOwners,
    IReadOnlyList<BrowserAutomationHistoryPoint> RecentSamples);

public sealed record BrowserAutomationReport(
    DateTimeOffset CapturedAt,
    int TotalBrowserProcesses,
    long WorkingSetBytes,
    long PrivateBytes,
    int SampleCount,
    string AccountingNotice,
    string SafetyNotice,
    IReadOnlyList<BrowserAutomationFamilyResult> Families,
    IReadOnlyList<BrowserAutomationProcess> Processes);
