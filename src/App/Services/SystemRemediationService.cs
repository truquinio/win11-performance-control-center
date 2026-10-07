using System.IO;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class SystemRemediationService
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IntegrityTimeout = TimeSpan.FromMinutes(25);

    public async Task<SystemRemediationResult> RepairIntegrityAsync()
    {
        var windows = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        var dism = Path.Combine(windows, "System32", "dism.exe");
        var sfc = Path.Combine(windows, "System32", "sfc.exe");

        var restore = await RunAsync(
            dism,
            ["/Online", "/Cleanup-Image", "/RestoreHealth", "/English", "/NoRestart"],
            IntegrityTimeout);
        if (restore.ExitCode != 0)
        {
            return new SystemRemediationResult(
                false,
                "DISM_RESTORE_FAILED",
                false,
                [restore]);
        }

        var scan = await RunAsync(
            sfc,
            ["/scannow"],
            IntegrityTimeout);

        return new SystemRemediationResult(
            scan.ExitCode == 0,
            scan.ExitCode == 0 ? "REPAIRED_AND_VERIFIED" : "SFC_FAILED",
            false,
            [restore, scan]);
    }

    public async Task<SystemRemediationResult> FlushDnsAsync()
    {
        var system = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        var result = await RunAsync(
            Path.Combine(system, "ipconfig.exe"),
            ["/flushdns"],
            ShortTimeout);

        return new SystemRemediationResult(
            result.ExitCode == 0,
            result.ExitCode == 0 ? "DNS_FLUSHED" : "DNS_FLUSH_FAILED",
            false,
            [result]);
    }

    public async Task<SystemRemediationResult> ResetWinsockAsync()
    {
        var system = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        var result = await RunAsync(
            Path.Combine(system, "netsh.exe"),
            ["winsock", "reset"],
            ShortTimeout);

        return new SystemRemediationResult(
            result.ExitCode == 0,
            result.ExitCode == 0 ? "WINSOCK_RESET" : "WINSOCK_RESET_FAILED",
            result.ExitCode == 0,
            [result]);
    }

    public async Task<SystemRemediationResult> RescanDevicesAsync()
    {
        var system = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        var result = await RunAsync(
            Path.Combine(system, "pnputil.exe"),
            ["/scan-devices"],
            ShortTimeout);

        return new SystemRemediationResult(
            result.ExitCode == 0,
            result.ExitCode == 0 ? "DEVICES_RESCANNED" : "DEVICE_RESCAN_FAILED",
            false,
            [result]);
    }

    public Task<SystemRemediationResult> RestartAudioAsync() =>
        RestartServicesAsync(
            ["Audiosrv"],
            "AUDIO_RESTARTED");

    public Task<SystemRemediationResult> RestartWindowsUpdateServicesAsync() =>
        RestartServicesAsync(
            ["BITS", "wuauserv"],
            "WINDOWS_UPDATE_SERVICES_RESTARTED");

    public Task<SystemRemediationResult> RestartExplorerAsync() =>
        Task.Run(async () =>
        {
            var before = Process.GetProcessesByName("explorer");
            try
            {
                foreach (var process in before)
                {
                    try
                    {
                        process.Kill(entireProcessTree: false);
                        await process.WaitForExitAsync();
                    }
                    catch (Exception ex) when (ex is
                        InvalidOperationException or
                        System.ComponentModel.Win32Exception or
                        NotSupportedException)
                    {
                    }
                }
            }
            finally
            {
                foreach (var process in before)
                    process.Dispose();
            }

            var explorer = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows),
                "explorer.exe");
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                UseShellExecute = true
            });
            if (started is null)
            {
                return new SystemRemediationResult(
                    false,
                    "EXPLORER_START_FAILED",
                    false,
                    []);
            }

            var verified = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250);
                var current = Process.GetProcessesByName("explorer");
                try
                {
                    if (current.Length > 0)
                    {
                        verified = true;
                        break;
                    }
                }
                finally
                {
                    foreach (var process in current)
                        process.Dispose();
                }
            }

            return new SystemRemediationResult(
                verified,
                verified ? "EXPLORER_RESTARTED" : "EXPLORER_VERIFY_FAILED",
                false,
                []);
        });

    private static async Task<SystemRemediationResult> RestartServicesAsync(
        IReadOnlyList<string> names,
        string successStatus)
    {
        var steps = new List<RemediationStep>();
        var success = true;

        foreach (var name in names)
        {
            var outcome = await RestartServiceAsync(name);
            steps.Add(outcome);
            success &= outcome.ExitCode == 0;
        }

        return new SystemRemediationResult(
            success,
            success ? successStatus : "SERVICE_RESTART_PARTIAL",
            false,
            steps);
    }

    private static Task<RemediationStep> RestartServiceAsync(string name) =>
        Task.Run(async () =>
        {
            var descriptor = ReadService(name);
            if (descriptor is null)
            {
                return new RemediationStep(
                    "service:" + name,
                    1060,
                    "Servicio no encontrado.",
                    0);
            }

            var stopwatch = Stopwatch.StartNew();
            uint stopCode = 0;
            if (descriptor.State.Equals(
                    "Running",
                    StringComparison.OrdinalIgnoreCase))
            {
                stopCode = InvokeServiceMethod(name, "StopService");
                if (stopCode != 0)
                {
                    stopwatch.Stop();
                    return new RemediationStep(
                        "service:" + name,
                        unchecked((int)stopCode),
                        "StopService rechazado.",
                        stopwatch.ElapsedMilliseconds);
                }

                await WaitForServiceStateAsync(
                    name,
                    "Stopped",
                    TimeSpan.FromSeconds(20));
            }

            var startCode = InvokeServiceMethod(name, "StartService");
            if (startCode is not (0 or 10))
            {
                stopwatch.Stop();
                return new RemediationStep(
                    "service:" + name,
                    unchecked((int)startCode),
                    "StartService rechazado.",
                    stopwatch.ElapsedMilliseconds);
            }

            var running = await WaitForServiceStateAsync(
                name,
                "Running",
                TimeSpan.FromSeconds(20));
            stopwatch.Stop();

            return new RemediationStep(
                "service:" + name,
                running ? 0 : 1,
                running
                    ? "Servicio reiniciado y verificado."
                    : "No alcanzó estado Running dentro del límite.",
                stopwatch.ElapsedMilliseconds);
        });

    private static ServiceState? ReadService(string name)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, State FROM Win32_Service");
            using var results = searcher.Get();
            foreach (var raw in results)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;
                    var candidate = Convert.ToString(item["Name"]);
                    if (!string.Equals(
                            candidate,
                            name,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    return new ServiceState(
                        candidate ?? name,
                        Convert.ToString(item["State"]) ?? "Unknown");
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

        return null;
    }

    private static uint InvokeServiceMethod(string name, string method)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name FROM Win32_Service");
        using var results = searcher.Get();
        foreach (var raw in results)
        {
            using (raw)
            {
                if (raw is not ManagementObject item)
                    continue;
                if (!string.Equals(
                        Convert.ToString(item["Name"]),
                        name,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                var result = item.InvokeMethod(method, null);
                return result is null
                    ? uint.MaxValue
                    : Convert.ToUInt32(result);
            }
        }

        return 1060;
    }

    private static async Task<bool> WaitForServiceStateAsync(
        string name,
        string expected,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var current = ReadService(name);
            if (current is not null &&
                current.State.Equals(
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private static async Task<RemediationStep> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        if (!File.Exists(executable))
        {
            return new RemediationStep(
                Path.GetFileName(executable),
                -1,
                "Ejecutable no encontrado.",
                0);
        }

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
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            return new RemediationStep(
                Path.GetFileName(executable),
                -1,
                "No se pudo iniciar el proceso.",
                0);
        }

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

            stopwatch.Stop();
            return new RemediationStep(
                Path.GetFileName(executable),
                -2,
                "Tiempo máximo excedido.",
                stopwatch.ElapsedMilliseconds);
        }

        stopwatch.Stop();
        var output = Compact(
            await stdout,
            await stderr);

        return new RemediationStep(
            Path.GetFileName(executable),
            process.ExitCode,
            output,
            stopwatch.ElapsedMilliseconds);
    }

    private static string Compact(string stdout, string stderr)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout))
            builder.AppendLine(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            builder.AppendLine(stderr.Trim());

        var value = builder
            .ToString()
            .Replace("\r", " ")
            .Replace("\n", " ");
        while (value.Contains("  ", StringComparison.Ordinal))
            value = value.Replace("  ", " ", StringComparison.Ordinal);

        return value.Length <= 2400
            ? value
            : value[..2400] + "...";
    }

    private sealed record ServiceState(
        string Name,
        string State);
}

public sealed record RemediationStep(
    string Step,
    int ExitCode,
    string Output,
    long DurationMilliseconds);

public sealed record SystemRemediationResult(
    bool Success,
    string Status,
    bool RebootRequired,
    IReadOnlyList<RemediationStep> Steps);
