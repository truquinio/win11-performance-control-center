using Win11PerformanceControlCenter.App.Core;

namespace App.Tests;

public sealed class OperationTimeoutLeaseTests
{
    [Fact]
    public async Task WatchdogTimeout_RetainsCoordinatorUntilAbandonedWorkCompletes()
    {
        var coordinator = new OperationCoordinator();
        var workSource = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = coordinator.Begin("system.health.scan");

        var timeout = Assert.ThrowsAsync<TimeoutException>(() =>
            OperationWatchdog.RunAsync(
                workSource.Task, TimeSpan.FromMilliseconds(20),
                "system.health.scan", lease));

        await timeout;

        Assert.Equal("ANALYZING", coordinator.State);
        Assert.Throws<InvalidOperationException>(() => coordinator.Begin("other"));

        workSource.SetResult(1);
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.State == "IDLE", TimeSpan.FromSeconds(2)));
        using var next = coordinator.Begin("other");
        Assert.Equal("other", coordinator.ActiveAction);
    }
}