using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class HealthHistoryAutomationTests
{
    [Fact]
    public async Task HealthScore_IsExplainable_AndDetectsMaterialRegression()
    {
        using var root = new TestDataRoot();
        var now = DateTimeOffset.Parse("2026-10-07T18:00:00+00:00");
        var currentSnapshot = Snapshot(
            cpu: 15,
            memoryUsedPercent: 50,
            systemFreePercent: 30,
            integrity: "OK",
            drivers: "OK",
            rebootRequired: false);
        var currentStorage = Storage(
            ("C:\\", 30d, "OK"),
            ("D:\\", 20d, "OK"));
        IReadOnlyList<ReliabilityEventDto> currentEvents = [];

        var service = new HealthHistoryService(
            Path.Combine(root.Path, "health-history.json"),
            () => Task.FromResult(currentSnapshot),
            () => Task.FromResult(currentStorage),
            _ => Task.FromResult(currentEvents),
            () => now);

        var first = await service.CaptureAsync();

        Assert.True(first.BaselineCreated);
        Assert.Equal(100, first.Score);
        Assert.Empty(first.Factors);

        now = now.AddHours(6);
        currentSnapshot = Snapshot(
            cpu: 97,
            memoryUsedPercent: 92,
            systemFreePercent: 8,
            integrity: "REPAIRABLE",
            drivers: "WARNING",
            rebootRequired: true);
        currentStorage = Storage(
            ("C:\\", 8d, "LOW"),
            ("D:\\", 4d, "CRITICAL"));
        currentEvents =
        [
            Event(now, 18, "Microsoft-Windows-WHEA-Logger"),
            Event(now.AddMinutes(-1), 41, "Microsoft-Windows-Kernel-Power")
        ];

        var second = await service.CaptureAsync();

        Assert.False(second.BaselineCreated);
        Assert.True(second.Score < first.Score);
        Assert.Equal("ACTION", second.Band);
        Assert.Contains(second.Factors, item =>
            item.Category == "Memoria" && item.Penalty == 20);
        Assert.Contains(second.Factors, item =>
            item.Category == "Integridad");
        Assert.Contains(second.Factors, item =>
            item.Category == "Hardware");
        Assert.Contains(second.Changes, item =>
            item.Name == "Health Score" &&
            item.Impact == "WORSE");

        var changes = service.ReadChanges();
        Assert.False(changes.BaselineRequired);
        Assert.NotEmpty(changes.Changes);
        Assert.Equal(2, changes.Trend.Count);
    }

    [Fact]
    public async Task HealthScore_WeightsTransientCpuSpikeLightly()
    {
        using var root = new TestDataRoot();
        var snapshot = Snapshot(
            cpu: 99,
            memoryUsedPercent: 50,
            systemFreePercent: 40,
            integrity: "OK",
            drivers: "OK",
            rebootRequired: false);
        var storage = Storage(
            ("C:\\", 40d, "OK"),
            ("D:\\", 50d, "OK"));

        var service = new HealthHistoryService(
            Path.Combine(root.Path, "health-history.json"),
            () => Task.FromResult(snapshot),
            () => Task.FromResult(storage),
            _ => Task.FromResult<IReadOnlyList<ReliabilityEventDto>>([]));

        var report = await service.CaptureAsync();

        Assert.Equal(95, report.Score);
        var cpu = Assert.Single(
            report.Factors,
            item => item.Category == "CPU");
        Assert.Equal(5, cpu.Penalty);
        Assert.Contains(
            "transitoria",
            cpu.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutcomePolicies_CoverEveryWriteAction()
    {
        var catalog = new ActionCatalog();
        var policies = OutcomeAuditService.KnownPoliciesForTests();

        var writes = catalog.All
            .Where(action => action.Mode == ActionMode.WRITE)
            .ToArray();

        var missing = writes
            .Where(action => !policies.ContainsKey(action.Id))
            .Select(action => action.Id)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void OutcomeAudit_ParsesTerminalAndIncompleteEvidence()
    {
        using var root = new TestDataRoot();
        var log = Path.Combine(root.Path, "Logs", "app.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);

        var lines = new[]
        {
            LogLine("op1", "system.integrity.repair", "STARTED",
                "2026-10-07T18:00:00+00:00"),
            LogLine("op1", "system.integrity.repair", "COMPLETED",
                "2026-10-07T18:01:00+00:00"),
            LogLine("op2", "drivers.usb.restart", "STARTED",
                "2026-10-07T18:02:00+00:00"),
            LogLine("op3", "network.winsock.reset", "STARTED",
                "2026-10-07T18:03:00+00:00"),
            LogLine("op3", "network.winsock.reset", "REJECTED",
                "2026-10-07T18:03:01+00:00")
        };
        File.WriteAllLines(log, lines);

        var report = new OutcomeAuditService(
            new ActionCatalog(),
            log).Analyze();

        Assert.Equal(100d, report.CoveragePercent);
        Assert.Equal(1, report.IncompleteRuns);

        var integrity = Assert.Single(
            report.Actions,
            item => item.ActionId == "system.integrity.repair");
        Assert.Equal(1, integrity.CompletedRuns);

        var usb = Assert.Single(
            report.Actions,
            item => item.ActionId == "drivers.usb.restart");
        Assert.Equal(1, usb.IncompleteRuns);

        var winsock = Assert.Single(
            report.Actions,
            item => item.ActionId == "network.winsock.reset");
        Assert.Equal(1, winsock.RejectedRuns);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HostBridge_AutomationDefaultsOff_AndRunsOnlyReadOnlyWhenOptedIn()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var status = await RunAsync(
            bridge,
            "maintenance.policy.status");
        var initial = Assert.IsType<MaintenancePolicyStatus>(
            status.Data);
        Assert.Equal("OFF", initial.Mode);
        Assert.False(initial.CanRunNow);

        var blocked = await RunAsync(
            bridge,
            "maintenance.safe.run");
        Assert.False(blocked.Success);
        var blockedReport = Assert.IsType<MaintenanceRunReport>(
            blocked.Data);
        Assert.False(blockedReport.Executed);
        Assert.Empty(blockedReport.Items);

        var enabled = await RunAsync(
            bridge,
            "maintenance.policy.readonly",
            new { confirmed = true });
        Assert.True(enabled.Success);
        var enabledStatus = Assert.IsType<MaintenancePolicyStatus>(
            enabled.Data);
        Assert.Equal("READ_ONLY_IDLE", enabledStatus.Mode);
        Assert.True(enabledStatus.CanRunNow);

        var run = await RunAsync(
            bridge,
            "maintenance.safe.run");
        Assert.True(run.Success);
        var report = Assert.IsType<MaintenanceRunReport>(run.Data);
        Assert.True(report.Executed);
        Assert.Equal(8, report.Items.Count);
        var catalog = new ActionCatalog();
        Assert.DoesNotContain(
            report.Policy.FixedReadOnlyScope,
            actionId => catalog.GetRequired(actionId).Mode == ActionMode.WRITE);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HostBridge_HealthHistoryAndOutcomeCoverage_AreAvailable()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var first = await RunAsync(
            bridge,
            "system.healthscore.analyze");
        var second = await RunAsync(
            bridge,
            "system.healthscore.analyze");
        var changes = await RunAsync(
            bridge,
            "system.changes.analyze");
        var outcomes = await RunAsync(
            bridge,
            "lab.outcomes.status");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(changes.Success);
        Assert.True(outcomes.Success);

        var secondReport = Assert.IsType<HealthScoreReport>(
            second.Data);
        Assert.False(secondReport.BaselineCreated);
        Assert.True(secondReport.HistoryCount >= 2);

        var outcomeReport = Assert.IsType<OutcomeAuditReport>(
            outcomes.Data);
        Assert.Equal(100d, outcomeReport.CoveragePercent);
        Assert.Equal(
            outcomeReport.WriteActionCount,
            outcomeReport.ClassifiedCount);
    }

    private static SystemSnapshot Snapshot(
        double cpu,
        double memoryUsedPercent,
        double systemFreePercent,
        string integrity,
        string drivers,
        bool rebootRequired)
    {
        const ulong totalMemory = 16UL * 1024 * 1024 * 1024;
        var usedMemory = (ulong)(
            totalMemory * memoryUsedPercent / 100d);
        var available = totalMemory - usedMemory;
        const long totalDisk = 100L * 1024 * 1024 * 1024;
        var freeDisk = (long)(
            totalDisk * systemFreePercent / 100d);

        return new SystemSnapshot(
            DateTimeOffset.UtcNow,
            cpu,
            totalMemory,
            usedMemory,
            available,
            "C:\\",
            totalDisk,
            freeDisk,
            new NetworkSnapshot("Ethernet", 1000, 0, 0),
            integrity,
            drivers,
            "LICENSED",
            rebootRequired,
            3600,
            "IDLE");
    }

    private static StorageVolumeAudit Storage(
        params (string Name, double FreePercent, string Status)[] items)
    {
        const long total = 100L * 1024 * 1024 * 1024;
        var volumes = items.Select(item =>
            new StorageVolumeStatus(
                item.Name,
                "Test",
                "NTFS",
                total,
                (long)(total * item.FreePercent / 100d),
                item.FreePercent,
                item.Status)).ToArray();

        return new StorageVolumeAudit(
            DateTimeOffset.UtcNow,
            volumes,
            volumes.Count(volume => volume.Status != "OK"));
    }

    private static ReliabilityEventDto Event(
        DateTimeOffset time,
        int id,
        string provider) =>
        new(
            time,
            id,
            provider,
            EvidenceClassification.HECHO,
            "Synthetic evidence");

    private static string LogLine(
        string operationId,
        string actionId,
        string status,
        string timestamp) =>
        JsonSerializer.Serialize(
            new
            {
                timestamp,
                operationId,
                actionId,
                status,
                data = new { mode = "WRITE" }
            },
            HostBridge.JsonOptions);

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
