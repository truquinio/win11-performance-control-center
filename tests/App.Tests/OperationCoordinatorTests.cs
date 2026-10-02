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
}
