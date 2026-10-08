using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class EdgePerformanceTests
{
    [Fact]
    public void EvaluationProfile_IsReversible_AndDoesNotTouchHostRegistry()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "edge-performance.json");
        var service = new EdgePerformanceService(
            state,
            evaluationMode: true);

        var before = service.Analyze();
        Assert.False(before.Optimized);
        Assert.Equal(1, before.AutoLaunchEntryCount);
        Assert.False(before.RestoreAvailable);

        var applied = service.Optimize();
        Assert.True(applied.Success);
        Assert.Equal("OPTIMIZED", applied.Status);
        Assert.True(applied.Report.Optimized);
        Assert.True(applied.RestoreAvailable);
        Assert.True(File.Exists(state));

        var restored = service.Restore();
        Assert.True(restored.Success);
        Assert.Equal("RESTORED", restored.Status);
        Assert.False(restored.Report.Optimized);
        Assert.False(restored.RestoreAvailable);
        Assert.False(File.Exists(state));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HostBridge_EdgePerformance_FlowsThroughRollbackCenter()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var before = await RunAsync(
            bridge,
            "browsers.edge.performance.audit");
        Assert.True(before.Success);
        var beforeReport = Assert.IsType<EdgePerformanceReport>(
            before.Data);
        Assert.False(beforeReport.Optimized);

        var optimized = await RunAsync(
            bridge,
            "browsers.edge.performance.optimize",
            new { confirmed = true });
        Assert.True(optimized.Success);
        var optimizedResult =
            Assert.IsType<EdgePerformanceChangeResult>(
                optimized.Data);
        Assert.True(optimizedResult.Report.Optimized);

        var rollback = await RunAsync(
            bridge,
            "backup.rollback.center");
        Assert.True(rollback.Success);
        var rollbackReport = Assert.IsType<RollbackCenterReport>(
            rollback.Data);
        Assert.Contains(
            rollbackReport.Entries,
            item =>
                item.Id == "edge-performance" &&
                item.ActionId ==
                    "browsers.edge.performance.restore");

        var restored = await RunAsync(
            bridge,
            "browsers.edge.performance.restore",
            new { confirmed = true });
        Assert.True(restored.Success);

        var afterRollback = await RunAsync(
            bridge,
            "backup.rollback.center");
        var afterReport = Assert.IsType<RollbackCenterReport>(
            afterRollback.Data);
        Assert.DoesNotContain(
            afterReport.Entries,
            item => item.Id == "edge-performance");
    }

    [Fact]
    public void EdgePerformanceWriteActions_HaveOutcomePolicies()
    {
        var policies =
            OutcomeAuditService.KnownPoliciesForTests();

        Assert.Contains(
            "browsers.edge.performance.optimize",
            policies.Keys);
        Assert.Contains(
            "browsers.edge.performance.restore",
            policies.Keys);
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
