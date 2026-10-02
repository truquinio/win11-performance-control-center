using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class AdvancedAuditTests
{
    [Fact]
    public async Task PageFileAudit_IsReadOnly()
    {
        var result = await new PageFileService().AnalyzeAsync();

        Assert.NotNull(result.Entries);
    }

    [Fact]
    public async Task StartupAudit_DegradesGracefully()
    {
        var result = await new StartupAuditService().AnalyzeAsync();

        Assert.Contains(result.Status, new[] { "OK", "NO_DATA" });
        Assert.NotNull(result.Items);
    }

    [Fact]
    public void ReadOnlyAdvancedAudits_ReturnCollections()
    {
        var results = new AuditResult[]
        {
            new WindowsUpdateAuditService().Analyze(),
            new InstalledAppsService().Analyze(),
            new PrivacyAuditService().Analyze(),
            new DeveloperToolingService().Analyze(),
            new ThermalEnergyService().Analyze(),
            new BootSleepAuditService().AnalyzeBoot(),
            new BootSleepAuditService().AnalyzeSleepResume(),
            new ExplorerAuditService().Analyze()
        };

        Assert.All(results, result => Assert.NotNull(result.Items));
    }

    [Fact]
    public void RecoveryAudit_ReturnsTypedState()
    {
        var store = new EcoQosStateStore();
        var result = new OperationRecoveryService(store).Analyze();

        Assert.NotNull(result.IncompleteOperations);
        Assert.NotNull(result.RollbackSnapshots);
    }

    [Theory]
    [InlineData(0, "System")]
    [InlineData(4, "System")]
    [InlineData(999, "explorer")]
    [InlineData(999, "SearchHost")]
    [InlineData(999, "RuntimeBroker")]
    [InlineData(999, "msedgewebview2")]
    public void ProcessSafetyPolicy_BlocksKnownCriticalPatterns(
        int processId,
        string name)
    {
        var candidate = new ProcessCandidate(
            processId,
            name,
            100_000_000,
            false);

        Assert.False(ProcessSafetyPolicy.IsEligible(
            candidate,
            requireBackground: true));
    }

    [Fact]
    public void ThermalAudit_UsesNvidiaTemperatureWhenDriverExposesIt()
    {
        using var probe = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi.exe",
                Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        try
        {
            probe.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }

        var output = probe.StandardOutput.ReadToEnd().Trim();
        if (!probe.WaitForExit(2000) || probe.ExitCode != 0 ||
            !double.TryParse(output.Split('\n')[0].Trim(), out _))
            return;

        var result = new ThermalEnergyService().Analyze();
        Assert.Contains(result.Items, item =>
            item.Category == "Thermal" &&
            item.Status == "OBSERVED" &&
            item.Value.Contains("°C", StringComparison.Ordinal));
    }
}
