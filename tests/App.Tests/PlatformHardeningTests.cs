using System.Reflection;
using System.Security.Principal;
using Win11PerformanceControlCenter.App;

namespace App.Tests;

public sealed class PlatformHardeningTests
{
    [Fact]
    public void SingleInstanceMutex_IsGlobalPerUserAcrossSessions()
    {
        var field = typeof(Win11PerformanceControlCenter.App.App).GetField(
            "SingleInstanceName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        var name = Assert.IsType<string>(field.GetValue(null));
        Assert.StartsWith(@"Global\", name, StringComparison.Ordinal);

        var sid = WindowsIdentity.GetCurrent().User?.Value;
        Assert.False(string.IsNullOrWhiteSpace(sid));
        Assert.Contains(sid!, name, StringComparison.Ordinal);
    }
}
