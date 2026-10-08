using System.Diagnostics;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

/// <summary>
/// Exercises the real power-throttling APIs against a throwaway child
/// process owned by the test, so nothing else on the machine is touched.
/// </summary>
public sealed class EcoQosRoundTripTests
{
    // Querying ProcessPowerThrottling needs Windows 11 22H2 or later, which
    // is also the product's supported baseline.
    private static bool ThrottlingQuerySupported =>
        Environment.OSVersion.Version.Build >= 22621;

    [Fact]
    public async Task ApplyThenRestore_RoundTripsOnAnOwnedProcess()
    {
        if (!ThrottlingQuerySupported)
            return;

        using var dataRoot = new TestDataRoot();
        var store = new EcoQosStateStore(
            Path.Combine(dataRoot.Path, "State", "ecoqos.json"));
        var service = new ProcessTuningService(store);
        using var process = StartIdleChild();

        try
        {
            var target = Target(process);

            var applied = (await service.ApplyEcoQosAsync([target])).Items.Single();
            Assert.True(applied.Success, applied.Error);
            Assert.False(applied.BeforeEcoQos);
            Assert.True(applied.AfterEcoQos);
            Assert.True(store.TryGet(process.Id, out var baseline));
            Assert.Equal(0u, baseline.ControlMask & 1u);

            // Applying twice must keep the first (original) baseline.
            var reapplied = (await service.ApplyEcoQosAsync([target])).Items.Single();
            Assert.True(reapplied.Success, reapplied.Error);
            Assert.True(reapplied.BeforeEcoQos);
            Assert.True(store.TryGet(process.Id, out var kept));
            Assert.Equal(baseline.CapturedAt, kept.CapturedAt);

            var restored = (await service.RestoreEcoQosAsync([process.Id])).Items.Single();
            Assert.True(restored.Success, restored.Error);
            Assert.True(restored.BeforeEcoQos);
            Assert.False(restored.AfterEcoQos);
            Assert.False(store.TryGet(process.Id, out _));

            // A process that was system-managed goes back to system-managed:
            // the next baseline records the policy as not explicitly set.
            var second = (await service.ApplyEcoQosAsync([target])).Items.Single();
            Assert.True(second.Success, second.Error);
            Assert.True(store.TryGet(process.Id, out var afterRestore));
            Assert.Equal(0u, afterRestore.ControlMask & 1u);
            Assert.Equal(0u, afterRestore.StateMask & 1u);
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    public async Task Apply_DoesNotThrottleWhenRollbackSnapshotCannotBeSaved()
    {
        if (!ThrottlingQuerySupported)
            return;

        using var dataRoot = new TestDataRoot();
        var statePath = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        var store = new EcoQosStateStore(statePath);
        var service = new ProcessTuningService(store);
        using var process = StartIdleChild();

        try
        {
            var target = Target(process);
            Directory.CreateDirectory(statePath + ".tmp");

            var blocked = (await service.ApplyEcoQosAsync([target])).Items.Single();
            Assert.False(blocked.Success);
            Assert.Contains("rollback", blocked.Error);
            Assert.False(store.TryGet(process.Id, out _));

            Directory.Delete(statePath + ".tmp");

            // The earlier attempt left the process untouched.
            var applied = (await service.ApplyEcoQosAsync([target])).Items.Single();
            Assert.True(applied.Success, applied.Error);
            Assert.False(applied.BeforeEcoQos);
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    public async Task Restore_IsBlockedWithoutSnapshot()
    {
        using var dataRoot = new TestDataRoot();
        var service = new ProcessTuningService(new EcoQosStateStore(
            Path.Combine(dataRoot.Path, "State", "ecoqos.json")));
        using var process = StartIdleChild();

        try
        {
            var item = (await service.RestoreEcoQosAsync([process.Id])).Items.Single();
            Assert.False(item.Success);
            Assert.Contains("snapshot", item.Error);
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    public async Task Restore_IsBlockedWhenSnapshotBelongsToAnotherProcessInstance()
    {
        using var dataRoot = new TestDataRoot();
        var store = new EcoQosStateStore(
            Path.Combine(dataRoot.Path, "State", "ecoqos.json"));
        var service = new ProcessTuningService(store);
        using var process = StartIdleChild();

        try
        {
            store.SaveBaseline(new EcoQosOriginalState(
                process.Id,
                process.ProcessName,
                DateTimeOffset.UtcNow.AddDays(-2),
                0,
                0,
                DateTimeOffset.UtcNow.AddDays(-2)));

            var item = (await service.RestoreEcoQosAsync([process.Id])).Items.Single();
            Assert.False(item.Success);
            Assert.Contains("reutilizado", item.Error);

            // The snapshot can never match this PID again, so it is dropped.
            Assert.False(store.TryGet(process.Id, out _));
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    public async Task Restore_DiscardsSnapshotOfProcessThatExited()
    {
        using var dataRoot = new TestDataRoot();
        var store = new EcoQosStateStore(
            Path.Combine(dataRoot.Path, "State", "ecoqos.json"));
        var service = new ProcessTuningService(store);
        using var process = StartIdleChild();
        var processId = process.Id;
        var target = Target(process);
        store.SaveBaseline(new EcoQosOriginalState(
            processId,
            target.Name,
            target.StartTime,
            0,
            0,
            DateTimeOffset.UtcNow));

        Kill(process);
        process.WaitForExit(5000);

        var item = (await service.RestoreEcoQosAsync([processId])).Items.Single();

        Assert.False(item.Success);
        Assert.Contains("ya no existe", item.Error);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Recovery_ReportsOnlyLiveInstancesAsRestorable()
    {
        using var dataRoot = new TestDataRoot();
        var store = new EcoQosStateStore(
            Path.Combine(dataRoot.Path, "State", "ecoqos.json"));
        using var process = StartIdleChild();

        try
        {
            var target = Target(process);
            store.SaveBaseline(new EcoQosOriginalState(
                process.Id,
                target.Name,
                target.StartTime,
                0,
                0,
                DateTimeOffset.UtcNow));
            store.SaveBaseline(new EcoQosOriginalState(
                2147483000,
                "gone",
                DateTimeOffset.UtcNow.AddDays(-1),
                0,
                0,
                DateTimeOffset.UtcNow.AddDays(-1)));

            var status = new OperationRecoveryService(
                store,
                Path.Combine(dataRoot.Path, "Logs", "app.jsonl")).Analyze();

            Assert.Equal(2, status.RollbackSnapshots.Count);
            Assert.True(status.EcoQosTargets
                .Single(item => item.ProcessId == process.Id).Restorable);
            Assert.False(status.EcoQosTargets
                .Single(item => item.ProcessId == 2147483000).Restorable);
        }
        finally
        {
            Kill(process);
        }
    }

    private static ValidatedProcessTarget Target(Process process) =>
        new(
            process.Id,
            process.ProcessName,
            new DateTimeOffset(process.StartTime.ToUniversalTime()));

    private static Process StartIdleChild()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            // Never weaken process protection just to satisfy the test.
            // The subprocess is a disposable ping workload, not a terminal.
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "PING.EXE"),
            Arguments = "127.0.0.1 -n 30",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        return process;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
