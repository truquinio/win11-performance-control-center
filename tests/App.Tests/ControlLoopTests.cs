using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class ControlLoopTests
{
    [Fact]
    public void WorkloadGuard_ProtectsActivePc_AndAllowsMaintenance()
    {
        using var root = new TestDataRoot();
        var idle = TimeSpan.Zero;
        var freePercent = 50d;
        var guard = new WorkloadGuardService(
            Path.Combine(root.Path, "workload.json"),
            evaluationMode: false,
            idleProvider: () => idle,
            dFreeProvider: () => freePercent);

        var automaticBusy = guard.GetStatus();
        Assert.Equal("AUTO", automaticBusy.ConfiguredMode);
        Assert.Equal("IN_USE", automaticBusy.EffectiveMode);
        Assert.False(automaticBusy.HeavyActionsAllowed);
        Assert.True(guard.ShouldBlock("system.integrity.repair"));

        var maintenance = guard.SetMode("MAINTENANCE");
        Assert.Equal("MAINTENANCE", maintenance.EffectiveMode);
        Assert.True(maintenance.HeavyActionsAllowed);
        Assert.False(guard.ShouldBlock("system.integrity.repair"));

        var protectedMode = guard.SetMode("IN_USE");
        Assert.False(protectedMode.HeavyActionsAllowed);

        idle = TimeSpan.FromMinutes(20);
        var automaticIdle = guard.SetMode("AUTO");
        Assert.Equal("IDLE", automaticIdle.EffectiveMode);
        Assert.True(automaticIdle.HeavyActionsAllowed);
    }

    [Fact]
    public void WorkloadGuard_CriticalD_BlocksEvenMaintenance()
    {
        using var root = new TestDataRoot();
        var guard = new WorkloadGuardService(
            Path.Combine(root.Path, "workload.json"),
            evaluationMode: false,
            idleProvider: () => TimeSpan.FromHours(1),
            dFreeProvider: () => 2.5d);

        var status = guard.SetMode("MAINTENANCE");

        Assert.True(status.StorageCritical);
        Assert.False(status.HeavyActionsAllowed);
        Assert.True(guard.ShouldBlock("disk.cleanup.execute"));
    }

    [Fact]
    public void RollbackCenter_AggregatesAppOwnedRollbackState()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "State");
        var quarantine = Path.Combine(root.Path, "Quarantine", "EdgeExtensions");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(Path.Combine(quarantine, "20261007T180000000Z-demo"));

        var eco = new EcoQosStateStore(
            Path.Combine(state, "ecoqos.json"));
        var recovery = new OperationRecoveryService(
            eco,
            Path.Combine(root.Path, "Logs", "app.jsonl"));

        var pageFile = Path.Combine(state, "pagefile.json");
        var power = Path.Combine(state, "power-plan.json");
        var services = Path.Combine(state, "service-startup.json");
        File.WriteAllText(pageFile, "{\"snapshot\":true}");
        File.WriteAllText(power, "{\"snapshot\":true}");
        File.WriteAllText(
            services,
            "{\"DemoVendorSvc\":{\"startMode\":\"Auto\"}}");

        var center = new RollbackCenterService(
            recovery,
            pageFile,
            power,
            services,
            quarantine);

        var report = center.Analyze();

        Assert.Contains(report.Entries, item => item.Id == "pagefile");
        Assert.Contains(report.Entries, item => item.Id == "power-plan");
        Assert.Contains(report.Entries, item => item.Id == "service:DemoVendorSvc");
        Assert.Contains(report.Entries, item => item.Id.StartsWith("edge:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HostBridge_InUseMode_BlocksHeavyActionWithoutTouchingFixture()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var mode = await RunAsync(
            bridge,
            "system.workload.inuse",
            new { confirmed = true });
        Assert.True(mode.Success);

        var blocked = await RunAsync(
            bridge,
            "disk.hotspots.scan");

        Assert.False(blocked.Success);
        var data = Assert.IsType<JsonElement>(blocked.Data);
        Assert.True(data.GetProperty("blocked").GetBoolean());
        Assert.False(
            data.GetProperty("workload")
                .GetProperty("heavyActionsAllowed")
                .GetBoolean());
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HostBridge_ActionPlanAndRollbackCenter_AreExecutableReads()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var plan = await RunAsync(
            bridge,
            "system.actionplan.preview");
        var rollback = await RunAsync(
            bridge,
            "backup.rollback.center");

        Assert.True(plan.Success);
        Assert.True(rollback.Success);
        Assert.IsType<JsonElement>(plan.Data);
        Assert.IsType<JsonElement>(rollback.Data);
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
