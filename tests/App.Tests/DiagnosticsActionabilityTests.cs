using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class DiagnosticsActionabilityTests
{
    [Fact]
    public async Task CrashIntelligence_GroupsEvidenceWithoutInventingCause()
    {
        var now = DateTimeOffset.UtcNow;
        var events = new[]
        {
            new ReliabilityEventDto(
                now,
                18,
                "Microsoft-Windows-WHEA-Logger",
                EvidenceClassification.HECHO,
                "WHEA test event"),
            new ReliabilityEventDto(
                now.AddMinutes(-1),
                1002,
                "Application Hang",
                EvidenceClassification.HECHO,
                "explorer.exe stopped responding"),
            new ReliabilityEventDto(
                now.AddMinutes(-2),
                41,
                "Microsoft-Windows-Kernel-Power",
                EvidenceClassification.INDICIO,
                "Unexpected shutdown")
        };

        var service = new CrashIntelligenceService(
            _ => Task.FromResult<IReadOnlyList<ReliabilityEventDto>>(events));

        var report = await service.AnalyzeAsync();

        Assert.Equal(3, report.EventsRead);
        Assert.Contains(report.Insights, item =>
            item.Title == "Eventos WHEA" &&
            item.RecommendedActionId == "drivers.analyze" &&
            item.Severity == "HIGH");
        Assert.Contains(report.Insights, item =>
            item.Title == "Aplicaciones sin responder" &&
            item.RecommendedActionId == "explorer.audit");
        Assert.Contains(report.Insights, item =>
            item.Title == "Apagados o reinicios no limpios" &&
            item.RecommendedActionId == "system.health.scan");
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task UsbRepairCenter_RestartsOnlyEligibleSyntheticDevice()
    {
        var service = new UsbDiagnosticsService(evaluationMode: true);
        var report = await service.AnalyzeAsync();

        Assert.Equal(2, report.DeviceCount);
        Assert.Equal(1, report.ProblemCount);
        var camera = Assert.Single(report.Devices.Where(item =>
            item.RestartEligible));
        Assert.Contains("Camera", camera.Name, StringComparison.Ordinal);

        var result = await service.RestartAsync(camera.DeviceInstanceId);

        Assert.True(result.Success);
        Assert.Equal("EVALUATION", result.Status);

        var hub = Assert.Single(report.Devices.Where(item =>
            item.Name.Contains("Root Hub", StringComparison.OrdinalIgnoreCase)));
        Assert.False(hub.RestartEligible);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RestartAsync(hub.DeviceInstanceId));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task StartupEntries_ProtectAutomation_AndRollbackThirdParty()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "startup-entries.json");
        var service = new StartupEntryRemediationService(
            state,
            evaluationMode: true);

        var preview = await service.PreviewAsync();

        var protectedPm2 = Assert.Single(preview.Entries.Where(item =>
            item.Name == "PM2"));
        Assert.True(protectedPm2.Protected);

        var vendor = Assert.Single(preview.Entries.Where(item =>
            item.Name == "DemoVendor"));
        Assert.False(vendor.Protected);
        Assert.True(vendor.Enabled);

        var disabled = await service.DisableAsync(vendor.EntryId);
        Assert.True(disabled.Success);
        Assert.True(disabled.RestoreAvailable);

        var afterDisable = await service.PreviewAsync();
        var disabledVendor = Assert.Single(afterDisable.Entries.Where(item =>
            item.Name == "DemoVendor"));
        Assert.False(disabledVendor.Enabled);
        Assert.True(disabledVendor.RestoreAvailable);

        var restored = await service.RestoreAsync(vendor.EntryId);
        Assert.True(restored.Success);

        var afterRestore = await service.PreviewAsync();
        var restoredVendor = Assert.Single(afterRestore.Entries.Where(item =>
            item.Name == "DemoVendor"));
        Assert.True(restoredVendor.Enabled);
        Assert.False(restoredVendor.RestoreAvailable);
    }

    [Fact]
    public async Task RollbackCenter_IncludesDisabledStartupEntry()
    {
        using var root = new TestDataRoot();
        var stateDir = Path.Combine(root.Path, "State");
        Directory.CreateDirectory(stateDir);

        var startupState = Path.Combine(stateDir, "startup-entries.json");
        var startup = new StartupEntryRemediationService(
            startupState,
            evaluationMode: true);
        var preview = await startup.PreviewAsync();
        var vendor = Assert.Single(preview.Entries.Where(item =>
            item.Name == "DemoVendor"));
        await startup.DisableAsync(vendor.EntryId);

        var eco = new EcoQosStateStore(
            Path.Combine(stateDir, "ecoqos.json"));
        var recovery = new OperationRecoveryService(
            eco,
            Path.Combine(root.Path, "Logs", "app.jsonl"));

        var center = new RollbackCenterService(
            recovery,
            Path.Combine(stateDir, "pagefile.json"),
            Path.Combine(stateDir, "power-plan.json"),
            Path.Combine(stateDir, "service-startup.json"),
            Path.Combine(root.Path, "Quarantine", "EdgeExtensions"),
            startupState);

        var report = center.Analyze();

        var item = Assert.Single(report.Entries.Where(entry =>
            entry.Id == "startup-entry:" + vendor.EntryId));
        Assert.Equal("startup.entry.restore", item.ActionId);
        Assert.Equal(vendor.EntryId, item.Parameters["entryId"]);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HostBridge_InUseMode_BlocksUsbRestartBeforeSyntheticMutation()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        await RunAsync(
            bridge,
            "system.workload.inuse",
            new { confirmed = true });

        var usb = await RunAsync(
            bridge,
            "drivers.usb.analyze");
        Assert.True(usb.Success);

        var report = Assert.IsType<UsbDiagnosticsReport>(usb.Data);
        var device = Assert.Single(report.Devices.Where(item =>
            item.RestartEligible));

        var blocked = await RunAsync(
            bridge,
            "drivers.usb.restart",
            new
            {
                deviceInstanceId = device.DeviceInstanceId,
                confirmed = true
            });

        Assert.False(blocked.Success);
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

        var request = System.Text.Json.JsonSerializer.Serialize(
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
