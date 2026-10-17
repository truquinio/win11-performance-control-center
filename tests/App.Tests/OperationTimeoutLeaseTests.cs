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

    [Fact]
    public async Task WatchdogTimeout_CancelsCooperativeWorkAndReleasesLease()
    {
        var coordinator = new OperationCoordinator();
        using var lease = coordinator.Begin("disk.directories.watch");
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Work(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 1;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        }

        await Assert.ThrowsAsync<TimeoutException>(() =>
            OperationWatchdog.RunAsync(
                Work, TimeSpan.FromMilliseconds(20), "disk.directories.watch", lease));

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.State == "IDLE", TimeSpan.FromSeconds(2)));
    }
}