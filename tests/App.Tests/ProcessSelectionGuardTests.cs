using System.Diagnostics;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class ProcessSelectionGuardTests
{
    [Fact]
    public void MemoryTrimWrite_RequiresPriorPreview()
    {
        var service = new ProcessAnalysisService();

        var error = Assert.Throws<InvalidOperationException>(
            () => service.ValidateMemoryTrimSelection([12345]));

        Assert.Contains("análisis previo", error.Message);
    }

    [Fact]
    public void EcoQosWrite_RequiresPriorPreview()
    {
        var service = new ProcessAnalysisService();

        var error = Assert.Throws<InvalidOperationException>(
            () => service.ValidateEcoQosSelection([12345]));

        Assert.Contains("análisis previo", error.Message);
    }

    [Fact]
    public void Selection_RejectsPidOutsideLatestPreview()
    {
        var service = new ProcessAnalysisService();
        _ = service.AnalyzeMemoryTrimCandidates();

        var impossiblePid = int.MaxValue;
        var error = Assert.Throws<InvalidOperationException>(
            () => service.ValidateMemoryTrimSelection([impossiblePid]));

        Assert.Contains("no pertenece", error.Message);
    }

    [Fact]
    public async Task Tuning_RejectsReusedPidIdentityAtNativeHandleBoundary()
    {
        using var dataRoot = new TestDataRoot();
        var statePath = Path.Combine(
            dataRoot.Path,
            "State",
            "ecoqos.json");
        var service = new ProcessTuningService(
            new EcoQosStateStore(statePath));

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ??
                "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 6 >nul",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            var fakeIdentity = new ValidatedProcessTarget(
                process.Id,
                process.ProcessName,
                DateTimeOffset.UtcNow.AddDays(-1));

            var result = await service.TrimWorkingSetsAsync(
                [fakeIdentity]);

            Assert.Equal(1, result.Attempted);
            Assert.Equal(0, result.Succeeded);
            Assert.Contains(
                "reutilizado",
                result.Items.Single().Error,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

}
