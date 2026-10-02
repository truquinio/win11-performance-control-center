using System.Diagnostics;
using System.IO;
using System.Text;

namespace Win11PerformanceControlCenter.App.Services;

public sealed record IntegrityCheckResult(
    int ExitCode,
    string Status,
    string Output,
    TimeSpan Duration);

public sealed class IntegrityService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<IntegrityCheckResult> CheckHealthAsync()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var dism = Path.Combine(windows, "System32", "dism.exe");
        if (!File.Exists(dism))
            throw new FileNotFoundException("No se encontró DISM.", dism);

        var startInfo = new ProcessStartInfo
        {
            FileName = dism,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("/Online");
        startInfo.ArgumentList.Add("/Cleanup-Image");
        startInfo.ArgumentList.Add("/CheckHealth");
        startInfo.ArgumentList.Add("/English");
        startInfo.ArgumentList.Add("/NoRestart");

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException("No se pudo iniciar DISM.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(Timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException("DISM /CheckHealth superó el tiempo máximo.");
        }

        stopwatch.Stop();
        var output = Compact(await stdout, await stderr);

        return new IntegrityCheckResult(
            process.ExitCode,
            Classify(process.ExitCode, output),
            output,
            stopwatch.Elapsed);
    }

    private static string Classify(int exitCode, string output)
    {
        if (exitCode != 0) return "ERROR";
        if (output.Contains("No component store corruption detected",
                StringComparison.OrdinalIgnoreCase))
            return "OK";
        if (output.Contains("component store is repairable",
                StringComparison.OrdinalIgnoreCase))
            return "REPAIRABLE";
        if (output.Contains("component store cannot be repaired",
                StringComparison.OrdinalIgnoreCase))
            return "UNREPAIRABLE";
        return "UNKNOWN";
    }

    private static string Compact(string stdout, string stderr)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout)) builder.AppendLine(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) builder.AppendLine(stderr.Trim());
        var value = builder.ToString().Replace("\r", " ").Replace("\n", " ");
        while (value.Contains("  ", StringComparison.Ordinal))
            value = value.Replace("  ", " ", StringComparison.Ordinal);
        return value.Length <= 1200 ? value : value[..1200] + "…";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Timeout cleanup is best-effort and must not hide the original timeout.
        }
    }
}
