using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class SystemServicesTests
{
    [Fact]
    public async Task DriverScan_DegradesToKnownState()
    {
        var result = await new DriverService().AnalyzeAsync();

        Assert.Contains(
            result.Status,
            new[] { "OK", "WARNING", "UNKNOWN" });
        Assert.True(result.ProblemCount >= 0);
    }

    [Fact]
    public async Task ActivationScan_DegradesToKnownState()
    {
        var result = await new ActivationService().AnalyzeAsync();

        Assert.Contains(
            result.Status,
            new[]
            {
                "LICENSED",
                "UNLICENSED",
                "GRACE_OR_NOTIFICATION",
                "UNKNOWN"
            });
    }

    [Fact]
    public void BrowserInventory_IsReadOnlyAndReturnsCollection()
    {
        var result = new BrowserInventoryService().Analyze();

        Assert.NotNull(result.Browsers);
    }

    [Fact]
    public void BrowserExtensionHealth_RealProfileSmoke()
    {
        var result = new BrowserExtensionHealthService().AnalyzeEdge();

        Assert.Contains(result.Status, new[] { "OK", "WARNING", "NO_DATA" });
        Assert.NotNull(result.Items);
    }

    [Fact]
    public async Task MultimediaInventory_IsReadOnlyAndReturnsCollection()
    {
        var result = await new MultimediaService().AnalyzeAsync();

        Assert.NotNull(result.Devices);
    }
}
