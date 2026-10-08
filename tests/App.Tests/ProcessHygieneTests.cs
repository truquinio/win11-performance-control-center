using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class ProcessHygieneTests
{
    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public void HistoricalIncident_ClassifiesLabs_AndProtectsLiveAutomation()
    {
        var service = new ProcessHygieneService(
            evaluationMode: true);

        var report = service.Analyze();

        Assert.Equal(3, report.StoppableCount);
        Assert.Equal(2, report.ProtectedCount);
        Assert.True(report.EstimatedReclaimMb > 1700);

        var edge = Assert.Single(
            report.Items,
            item => item.Category == "HEADLESS_SIG_TEST");
        Assert.True(edge.Stoppable);
        Assert.False(edge.Protected);
        Assert.Contains(
            "sig-edge-headless-profile3",
            edge.Identity,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, edge.RelatedProcessIds.Count);

        var qemu = Assert.Single(
            report.Items,
            item => item.Category == "ANDROID_LAB");
        Assert.True(qemu.Stoppable);
        Assert.Contains(
            "hooklab",
            qemu.Identity,
            StringComparison.OrdinalIgnoreCase);

        var orphan = Assert.Single(
            report.Items,
            item => item.Category == "ORPHAN_UVICORN");
        Assert.True(orphan.Stoppable);
        Assert.Equal("127.0.0.1:8773", orphan.Identity);

        var activeDev = Assert.Single(
            report.Items,
            item => item.Category == "ACTIVE_DEV_SERVER");
        Assert.True(activeDev.Protected);
        Assert.False(activeDev.Stoppable);
        Assert.Equal("127.0.0.1:8877", activeDev.Identity);

        var playwright = Assert.Single(
            report.Items,
            item => item.Category == "PROTECTED_HEADLESS_BROWSER");
        Assert.True(playwright.Protected);
        Assert.False(playwright.Stoppable);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task Stop_RequiresFingerprintFromCurrentPreview()
    {
        var service = new ProcessHygieneService(
            evaluationMode: true);
        var preview = service.Analyze();
        var target = Assert.Single(
            preview.Items,
            item => item.Category == "ANDROID_LAB");

        var result = await service.StopAsync(
            target.ProcessId,
            target.Fingerprint);

        Assert.True(result.Success);
        Assert.Equal("EVALUATION", result.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StopAsync(
                target.ProcessId,
                "000000000000000000000000"));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HostBridge_InUseMode_BlocksHygieneStopBeforeMutation()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var mode = await RunAsync(
            bridge,
            "system.workload.inuse",
            new { confirmed = true });
        Assert.True(mode.Success);

        var preview = await RunAsync(
            bridge,
            "processes.hygiene.analyze");
        Assert.True(preview.Success);

        var report = Assert.IsType<ProcessHygieneReport>(
            preview.Data);
        var target = Assert.Single(
            report.Items,
            item => item.Category == "ANDROID_LAB");

        var blocked = await RunAsync(
            bridge,
            "processes.hygiene.stop",
            new
            {
                processId = target.ProcessId,
                fingerprint = target.Fingerprint,
                confirmed = true
            });

        Assert.False(blocked.Success);
        var data = JsonSerializer.SerializeToElement(
            blocked.Data,
            HostBridge.JsonOptions);
        Assert.True(data.GetProperty("blocked").GetBoolean());
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HostBridge_MaintenanceMode_AllowsOnlyPreclassifiedSyntheticStop()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var mode = await RunAsync(
            bridge,
            "system.workload.maintenance",
            new { confirmed = true });
        Assert.True(mode.Success);

        var preview = await RunAsync(
            bridge,
            "processes.hygiene.analyze");
        var report = Assert.IsType<ProcessHygieneReport>(
            preview.Data);
        var target = Assert.Single(
            report.Items,
            item => item.Category == "ORPHAN_UVICORN");

        var stopped = await RunAsync(
            bridge,
            "processes.hygiene.stop",
            new
            {
                processId = target.ProcessId,
                fingerprint = target.Fingerprint,
                confirmed = true
            });

        Assert.True(stopped.Success);
        var result = Assert.IsType<ProcessHygieneStopResult>(
            stopped.Data);
        Assert.Equal("EVALUATION", result.Status);
    }

    private static async Task<ActionResult> RunAsync(
        HostBridge bridge,
        string actionId,
        object? parameters = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = actionId
        };
        if (parameters is not null)
            payload["parameters"] = parameters;

        var request = JsonSerializer.Serialize(
            new
            {
                type = "request",
                requestId = Guid.NewGuid().ToString("N"),
                method = "actions.run",
                payload
            },
            HostBridge.JsonOptions);

        var response = await bridge.HandleAsync(request);
        Assert.True(response.Ok, response.Error);
        return Assert.IsType<ActionResult>(response.Result);
    }
}
