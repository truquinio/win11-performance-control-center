using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class DeepWindowsEvidenceTests
{
    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task ScheduledTasks_ProtectMicrosoftAndPm2_AndRollbackVendorTask()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "scheduled-tasks.json");
        var service = new ScheduledTaskRemediationService(
            state,
            evaluationMode: true);

        var preview = await service.PreviewAsync();

        Assert.Equal(3, preview.TaskCount);
        Assert.Equal(1, preview.EligibleCount);

        var vendor = Assert.Single(
            preview.Tasks,
            item => item.FullName == @"\DemoVendor\Updater");
        Assert.False(vendor.Protected);

        var microsoft = Assert.Single(
            preview.Tasks,
            item => item.FullName.Contains(
                @"\Microsoft\",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(microsoft.Protected);

        var pm2 = Assert.Single(
            preview.Tasks,
            item => item.FullName.Contains(
                "PM2",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(pm2.Protected);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DisableAsync(microsoft.EntryId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DisableAsync(pm2.EntryId));

        var disabled = await service.DisableAsync(vendor.EntryId);
        Assert.True(disabled.Success);
        Assert.True(disabled.RestoreAvailable);

        var withRollback = await service.PreviewAsync();
        var vendorAfter = Assert.Single(
            withRollback.Tasks,
            item => item.EntryId == vendor.EntryId);
        Assert.True(vendorAfter.RestoreAvailable);

        var restored = await service.RestoreAsync(vendor.EntryId);
        Assert.True(restored.Success);
        Assert.False(restored.RestoreAvailable);
    }

    [Fact]
    public async Task RollbackCenter_IncludesDisabledScheduledTask()
    {
        using var root = new TestDataRoot();
        var stateDir = Path.Combine(root.Path, "State");
        Directory.CreateDirectory(stateDir);

        var taskState = Path.Combine(
            stateDir,
            "scheduled-tasks.json");
        var service = new ScheduledTaskRemediationService(
            taskState,
            evaluationMode: true);
        var preview = await service.PreviewAsync();
        var vendor = Assert.Single(
            preview.Tasks,
            item => item.FullName == @"\DemoVendor\Updater");
        await service.DisableAsync(vendor.EntryId);

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
            Path.Combine(stateDir, "startup-entries.json"),
            taskState);

        var report = center.Analyze();

        var rollback = Assert.Single(
            report.Entries,
            item => item.Id == "scheduled-task:" + vendor.EntryId);
        Assert.Equal("startup.task.restore", rollback.ActionId);
        Assert.Equal(vendor.EntryId, rollback.Parameters["entryId"]);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task ServiceDependencies_ActionIsReadOnlyAndAvailable()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var result = await RunAsync(
            bridge,
            "startup.service.dependencies",
            new { serviceName = "DemoVendorSvc" });

        Assert.True(result.Success);
        var report = Assert.IsType<ServiceDependencyReport>(result.Data);
        Assert.Equal("DemoVendorSvc", report.ServiceName);
        Assert.Equal(0, report.DependentCount);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task EvidenceActions_AreReadOnlyAndReturnBoundedReports()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        foreach (var actionId in new[]
                 {
                     "system.registry.evidence",
                     "system.com.evidence",
                     "system.certificates.audit"
                 })
        {
            var result = await RunAsync(bridge, actionId);

            Assert.True(result.Success);
            var report = Assert.IsType<WindowsEvidenceReport>(
                result.Data);
            Assert.True(report.Items.Count <= 200);
        }
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task DiagnosticBundle_IsSanitizedAndContainsExpectedFiles()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var result = await RunAsync(
            bridge,
            "diagnostics.bundle.create",
            new { confirmed = true });

        Assert.True(result.Success);
        var bundle = Assert.IsType<DiagnosticBundleResult>(
            result.Data);
        Assert.True(bundle.Sanitized);
        Assert.Equal(10, bundle.JsonFiles);
        Assert.True(File.Exists(bundle.Path));

        using var archive = ZipFile.OpenRead(bundle.Path);
        var names = archive.Entries
            .Select(entry => entry.FullName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(10, names.Length);
        Assert.Contains("manifest.json", names);
        Assert.Contains("system.json", names);
        Assert.Contains("storage.json", names);
        Assert.Contains("drivers-usb.json", names);
        Assert.Contains("reliability.json", names);
        Assert.Contains("startup.json", names);
        Assert.Contains("health-history.json", names);
        Assert.Contains("outcomes.json", names);
        Assert.Contains("windows-evidence.json", names);
        Assert.Contains("recovery.json", names);

        var combined = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            combined.AppendLine(await reader.ReadToEndAsync());
        }

        var payload = combined.ToString();
        Assert.DoesNotContain(
            "deviceInstanceId",
            payload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "pnpDeviceId",
            payload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "VID_1234",
            payload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            ""arguments"",
            payload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            ""execute"",
            payload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            ""command"",
            payload,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "partialProductKey",
            payload,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewWriteActions_HaveOutcomePolicies()
    {
        var policies = OutcomeAuditService.KnownPoliciesForTests();

        Assert.Contains("startup.task.disable", policies.Keys);
        Assert.Contains("startup.task.restore", policies.Keys);
        Assert.Contains("diagnostics.bundle.create", policies.Keys);
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
