using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class ScheduledTaskRemediationService
{
    private static readonly string[] ProtectedKeywords =
    [
        "security", "defender", "antivirus", "malwarebytes",
        "openai", "chatgpt", "claude", "desktop commander",
        "desktopcommander", "mcp", "ollama", "pm2", "node",
        "python", "nssm", "onedrive", "windows"
    ];

    private readonly string statePath;
    private readonly bool evaluationMode;

    public ScheduledTaskRemediationService(
        string? statePath = null,
        bool evaluationMode = false)
    {
        AppPaths.EnsureDirectories();
        this.statePath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.ScheduledTaskState
            : Path.GetFullPath(statePath);
        this.evaluationMode = evaluationMode;

        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public Task<ScheduledTaskPreview> PreviewAsync() =>
        Task.Run(() =>
        {
            var state = LoadState();
            var tasks = ReadTasks();

            var items = tasks
                .Select(task =>
                {
                    var restore = state.ContainsKey(task.EntryId);
                    var protection = Classify(task);
                    return new ScheduledTaskCandidate(
                        task.EntryId,
                        task.FullName,
                        task.TaskName,
                        task.TaskPath,
                        task.State,
                        task.Execute,
                        task.Arguments,
                        protection.Protected,
                        protection.Reason,
                        restore);
                })
                .OrderBy(item => item.State.Equals(
                    "Disabled",
                    StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(item => item.Protected)
                .ThenBy(item => item.FullName,
                    StringComparer.OrdinalIgnoreCase)
                .Take(160)
                .ToList();

            var knownIds = tasks
                .Select(task => task.EntryId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var snapshot in state.Values)
            {
                if (knownIds.Contains(snapshot.EntryId))
                    continue;

                items.Add(new ScheduledTaskCandidate(
                    snapshot.EntryId,
                    snapshot.FullName,
                    snapshot.TaskName,
                    snapshot.TaskPath,
                    "MISSING",
                    snapshot.Execute,
                    snapshot.Arguments,
                    true,
                    "La tarea guardada ya no existe; el snapshot se conserva como evidencia.",
                    true));
            }

            return new ScheduledTaskPreview(
                items.Count,
                items.Count(item =>
                    !item.Protected &&
                    !item.State.Equals(
                        "Disabled",
                        StringComparison.OrdinalIgnoreCase)),
                items.Count(item => item.Protected),
                items.Count(item => item.RestoreAvailable),
                items);
        });

    public async Task<ScheduledTaskChangeResult> DisableAsync(
        string entryId)
    {
        ValidateEntryId(entryId);
        var task = ReadTasks().FirstOrDefault(item =>
            string.Equals(
                item.EntryId,
                entryId,
                StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidOperationException(
                "La tarea programada ya no existe.");

        var protection = Classify(task);
        if (protection.Protected)
        {
            throw new InvalidOperationException(
                "Tarea protegida: " + protection.Reason);
        }

        if (task.State.Equals(
                "Disabled",
                StringComparison.OrdinalIgnoreCase))
        {
            return new ScheduledTaskChangeResult(
                true,
                "ALREADY_DISABLED",
                task.EntryId,
                task.FullName,
                task.State,
                task.State,
                LoadState().ContainsKey(task.EntryId),
                "La tarea ya estaba deshabilitada.");
        }

        var state = LoadState();
        if (!state.ContainsKey(task.EntryId))
        {
            state[task.EntryId] = new ScheduledTaskSnapshot(
                task.EntryId,
                task.FullName,
                task.TaskName,
                task.TaskPath,
                task.State,
                task.Execute,
                task.Arguments,
                DateTimeOffset.UtcNow);
            PersistState(state);
        }

        if (evaluationMode)
        {
            return new ScheduledTaskChangeResult(
                true,
                "DISABLED",
                task.EntryId,
                task.FullName,
                task.State,
                "Disabled",
                true,
                "EVALUATION");
        }

        var result = await RunSchtasksAsync(
            ["/Change", "/TN", task.FullName, "/Disable"]);

        if (result.ExitCode != 0)
        {
            return new ScheduledTaskChangeResult(
                false,
                "DISABLE_FAILED",
                task.EntryId,
                task.FullName,
                task.State,
                task.State,
                true,
                result.Output);
        }

        await Task.Delay(300);
        var after = ReadTasks().FirstOrDefault(item =>
            string.Equals(
                item.EntryId,
                task.EntryId,
                StringComparison.OrdinalIgnoreCase));
        var verified = after is not null &&
            after.State.Equals(
                "Disabled",
                StringComparison.OrdinalIgnoreCase);

        return new ScheduledTaskChangeResult(
            verified,
            verified ? "DISABLED" : "VERIFY_FAILED",
            task.EntryId,
            task.FullName,
            task.State,
            after?.State ?? "MISSING",
            true,
            verified
                ? "Tarea deshabilitada para futuras ejecuciones. No se detuvo ningún proceso actual."
                : "schtasks aceptó el cambio, pero el estado Disabled no pudo verificarse.");
    }

    public async Task<ScheduledTaskChangeResult> RestoreAsync(
        string entryId)
    {
        ValidateEntryId(entryId);
        var state = LoadState();
        if (!state.TryGetValue(entryId, out var snapshot))
        {
            return new ScheduledTaskChangeResult(
                true,
                "NO_SNAPSHOT",
                entryId,
                entryId,
                "UNKNOWN",
                "UNKNOWN",
                false,
                "No existe snapshot guardado.");
        }

        var current = ReadTasks().FirstOrDefault(item =>
            string.Equals(
                item.EntryId,
                entryId,
                StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return new ScheduledTaskChangeResult(
                false,
                "TASK_MISSING",
                entryId,
                snapshot.FullName,
                "MISSING",
                "MISSING",
                true,
                "La tarea ya no existe; la app no recrea tareas programadas desde un snapshot parcial.");
        }

        if (evaluationMode)
        {
            state.Remove(entryId);
            PersistState(state);
            return new ScheduledTaskChangeResult(
                true,
                "RESTORED",
                entryId,
                snapshot.FullName,
                "Disabled",
                snapshot.State,
                false,
                "EVALUATION");
        }

        var result = await RunSchtasksAsync(
            ["/Change", "/TN", snapshot.FullName, "/Enable"]);
        if (result.ExitCode != 0)
        {
            return new ScheduledTaskChangeResult(
                false,
                "RESTORE_FAILED",
                entryId,
                snapshot.FullName,
                current.State,
                current.State,
                true,
                result.Output);
        }

        await Task.Delay(300);
        var after = ReadTasks().FirstOrDefault(item =>
            string.Equals(
                item.EntryId,
                entryId,
                StringComparison.OrdinalIgnoreCase));
        var verified = after is not null &&
            !after.State.Equals(
                "Disabled",
                StringComparison.OrdinalIgnoreCase);

        if (verified)
        {
            state.Remove(entryId);
            PersistState(state);
        }

        return new ScheduledTaskChangeResult(
            verified,
            verified ? "RESTORED" : "VERIFY_FAILED",
            entryId,
            snapshot.FullName,
            current.State,
            after?.State ?? "MISSING",
            !verified,
            verified
                ? "Tarea habilitada de nuevo y snapshot retirado."
                : "La restauración no pudo verificarse.");
    }

    private IReadOnlyList<ScheduledTaskDescriptor> ReadTasks()
    {
        if (evaluationMode)
            return SyntheticTasks();

        var result = new List<ScheduledTaskDescriptor>();
        try
        {
            var scope = new ManagementScope(
                @"\\.\root\Microsoft\Windows\TaskScheduler");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery(
                    "SELECT TaskName, TaskPath, State, Actions FROM MSFT_ScheduledTask"));
            using var rows = searcher.Get();

            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var name = Convert.ToString(item["TaskName"]);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var path = NormalizeTaskPath(
                        Convert.ToString(item["TaskPath"]));
                    var fullName = path + name;
                    var action = ReadExecAction(item["Actions"]);

                    result.Add(new ScheduledTaskDescriptor(
                        MakeEntryId(fullName),
                        fullName,
                        name,
                        path,
                        NormalizeTaskState(item["State"]),
                        action.Execute,
                        action.Arguments));
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

        return result
            .DistinctBy(item => item.EntryId)
            .ToArray();
    }

    private static (string? Execute, string? Arguments) ReadExecAction(
        object? raw)
    {
        if (raw is not Array actions)
            return (null, null);

        foreach (var action in actions)
        {
            if (action is not ManagementBaseObject item)
                continue;

            var executeProperty = item.Properties
                .Cast<PropertyData>()
                .FirstOrDefault(property =>
                    property.Name.Equals(
                        "Execute",
                        StringComparison.OrdinalIgnoreCase));
            var execute = Convert.ToString(
                executeProperty?.Value);
            if (string.IsNullOrWhiteSpace(execute))
                continue;

            var argumentsProperty = item.Properties
                .Cast<PropertyData>()
                .FirstOrDefault(property =>
                    property.Name.Equals(
                        "Arguments",
                        StringComparison.OrdinalIgnoreCase));

            return (
                execute,
                Convert.ToString(
                    argumentsProperty?.Value));
        }

        return (null, null);
    }

    private static string NormalizeTaskState(object? raw)
    {
        if (raw is null)
            return "Unknown";

        if (raw is string text &&
            !int.TryParse(text, out _))
        {
            return text.Trim() switch
            {
                "Unknown" => "Unknown",
                "Disabled" => "Disabled",
                "Queued" => "Queued",
                "Ready" => "Ready",
                "Running" => "Running",
                _ => text.Trim()
            };
        }

        try
        {
            return Convert.ToInt32(raw) switch
            {
                0 => "Unknown",
                1 => "Disabled",
                2 => "Queued",
                3 => "Ready",
                4 => "Running",
                _ => "Unknown"
            };
        }
        catch
        {
            return "Unknown";
        }
    }

    private TaskProtection Classify(
        ScheduledTaskDescriptor task)
    {
        if (task.TaskPath.StartsWith(
                @"\Microsoft\",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                true,
                "Tarea bajo el namespace Microsoft; nunca se modifica automáticamente.");
        }

        if (task.State.Equals(
                "Running",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                true,
                "La tarea está ejecutándose; se evita modificarla durante su ejecución.");
        }

        var combined = string.Join(
            " ",
            task.FullName,
            task.Execute ?? string.Empty,
            task.Arguments ?? string.Empty);

        if (ProtectedKeywords.Any(keyword =>
                combined.Contains(
                    keyword,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new(
                true,
                "Protegida por política local: seguridad, Windows, nube, IA o automatización.");
        }

        var executable = ResolveExecutable(task.Execute);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new(
                true,
                "No se pudo identificar un ejecutable de forma segura.");
        }

        var windows = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        if (IsUnderRoot(executable, windows))
        {
            return new(
                true,
                "El ejecutable está dentro de Windows.");
        }

        if (!evaluationMode && !File.Exists(executable))
        {
            return new(
                true,
                "El ejecutable ya no existe; se conserva como evidencia y no se toca.");
        }

        return new(
            false,
            "Tarea de terceros revisable. Deshabilitarla afecta futuras ejecuciones y no mata procesos actuales.");
    }

    private static string NormalizeTaskPath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path)
            ? @"\"
            : path.Trim();
        if (!value.StartsWith('\\'))
            value = @"\" + value;
        if (!value.EndsWith('\\'))
            value += @"\";
        return value;
    }

    private static string? ResolveExecutable(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = Environment
            .ExpandEnvironmentVariables(raw.Trim());

        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1
                ? SafeFullPath(value[1..end])
                : null;
        }

        var exe = value.IndexOf(
            ".exe",
            StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
            return SafeFullPath(value[..(exe + 4)]);

        return SafeFullPath(value);
    }

    private static string? SafeFullPath(string value)
    {
        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnderRoot(
        string path,
        string root)
    {
        try
        {
            var p = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar);
            var r = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar);
            return p.Equals(
                    r,
                    StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(
                    r + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static string MakeEntryId(string fullName)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                fullName.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..24];
    }

    private static void ValidateEntryId(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId) ||
            entryId.Length != 24 ||
            !entryId.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Task ID no permitido.");
        }
    }

    private Dictionary<string, ScheduledTaskSnapshot> LoadState()
    {
        try
        {
            if (!File.Exists(statePath))
                return new(StringComparer.OrdinalIgnoreCase);

            var value = JsonSerializer.Deserialize<
                Dictionary<string, ScheduledTaskSnapshot>>(
                    File.ReadAllText(statePath),
                    HostBridge.JsonOptions);
            return value is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(value, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void PersistState(
        Dictionary<string, ScheduledTaskSnapshot> state)
    {
        var temp = statePath + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(
                state,
                HostBridge.JsonOptions));
        File.Move(temp, statePath, overwrite: true);
    }

    private static async Task<(int ExitCode, string Output)>
        RunSchtasksAsync(IReadOnlyList<string> arguments)
    {
        var system = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        var executable = Path.Combine(system, "schtasks.exe");
        if (!File.Exists(executable))
            return (-1, "schtasks.exe no está disponible.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return (-1, "No se pudo iniciar schtasks.exe.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(45));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return (-2, "Tiempo máximo excedido.");
        }

        var output = string.Join(
            " ",
            new[] { await stdout, await stderr }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
        if (output.Length > 1200)
            output = output[..1200] + "...";

        return (process.ExitCode, output);
    }

    private static IReadOnlyList<ScheduledTaskDescriptor>
        SyntheticTasks() =>
    [
        new ScheduledTaskDescriptor(
            MakeEntryId(@"\DemoVendor\Updater"),
            @"\DemoVendor\Updater",
            "Updater",
            @"\DemoVendor\",
            "Ready",
            @"C:\Program Files\DemoVendor\updater.exe",
            "--background"),
        new ScheduledTaskDescriptor(
            MakeEntryId(@"\Microsoft\Windows\Defrag\ScheduledDefrag"),
            @"\Microsoft\Windows\Defrag\ScheduledDefrag",
            "ScheduledDefrag",
            @"\Microsoft\Windows\Defrag\",
            "Ready",
            @"C:\Windows\System32\defrag.exe",
            null),
        new ScheduledTaskDescriptor(
            MakeEntryId(@"\Automation\PM2 resurrect"),
            @"\Automation\PM2 resurrect",
            "PM2 resurrect",
            @"\Automation\",
            "Ready",
            @"C:\Program Files\nodejs\node.exe",
            "pm2 resurrect")
    ];

    private sealed record ScheduledTaskDescriptor(
        string EntryId,
        string FullName,
        string TaskName,
        string TaskPath,
        string State,
        string? Execute,
        string? Arguments);

    private sealed record TaskProtection(
        bool Protected,
        string Reason);
}

public sealed record ScheduledTaskCandidate(
    string EntryId,
    string FullName,
    string TaskName,
    string TaskPath,
    string State,
    string? Execute,
    string? Arguments,
    bool Protected,
    string Reason,
    bool RestoreAvailable);

public sealed record ScheduledTaskPreview(
    int TaskCount,
    int EligibleCount,
    int ProtectedCount,
    int RestoreAvailableCount,
    IReadOnlyList<ScheduledTaskCandidate> Tasks);

public sealed record ScheduledTaskSnapshot(
    string EntryId,
    string FullName,
    string TaskName,
    string TaskPath,
    string State,
    string? Execute,
    string? Arguments,
    DateTimeOffset CapturedAt);

public sealed record ScheduledTaskChangeResult(
    bool Success,
    string Status,
    string EntryId,
    string FullName,
    string BeforeState,
    string AfterState,
    bool RestoreAvailable,
    string Detail);
