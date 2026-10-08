using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Win11PerformanceControlCenter.App.Services;

public sealed partial class ProcessHygieneService
{
    private const int MaxItems = 80;
    private const int MaxRelatedProcesses = 24;

    private static readonly string[] ProtectedKeywords =
    [
        "bot_linkedin",
        "pm2",
        "desktop-commander",
        "desktop commander",
        "openai",
        "chatgpt",
        "claude",
        "cowork",
        "mcp",
        "ollama",
        "win11performancecontrolcenter"
    ];

    private readonly bool evaluationMode;

    public ProcessHygieneService(bool evaluationMode = false)
    {
        this.evaluationMode = evaluationMode;
    }

    public ProcessHygieneReport Analyze()
    {
        var processes = evaluationMode
            ? SyntheticProcesses()
            : ReadProcesses();

        var byPid = processes.ToDictionary(
            item => item.ProcessId);
        var items = new List<ProcessHygieneItem>();

        foreach (var process in processes
                     .OrderByDescending(item => item.WorkingSetBytes))
        {
            var item = Classify(process, processes, byPid);
            if (item is not null)
                items.Add(item);
            if (items.Count >= MaxItems)
                break;
        }

        var distinct = items
            .GroupBy(item => item.ProcessId)
            .Select(group => group.First())
            .OrderBy(item => item.Protected)
            .ThenByDescending(item => item.Stoppable)
            .ThenByDescending(item => item.EstimatedReclaimMb)
            .ToArray();

        return new ProcessHygieneReport(
            DateTimeOffset.UtcNow,
            distinct.Count(item => item.Stoppable),
            distinct.Count(item => item.Protected),
            Math.Round(
                distinct
                    .Where(item => item.Stoppable)
                    .Sum(item => item.EstimatedReclaimMb),
                1),
            distinct);
    }

    public async Task<ProcessHygieneStopResult> StopAsync(
        int processId,
        string fingerprint)
    {
        if (processId <= 0 ||
            string.IsNullOrWhiteSpace(fingerprint) ||
            fingerprint.Length != 24 ||
            !fingerprint.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Identificador de proceso no permitido.");
        }

        var report = Analyze();
        var item = report.Items.FirstOrDefault(candidate =>
            candidate.ProcessId == processId);

        if (item is null ||
            item.Protected ||
            !item.Stoppable)
        {
            throw new InvalidOperationException(
                "El proceso ya no es un candidato detenible.");
        }

        if (!string.Equals(
                item.Fingerprint,
                fingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "La huella del proceso cambió; vuelve a analizar antes de detenerlo.");
        }

        if (evaluationMode)
        {
            return new ProcessHygieneStopResult(
                true,
                "EVALUATION",
                item.Category,
                item.Identity,
                item.RelatedProcessIds,
                [],
                item.EstimatedReclaimMb,
                "Detención sintética verificada; no se tocó el host.");
        }

        var stopped = new List<int>();
        var failed = new List<int>();

        foreach (var pid in item.RelatedProcessIds
                     .Distinct()
                     .Take(MaxRelatedProcesses)
                     .OrderByDescending(pid => pid == processId))
        {
            try
            {
                using var target = Process.GetProcessById(pid);
                target.Kill(entireProcessTree: true);
                stopped.Add(pid);
            }
            catch (ArgumentException)
            {
                stopped.Add(pid);
            }
            catch (InvalidOperationException)
            {
                stopped.Add(pid);
            }
            catch
            {
                failed.Add(pid);
            }
        }

        await Task.Delay(600);

        var stillAlive = item.RelatedProcessIds
            .Distinct()
            .Where(IsAlive)
            .ToArray();

        var success =
            failed.Count == 0 &&
            stillAlive.Length == 0;

        return new ProcessHygieneStopResult(
            success,
            success
                ? "STOPPED_AND_VERIFIED"
                : "VERIFY_FAILED",
            item.Category,
            item.Identity,
            stopped,
            stillAlive,
            item.EstimatedReclaimMb,
            success
                ? "El residuo de laboratorio dejó de ejecutarse y se verificó el resultado."
                : "Algún proceso no pudo detenerse o reapareció; no se encadenaron más acciones.");
    }

    private static ProcessHygieneItem? Classify(
        ProcessDescriptor process,
        IReadOnlyList<ProcessDescriptor> all,
        IReadOnlyDictionary<int, ProcessDescriptor> byPid)
    {
        var command = process.CommandLine ?? string.Empty;
        var lower = command.ToLowerInvariant();

        if (process.Name.Equals(
                "msedge.exe",
                StringComparison.OrdinalIgnoreCase) &&
            lower.Contains("--headless") &&
            !lower.Contains("--type=") &&
            TryExtractUserDataDir(command, out var userDataDir) &&
            Regex.IsMatch(
                userDataDir,
                @"sig-edge-headless-profile\d+",
                RegexOptions.IgnoreCase))
        {
            var related = all
                .Where(candidate =>
                    candidate.Name.Equals(
                        "msedge.exe",
                        StringComparison.OrdinalIgnoreCase) &&
                    (candidate.CommandLine ?? string.Empty)
                        .Contains(
                            userDataDir,
                            StringComparison.OrdinalIgnoreCase))
                .Take(MaxRelatedProcesses)
                .ToArray();

            return CreateItem(
                process,
                "HEADLESS_SIG_TEST",
                Path.GetFileName(userDataDir.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)),
                "Navegador Edge headless con perfil dedicado de pruebas SIG.",
                protectedItem: false,
                stoppable: true,
                related);
        }

        if (process.Name.StartsWith(
                "qemu-system-x86_64",
                StringComparison.OrdinalIgnoreCase))
        {
            var match = AvdRegex().Match(command);
            if (match.Success)
            {
                var avd = match.Groups["avd"].Value;
                var lab = Regex.IsMatch(
                    avd,
                    @"(?:hooklab|play_control|test|lab)",
                    RegexOptions.IgnoreCase);
                if (lab)
                {
                    var launcher = all.FirstOrDefault(candidate =>
                        candidate.Name.Equals(
                            "emulator.exe",
                            StringComparison.OrdinalIgnoreCase) &&
                        AvdRegex().Match(
                            candidate.CommandLine ?? string.Empty)
                            .Groups["avd"]
                            .Value
                            .Equals(
                                avd,
                                StringComparison.OrdinalIgnoreCase));

                    if (launcher is not null)
                    {
                        return CreateItem(
                            process,
                            "ACTIVE_ANDROID_EMULATOR",
                            avd,
                            "AVD de laboratorio con emulator.exe activo asociado; se protege y no se detiene.",
                            protectedItem: true,
                            stoppable: false,
                            [process, launcher]);
                    }

                    return CreateItem(
                        process,
                        "ANDROID_LAB",
                        avd,
                        "QEMU de laboratorio sin emulator.exe activo asociado; candidato a residuo solo tras revalidación.",
                        protectedItem: false,
                        stoppable: true,
                        [process]);
                }
            }
        }

        if (process.Name.Equals(
                "python.exe",
                StringComparison.OrdinalIgnoreCase) &&
            TryExtractUvicornPort(command, out var port))
        {
            var childWithSameCommand = all.Any(candidate =>
                candidate.ParentProcessId == process.ProcessId &&
                candidate.Name.Equals(
                    "python.exe",
                    StringComparison.OrdinalIgnoreCase) &&
                TryExtractUvicornPort(
                    candidate.CommandLine ?? string.Empty,
                    out var childPort) &&
                childPort == port);

            if (childWithSameCommand)
                return null;

            if (byPid.TryGetValue(
                    process.ParentProcessId,
                    out var parent) &&
                parent.Name.Equals(
                    "python.exe",
                    StringComparison.OrdinalIgnoreCase) &&
                TryExtractUvicornPort(
                    parent.CommandLine ?? string.Empty,
                    out var parentPort) &&
                parentPort == port)
            {
                var grandParentAlive =
                    byPid.ContainsKey(parent.ParentProcessId);

                if (!grandParentAlive)
                {
                    return CreateItem(
                        process,
                        "ORPHAN_UVICORN",
                        $"127.0.0.1:{port}",
                        "Servidor Uvicorn cuyo shim Python padre quedó sin proceso iniciador.",
                        protectedItem: false,
                        stoppable: true,
                        [process, parent]);
                }

                return CreateItem(
                    process,
                    "ACTIVE_DEV_SERVER",
                    $"127.0.0.1:{port}",
                    "Servidor de desarrollo con cadena de procesos activa; no se detiene automáticamente.",
                    protectedItem: true,
                    stoppable: false,
                    [process, parent]);
            }
        }

        if (process.Name.Equals(
                "chrome-headless-shell.exe",
                StringComparison.OrdinalIgnoreCase) &&
            !lower.Contains("--type="))
        {
            var protectedAncestor =
                HasProtectedAncestor(process, byPid);
            if (protectedAncestor)
            {
                return CreateItem(
                    process,
                    "PROTECTED_HEADLESS_BROWSER",
                    "Playwright / Chromium",
                    "El navegador headless pertenece a una cadena protegida (PM2/bot/automatización).",
                    protectedItem: true,
                    stoppable: false,
                    [process]);
            }
        }

        return null;
    }

    private static ProcessHygieneItem CreateItem(
        ProcessDescriptor primary,
        string category,
        string identity,
        string reason,
        bool protectedItem,
        bool stoppable,
        IReadOnlyList<ProcessDescriptor> related)
    {
        var safeRelated = related
            .DistinctBy(item => item.ProcessId)
            .Take(MaxRelatedProcesses)
            .ToArray();

        return new ProcessHygieneItem(
            primary.ProcessId,
            primary.Name,
            category,
            identity,
            reason,
            protectedItem,
            stoppable,
            Fingerprint(primary),
            Math.Round(
                safeRelated.Sum(item => item.WorkingSetBytes) /
                1048576d,
                1),
            primary.StartedAt is null
                ? null
                : Math.Max(
                    0,
                    Math.Round(
                        (DateTimeOffset.Now -
                         primary.StartedAt.Value)
                        .TotalMinutes,
                        1)),
            safeRelated
                .Select(item => item.ProcessId)
                .ToArray());
    }

    private static bool HasProtectedAncestor(
        ProcessDescriptor process,
        IReadOnlyDictionary<int, ProcessDescriptor> byPid)
    {
        var current = process;
        for (var depth = 0; depth < 4; depth++)
        {
            if (!byPid.TryGetValue(
                    current.ParentProcessId,
                    out var parent))
                return false;

            var combined = string.Join(
                " ",
                parent.Name,
                parent.CommandLine ?? string.Empty);

            if (ProtectedKeywords.Any(keyword =>
                    combined.Contains(
                        keyword,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private static bool TryExtractUserDataDir(
        string commandLine,
        out string path)
    {
        path = string.Empty;
        var match = UserDataDirRegex().Match(commandLine);
        if (!match.Success)
            return false;

        path = match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["plain"].Value;
        return path.Length > 0;
    }

    private static bool TryExtractUvicornPort(
        string commandLine,
        out int port)
    {
        port = 0;
        var match = UvicornRegex().Match(commandLine);
        return match.Success &&
            int.TryParse(
                match.Groups["port"].Value,
                out port);
    }

    private static string Fingerprint(
        ProcessDescriptor process)
    {
        var raw = string.Join(
            "|",
            process.ProcessId,
            process.Name,
            process.StartedAt?.UtcDateTime.Ticks ?? 0,
            process.CommandLine ?? string.Empty);
        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(raw)))[..24];
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<ProcessDescriptor>
        ReadProcesses()
    {
        var result = new List<ProcessDescriptor>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, " +
                "CommandLine, CreationDate FROM Win32_Process");
            using var rows = searcher.Get();

            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var pid = ToInt32(item["ProcessId"]);
                    if (pid <= 0)
                        continue;

                    var name =
                        Convert.ToString(item["Name"]) ??
                        string.Empty;
                    var command =
                        Convert.ToString(item["CommandLine"]);
                    var parent =
                        ToInt32(item["ParentProcessId"]);
                    DateTimeOffset? startedAt = null;

                    try
                    {
                        var creation = Convert.ToString(
                            item["CreationDate"]);
                        if (!string.IsNullOrWhiteSpace(creation))
                        {
                            var date =
                                ManagementDateTimeConverter
                                    .ToDateTime(creation);
                            startedAt =
                                new DateTimeOffset(date);
                        }
                    }
                    catch
                    {
                    }

                    long workingSet = 0;
                    try
                    {
                        using var process =
                            Process.GetProcessById(pid);
                        workingSet =
                            process.WorkingSet64;
                        startedAt ??=
                            new DateTimeOffset(
                                process.StartTime);
                    }
                    catch
                    {
                    }

                    result.Add(new ProcessDescriptor(
                        pid,
                        parent,
                        name,
                        command,
                        workingSet,
                        startedAt));
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (COMException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    private static int ToInt32(object? value)
    {
        try
        {
            return value is null
                ? 0
                : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static IReadOnlyList<ProcessDescriptor>
        SyntheticProcesses()
    {
        var now = DateTimeOffset.Now;

        return
        [
            new(
                100,
                10,
                "msedge.exe",
                @"msedge.exe --headless=new --user-data-dir=C:\Users\demo\sig-edge-headless-profile3 http://127.0.0.1:8774/",
                90 * 1024 * 1024,
                now.AddHours(-2)),
            new(
                101,
                100,
                "msedge.exe",
                @"msedge.exe --type=renderer --user-data-dir=C:\Users\demo\sig-edge-headless-profile3",
                60 * 1024 * 1024,
                now.AddHours(-2)),
            new(
                200,
                20,
                "qemu-system-x86_64-headless.exe",
                @"qemu-system-x86_64-headless.exe -avd spotify_api28_hooklab2 -port 5566 -no-window",
                1536L * 1024 * 1024,
                now.AddHours(-8)),
            new(
                210,
                1,
                "emulator.exe",
                @"emulator.exe -avd spotify_api28_hooklab_active -port 5570 -memory 2048",
                80 * 1024 * 1024,
                now.AddMinutes(-15)),
            new(
                211,
                210,
                "qemu-system-x86_64.exe",
                @"qemu-system-x86_64.exe -avd spotify_api28_hooklab_active -port 5570 -memory 2048",
                1800L * 1024 * 1024,
                now.AddMinutes(-15)),
            new(
                300,
                301,
                "python.exe",
                @"python.exe -m uvicorn backend.app.main:app --host 127.0.0.1 --port 8773",
                90 * 1024 * 1024,
                now.AddHours(-4)),
            new(
                301,
                999,
                "python.exe",
                @"python.exe -m uvicorn backend.app.main:app --host 127.0.0.1 --port 8773",
                12 * 1024 * 1024,
                now.AddHours(-4)),
            new(
                400,
                401,
                "python.exe",
                @"python.exe -m uvicorn backend.app.main:app --host 127.0.0.1 --port 8877",
                120 * 1024 * 1024,
                now.AddMinutes(-20)),
            new(
                401,
                402,
                "python.exe",
                @"python.exe -m uvicorn backend.app.main:app --host 127.0.0.1 --port 8877",
                12 * 1024 * 1024,
                now.AddMinutes(-20)),
            new(
                402,
                1,
                "powershell.exe",
                @"powershell -Command Set-Location D:\SIG; python -m uvicorn backend.app.main:app --host 127.0.0.1 --port 8877",
                40 * 1024 * 1024,
                now.AddMinutes(-21)),
            new(
                500,
                501,
                "chrome-headless-shell.exe",
                @"chrome-headless-shell.exe --headless --user-data-dir=C:\Temp\playwright-profile",
                100 * 1024 * 1024,
                now.AddMinutes(-30)),
            new(
                501,
                502,
                "node.exe",
                @"node C:\Users\demo\AppData\Roaming\npm\node_modules\pm2\lib\ProcessContainerFork.js",
                80 * 1024 * 1024,
                now.AddHours(-3)),
            new(
                502,
                1,
                "node.exe",
                @"node pm2 Daemon.js bot_linkedin",
                70 * 1024 * 1024,
                now.AddHours(-3))
        ];
    }

    [GeneratedRegex(
        @"-avd\s+(?<avd>[^\s]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex AvdRegex();

    [GeneratedRegex(
        @"--user-data-dir(?:=|\s+)(?:""(?<quoted>[^""]+)""|(?<plain>[^\s]+))",
        RegexOptions.IgnoreCase)]
    private static partial Regex UserDataDirRegex();

    [GeneratedRegex(
        @"-m\s+uvicorn\s+backend\.app\.main:app.*?--host\s+127\.0\.0\.1.*?--port\s+(?<port>\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UvicornRegex();

    private sealed record ProcessDescriptor(
        int ProcessId,
        int ParentProcessId,
        string Name,
        string? CommandLine,
        long WorkingSetBytes,
        DateTimeOffset? StartedAt);
}

public sealed record ProcessHygieneItem(
    int ProcessId,
    string ProcessName,
    string Category,
    string Identity,
    string Reason,
    bool Protected,
    bool Stoppable,
    string Fingerprint,
    double EstimatedReclaimMb,
    double? AgeMinutes,
    IReadOnlyList<int> RelatedProcessIds);

public sealed record ProcessHygieneReport(
    DateTimeOffset CapturedAt,
    int StoppableCount,
    int ProtectedCount,
    double EstimatedReclaimMb,
    IReadOnlyList<ProcessHygieneItem> Items);

public sealed record ProcessHygieneStopResult(
    bool Success,
    string Status,
    string Category,
    string Identity,
    IReadOnlyList<int> StoppedProcessIds,
    IReadOnlyList<int> StillAliveProcessIds,
    double EstimatedReclaimMb,
    string Detail);
