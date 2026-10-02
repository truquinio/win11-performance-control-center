using Win11PerformanceControlCenter.App.Services;
using Xunit.Abstractions;

namespace App.Tests;

public sealed class BrowserExtensionHealthLiveSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void AnalyzeEdge_CurrentProfile_IsReadOnlyAndDoesNotCrash()
    {
        var result = new BrowserExtensionHealthService().AnalyzeEdge();

        Assert.Contains(result.Status, new[] { "OK", "WARNING", "NO_DATA" });
        Assert.True(result.ProfilesScanned >= 0);
        Assert.NotNull(result.Items);

        output.WriteLine(
            "Edge health: status={0}, profiles={1}, installed={2}, unpacked={3}, dataOnly={4}, broken={5}, developerMode={6}",
            result.Status,
            result.ProfilesScanned,
            result.InstalledCount,
            result.DeveloperLoadedCount,
            result.DataOnlyCount,
            result.BrokenCount,
            result.DeveloperMode?.ToString() ?? "unknown");
    }
}
