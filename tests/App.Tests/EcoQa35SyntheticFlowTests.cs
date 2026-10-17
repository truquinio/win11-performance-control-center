using System.Security.Cryptography;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace App.Tests;

/// <summary>
/// Positive WRITE integration exercises limited to per-test files under the
/// dedicated TestDataRoot. Never operates on a live Windows service or profile.
/// </summary>
public sealed class EcoQa35SyntheticFlowTests
{
    [Theory]
    [InlineData("system.workload.inuse", "IN_USE")]
    [InlineData("system.workload.auto", "AUTO")]
    [InlineData("system.workload.maintenance", "MAINTENANCE")]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task WorkloadModes_RoundTripInAnIsolatedFixture(
        string actionId, string expectedMode)
    {
        using var root = new TestDataRoot();
        using var bridge = root.CreateBridge();

        var changed = await InvokeAsync(bridge, actionId);
        Assert.True(changed.Ok, changed.Error);
        var outcome = Assert.IsType<ActionResult>(changed.Result);
        Assert.True(outcome.Success, outcome.Message);

        var path = Path.Combine(root.Path, "State", "workload-mode.json");
        Assert.True(File.Exists(path));
        using var persisted = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(expectedMode,
            persisted.RootElement.GetProperty("mode").GetString());

        var status = await InvokeAsync(bridge, "system.workload.status", confirmed: false);
        Assert.True(status.Ok, status.Error);
    }

    [Theory]
    [InlineData("maintenance.policy.readonly", "READ_ONLY_IDLE")]
    [InlineData("maintenance.policy.off", "OFF")]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task MaintenanceMode_WritesOnlySyntheticPolicyState(
        string actionId, string expectedMode)
    {
        using var root = new TestDataRoot();
        using var bridge = root.CreateBridge();

        var changed = await InvokeAsync(bridge, actionId);
        Assert.True(changed.Ok, changed.Error);
        var outcome = Assert.IsType<ActionResult>(changed.Result);
        Assert.True(outcome.Success, outcome.Message);

        var path = Path.Combine(root.Path, "State", "maintenance-policy.json");
        Assert.True(File.Exists(path));
        using var persisted = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(expectedMode,
            persisted.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task EdgeQuarantineAndRestore_RoundTripSyntheticContentExactly()
    {
        using var root = new TestDataRoot();
        const string extensionId = "abcdefghijklmnopabcdefghijklmnop";
        var folder = Path.Combine(root.Path, "EdgeUserData", "Default",
            "Local Extension Settings", extensionId);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "synthetic.bin");
        var bytes = new byte[2048];
        RandomNumberGenerator.Fill(bytes);
        await File.WriteAllBytesAsync(file, bytes);
        var baseline = Convert.ToHexString(SHA256.HashData(bytes));

        using var bridge = root.CreateBridge();

        var quarantine = await InvokeAsync(
            bridge, "browsers.extensions.orphans.quarantine");
        Assert.True(quarantine.Ok, quarantine.Error);
        var result = Assert.IsType<ActionResult>(quarantine.Result);
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(file));

        var rollback = await InvokeAsync(
            bridge, "browsers.extensions.orphans.restore");
        Assert.True(rollback.Ok, rollback.Error);
        var restored = Assert.IsType<ActionResult>(rollback.Result);
        Assert.True(restored.Success, restored.Message);
        Assert.True(File.Exists(file));
        Assert.Equal(baseline,
            Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(file))));
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task EdgePerformanceProfile_RestoresWithoutChangingUserRegistry()
    {
        using var root = new TestDataRoot();
        using var bridge = root.CreateBridge();

        var applied = await InvokeAsync(
            bridge, "browsers.edge.performance.optimize");
        Assert.True(applied.Ok, applied.Error);
        var optimized = Assert.IsType<ActionResult>(applied.Result);
        Assert.True(optimized.Success, optimized.Message);
        var fixturePath = Path.Combine(
            root.Path, "State", "edge-performance.json");
        Assert.True(File.Exists(fixturePath));

        var restored = await InvokeAsync(
            bridge, "browsers.edge.performance.restore");
        Assert.True(restored.Ok, restored.Error);
        var reverted = Assert.IsType<ActionResult>(restored.Result);
        Assert.True(reverted.Success, reverted.Message);
        Assert.False(File.Exists(fixturePath));
    }

    private static Task<BridgeResponse> InvokeAsync(
        HostBridge bridge, string actionId, bool confirmed = true)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "eco-qa-" + Guid.NewGuid().ToString("N"),
            method = "actions.run",
            payload = confirmed
                ? new { id = actionId, parameters = (object)new { confirmed = true } }
                : new { id = actionId, parameters = (object)new { } }
        });
        return bridge.HandleAsync(request);
    }
}
