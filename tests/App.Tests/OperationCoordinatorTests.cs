using Win11PerformanceControlCenter.App.Core;

namespace App.Tests;

public sealed class OperationCoordinatorTests
{
    [Fact]
    public void Begin_SetsAndResetsOperationState()
    {
        var coordinator = new OperationCoordinator();

        using (coordinator.Begin("system.health.scan"))
        {
            Assert.Equal("ANALYZING", coordinator.State);
            Assert.Equal("system.health.scan", coordinator.ActiveAction);
        }

        Assert.Equal("IDLE", coordinator.State);
        Assert.Null(coordinator.ActiveAction);
    }

    [Fact]
    public void Begin_RejectsConcurrentOperation()
    {
        var coordinator = new OperationCoordinator();
        using var first = coordinator.Begin("disk.scan");

        var error = Assert.Throws<InvalidOperationException>(
            () => coordinator.Begin("network.test"));

        Assert.Contains("operación activa", error.Message);
        Assert.Equal("disk.scan", coordinator.ActiveAction);
    }

    [Fact]
    public void Lease_DisposeIsIdempotent()
    {
        var coordinator = new OperationCoordinator();
        var lease = coordinator.Begin("memory.analyze");

        lease.Dispose();
        lease.Dispose();

        Assert.Equal("IDLE", coordinator.State);
        Assert.Null(coordinator.ActiveAction);
    }

    [Fact]
    public void Begin_UnderParallelContention_NeverAllowsOverlap()
    {
        var coordinator = new OperationCoordinator();
        var active = 0;
        var maximumActive = 0;

        Parallel.For(0, 64, index =>
        {
            try
            {
                using var lease = coordinator.Begin("stress-" + index);
                var nowActive = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximumActive, nowActive);
                Thread.Sleep(10);
                Interlocked.Decrement(ref active);
            }
            catch (InvalidOperationException)
            {
                // Contention is expected; overlap is not.
            }
        });

        Assert.InRange(maximumActive, 1, 1);
        Assert.Equal("IDLE", coordinator.State);
        Assert.Null(coordinator.ActiveAction);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (value <= current)
                    return;
                if (Interlocked.CompareExchange(
                        ref target,
                        value,
                        current) == current)
                    return;
            }
        }
    }

}
