using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class PowerPlanTuningService
{
    private static readonly Regex GuidPattern =
        new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string statePath;
    private readonly Func<IReadOnlyList<string>, TimeSpan,
        Task<(int ExitCode, string Output)>> commandRunner;

    public PowerPlanTuningService(
        string? statePath = null,
        Func<IReadOnlyList<string>, TimeSpan,
            Task<(int ExitCode, string Output)>>? commandRunner = null)
    {
        if (string.IsNullOrWhiteSpace(statePath))
            AppPaths.EnsureDirectories();
        this.statePath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.PowerPlanState
            : Path.GetFullPath(statePath);
        this.commandRunner = commandRunner ?? RunPowerCfgAsync;
        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public async Task<PowerPlanChangeResult> SetBalancedAsync() =>
        await SetAsync("SCHEME_BALANCED", "BALANCED");

    public async Task<PowerPlanChangeResult> SetPerformanceAsync() =>
        await SetAsync("SCHEME_MIN", "HIGH_PERFORMANCE");

    public async Task<PowerPlanChangeResult> RestoreAsync()
    {
        var snapshot = Load();
        if (snapshot is null)
        {
            return new PowerPlanChangeResult(
                true,
                "NO_SNAPSHOT",
                null,
                null,
                false);
        }

        var before = await GetActiveGuidAsync();
        var set = await commandRunner(
            ["/setactive", snapshot.SchemeGuid],
            TimeSpan.FromSeconds(20));
        if (set.ExitCode != 0)
        {
            return new PowerPlanChangeResult(
                false,
                "RESTORE_FAILED",
                before,
                null,
                true);
        }

        var after = await GetActiveGuidAsync();
        var success = string.Equals(
            after,
            snapshot.SchemeGuid,
            StringComparison.OrdinalIgnoreCase);
        if (success)
        {
            try { File.Delete(statePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new PowerPlanChangeResult(
            success,
            success ? "RESTORED" : "VERIFY_FAILED",
            before,
            after,
            !success);
    }

    private async Task<PowerPlanChangeResult> SetAsync(
        string target,
        string status)
    {
        var before = await GetActiveGuidAsync();
        if (string.IsNullOrWhiteSpace(before))
        {
            return new PowerPlanChangeResult(
                false,
                "ACTIVE_PLAN_UNKNOWN",
                null,
                null,
                false);
        }

        if (Load() is null)
        {
            Persist(new PowerPlanSnapshot(
                before,
                DateTimeOffset.UtcNow));
        }

        var set = await commandRunner(
            ["/setactive", target],
            TimeSpan.FromSeconds(20));
        if (set.ExitCode != 0)
        {
            return new PowerPlanChangeResult(
                false,
                "SET_FAILED",
                before,
                null,
                true);
        }

        var after = await GetActiveGuidAsync();
        var verified = !string.IsNullOrWhiteSpace(after);
        var changed = verified &&
            !string.Equals(
                before,
                after,
                StringComparison.OrdinalIgnoreCase);
        return new PowerPlanChangeResult(
            verified,
            changed ? status : "ALREADY_ACTIVE",
            before,
            after,
            changed);
    }

    private async Task<string?> GetActiveGuidAsync()
    {
        var result = await commandRunner(
            ["/getactivescheme"],
            TimeSpan.FromSeconds(20));
        if (result.ExitCode != 0)
            return null;

        return GuidPattern.Match(result.Output) is { Success: true } match
            ? match.Value
            : null;
    }

    private static async Task<(int ExitCode, string Output)> RunPowerCfgAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.System),
            "powercfg.exe");
        if (!File.Exists(executable))
            return (-1, "powercfg.exe no encontrado.");

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
            return (-1, "No se pudo iniciar powercfg.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
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
            return (-2, "powercfg excedió el tiempo máximo.");
        }

        var output = (await stdout + Environment.NewLine + await stderr).Trim();
        return (process.ExitCode, output);
    }

    private PowerPlanSnapshot? Load()
    {
        try
        {
            if (!File.Exists(statePath))
                return null;
            return JsonSerializer.Deserialize<PowerPlanSnapshot>(
                File.ReadAllText(statePath),
                HostBridge.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void Persist(PowerPlanSnapshot snapshot)
    {
        var temp = statePath + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(snapshot, HostBridge.JsonOptions));
        File.Move(temp, statePath, overwrite: true);
    }

}

public sealed record PowerPlanSnapshot(
    string SchemeGuid,
    DateTimeOffset CapturedAt);

public sealed record PowerPlanChangeResult(
    bool Success,
    string Status,
    string? BeforeGuid,
    string? AfterGuid,
    bool RestoreAvailable);
