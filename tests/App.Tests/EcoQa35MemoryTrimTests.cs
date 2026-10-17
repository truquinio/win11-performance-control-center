using System.Diagnostics;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class EcoQa35MemoryTrimTests
{
    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task MemoryTrim_OnlyTrimsAnOwnedDisposableChildProcess()
    {
        using var root = new TestDataRoot();
        var pingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "PING.EXE");
        Assert.True(File.Exists(pingPath), "Windows PING.EXE is required.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = pingPath,
            Arguments = "127.0.0.1 -n 20 -w 100",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        Assert.NotNull(process);

        try
        {
            var target = new ValidatedProcessTarget(
                process.Id,
                process.ProcessName,
                new DateTimeOffset(process.StartTime.ToUniversalTime()));
            var rollbackPath = Path.Combine(root.Path, "State", "ecoqos.json");
            var service = new ProcessTuningService(
                new EcoQosStateStore(rollbackPath));

            var result = await service.TrimWorkingSetsAsync([target]);

            Assert.Equal(1, result.Attempted);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
            var item = Assert.Single(result.Items);
            Assert.True(item.Success, item.Error);
            Assert.Equal(process.Id, item.ProcessId);
            Assert.False(process.HasExited);
            Assert.False(File.Exists(rollbackPath));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
