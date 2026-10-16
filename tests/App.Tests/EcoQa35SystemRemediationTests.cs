using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class EcoQa35SystemRemediationTests
{
    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task GlobalCommandWrites_UseOnlyInjectedRunnerAndReportVerifiedOutcomes()
    {
        var calls = new List<(string Executable, string[] Arguments, TimeSpan Timeout)>();
        var service = new SystemRemediationService((executable, arguments, timeout) =>
        {
            calls.Add((executable, arguments.ToArray(), timeout));
            return Task.FromResult(new RemediationStep(
                Path.GetFileName(executable), 0, "synthetic success", 1));
        });

        var integrity = await service.RepairIntegrityAsync();
        var dns = await service.FlushDnsAsync();
        var winsock = await service.ResetWinsockAsync();
        var devices = await service.RescanDevicesAsync();

        Assert.True(integrity.Success, integrity.Status);
        Assert.Equal("REPAIRED_AND_VERIFIED", integrity.Status);
        Assert.True(dns.Success, dns.Status);
        Assert.Equal("DNS_FLUSHED", dns.Status);
        Assert.True(winsock.Success, winsock.Status);
        Assert.Equal("WINSOCK_RESET", winsock.Status);
        Assert.True(winsock.RebootRequired);
        Assert.True(devices.Success, devices.Status);
        Assert.Equal("DEVICES_RESCANNED", devices.Status);
        Assert.Equal(5, calls.Count);

        Assert.Equal("dism.exe", Path.GetFileName(calls[0].Executable));
        Assert.Equal(["/Online", "/Cleanup-Image", "/RestoreHealth", "/English", "/NoRestart"], calls[0].Arguments);
        Assert.Equal("sfc.exe", Path.GetFileName(calls[1].Executable));
        Assert.Equal(["/scannow"], calls[1].Arguments);
        Assert.Equal("ipconfig.exe", Path.GetFileName(calls[2].Executable));
        Assert.Equal(["/flushdns"], calls[2].Arguments);
        Assert.Equal("netsh.exe", Path.GetFileName(calls[3].Executable));
        Assert.Equal(["winsock", "reset"], calls[3].Arguments);
        Assert.Equal("pnputil.exe", Path.GetFileName(calls[4].Executable));
        Assert.Equal(["/scan-devices"], calls[4].Arguments);
        Assert.Equal(TimeSpan.FromMinutes(25), calls[0].Timeout);
        Assert.Equal(TimeSpan.FromMinutes(25), calls[1].Timeout);
        Assert.All(calls.Skip(2), call => Assert.Equal(TimeSpan.FromMinutes(2), call.Timeout));
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task InjectedTimeout_FailsClosedAndRetryCanRecover()
    {
        var calls = 0;
        var service = new SystemRemediationService((executable, _, _) =>
        {
            calls++;
            var exitCode = calls == 1 ? -2 : 0;
            return Task.FromResult(new RemediationStep(
                Path.GetFileName(executable), exitCode,
                calls == 1 ? "simulated timeout" : "synthetic success", 1));
        });

        var timedOut = await service.ResetWinsockAsync();
        Assert.False(timedOut.Success);
        Assert.Equal("WINSOCK_RESET_FAILED", timedOut.Status);
        Assert.False(timedOut.RebootRequired);

        var recovered = await service.ResetWinsockAsync();
        Assert.True(recovered.Success, recovered.Status);
        Assert.Equal("WINSOCK_RESET", recovered.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task IntegrityRestoreFailure_DoesNotStartSfc()
    {
        var calls = new List<string>();
        var service = new SystemRemediationService((executable, _, _) =>
        {
            calls.Add(Path.GetFileName(executable));
            return Task.FromResult(new RemediationStep(
                Path.GetFileName(executable), 8, "simulated failure", 1));
        });

        var result = await service.RepairIntegrityAsync();

        Assert.False(result.Success);
        Assert.Equal("DISM_RESTORE_FAILED", result.Status);
        Assert.Equal(["dism.exe"], calls);
        Assert.Single(result.Steps);
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task ServiceRestartWrites_UseOnlyInjectedRestartRunner()
    {
        var calls = new List<string>();
        var service = new SystemRemediationService(
            serviceRestartRunner: name =>
            {
                calls.Add(name);
                return Task.FromResult(new RemediationStep(
                    "service:" + name, 0, "synthetic success", 1));
            });

        var audio = await service.RestartAudioAsync();
        var updates = await service.RestartWindowsUpdateServicesAsync();

        Assert.True(audio.Success, audio.Status);
        Assert.Equal("AUDIO_RESTARTED", audio.Status);
        Assert.True(updates.Success, updates.Status);
        Assert.Equal("WINDOWS_UPDATE_SERVICES_RESTARTED", updates.Status);
        Assert.Equal(["Audiosrv", "BITS", "wuauserv"], calls);
        Assert.False(audio.RebootRequired);
        Assert.False(updates.RebootRequired);
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task ServiceRestartFailure_ReportsPartialAndAttemptsRemainingService()
    {
        var calls = new List<string>();
        var service = new SystemRemediationService(
            serviceRestartRunner: name =>
            {
                calls.Add(name);
                var code = name == "BITS" ? 5 : 0;
                return Task.FromResult(new RemediationStep(
                    "service:" + name, code, "synthetic result", 1));
            });

        var result = await service.RestartWindowsUpdateServicesAsync();

        Assert.False(result.Success);
        Assert.Equal("SERVICE_RESTART_PARTIAL", result.Status);
        Assert.Equal(["BITS", "wuauserv"], calls);
        Assert.All(result.Steps, step => Assert.StartsWith("service:", step.Step));
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task HibernateReduction_UsesInjectedPowerCfgAndReportsTimeout()
    {
        var calls = new List<(string Executable, string[] Arguments, TimeSpan Timeout)>();
        var exitCode = 0;
        var service = new SystemRemediationService((executable, arguments, timeout) =>
        {
            calls.Add((executable, arguments.ToArray(), timeout));
            return Task.FromResult(new RemediationStep(
                Path.GetFileName(executable), exitCode, "synthetic", 1));
        });

        var applied = await service.ReduceHibernateAsync();
        Assert.True(applied.Success, applied.Status);
        Assert.Equal("HIBERNATION_REDUCED", applied.Status);
        Assert.Equal("powercfg.exe", Path.GetFileName(calls[0].Executable));
        Assert.Equal(["/h", "/type", "reduced"], calls[0].Arguments);
        Assert.Equal(TimeSpan.FromSeconds(25), calls[0].Timeout);

        exitCode = -2;
        var timedOut = await service.ReduceHibernateAsync();
        Assert.False(timedOut.Success);
        Assert.Equal("HIBERNATION_REDUCTION_FAILED", timedOut.Status);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task ExplorerRestart_UsesOnlyInjectedRunnerAndCanRecover()
    {
        var calls = 0;
        var service = new SystemRemediationService(
            explorerRestartRunner: () =>
            {
                calls++;
                var success = calls > 1;
                return Task.FromResult(new SystemRemediationResult(
                    success,
                    success ? "EXPLORER_RESTARTED" : "EXPLORER_VERIFY_FAILED",
                    false,
                    []));
            });

        var failed = await service.RestartExplorerAsync();
        Assert.False(failed.Success);
        Assert.Equal("EXPLORER_VERIFY_FAILED", failed.Status);

        var recovered = await service.RestartExplorerAsync();
        Assert.True(recovered.Success, recovered.Status);
        Assert.Equal("EXPLORER_RESTARTED", recovered.Status);
        Assert.Equal(2, calls);
    }
}
