using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Win11PerformanceControlCenter.App;
using Win11PerformanceControlCenter.App.Core;

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

    [Fact]
    public void NativeImports_ResolveOnlyFromSystem32()
    {
        var assembly = typeof(Win11PerformanceControlCenter.App.App).Assembly;
        var attribute = assembly
            .GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(DllImportSearchPath.System32, attribute.Paths);

        // No individual import may widen the assembly-wide restriction.
        var imports = assembly.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic |
                BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not null)
            .ToArray();

        Assert.NotEmpty(imports);
        Assert.All(imports, method =>
        {
            var local = method
                .GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
            Assert.True(
                local is null || local.Paths == DllImportSearchPath.System32,
                method.DeclaringType?.Name + "." + method.Name);
        });
    }

    [Fact]
    public void SingleInstanceGuard_FirstCallerOwnsTheNameAndSecondIsRefused()
    {
        var name = @"Local\WPCC-Test-" + Guid.NewGuid().ToString("N");

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        Assert.NotNull(first);
        try
        {
            Assert.False(SingleInstanceGuard.TryAcquire(name, out var second));
            Assert.Null(second);
        }
        finally
        {
            first.ReleaseMutex();
            first.Dispose();
        }

        // Once released, the name can be owned again.
        Assert.True(SingleInstanceGuard.TryAcquire(name, out var third));
        third!.ReleaseMutex();
        third.Dispose();
    }

    [Fact]
    public void SingleInstanceGuard_TreatsAccessDeniedMutexAsRunningInstance()
    {
        // An instance started elevated owns a mutex whose ACL refuses a
        // full-access open from a standard-integrity launch of the same user.
        var name = @"Local\WPCC-Test-" + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            identity.User!,
            MutexRights.Synchronize,
            AccessControlType.Allow));

        using var restricted = MutexAcl.Create(
            initiallyOwned: false,
            name,
            out var createdNew,
            security);
        Assert.True(createdNew);

        Assert.False(SingleInstanceGuard.TryAcquire(name, out var mutex));
        Assert.Null(mutex);
    }

    [Fact]
    public void SingleInstanceGuard_TreatsForeignKernelObjectAsRunningInstance()
    {
        var name = @"Local\WPCC-Test-" + Guid.NewGuid().ToString("N");
        using var foreign = new Semaphore(1, 1, name);

        Assert.False(SingleInstanceGuard.TryAcquire(name, out var mutex));
        Assert.Null(mutex);
    }

    [Theory]
    [InlineData(1.00, 1024)]
    [InlineData(1.25, 1280)]
    [InlineData(1.50, 1536)]
    [InlineData(2.00, 2048)]
    public void WindowMinimumTracking_ScalesAcrossTargetDpi(
        double dpiScale,
        int expectedPixels)
    {
        Assert.Equal(
            expectedPixels,
            WindowGeometryPolicy.ScaleMinimumToPixels(
                1024d,
                dpiScale,
                4096));
    }

    [Theory]
    [InlineData(0, 0, 1536, 816)]
    [InlineData(-1536, 0, 1536, 864)]
    [InlineData(1920, -200, 1280, 720)]
    [InlineData(-960, -540, 960, 540)]
    public void WindowLayout_RemainsInsideAnyMonitorWorkArea(
        double left,
        double top,
        double width,
        double height)
    {
        var layout = WindowGeometryPolicy.Constrain(
            left, top, width, height,
            1440, 780,
            left - 500, top - 500);

        Assert.InRange(layout.Width, 1, Math.Max(1, width - 16));
        Assert.InRange(layout.Height, 1, Math.Max(1, height - 16));
        Assert.InRange(layout.Left, left, left + width - layout.Width);
        Assert.InRange(layout.Top, top, top + height - layout.Height);
    }
    [Theory]
    [InlineData("es-ES")]
    [InlineData("es-MX")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    [InlineData("ja-JP")]
    [InlineData("zh-CN")]
    public async Task BridgeJson_RemainsValidAcrossCultures(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            using var dataRoot = new TestDataRoot();
            using var bridge = dataRoot.CreateBridge();
            var json = await bridge.HandleSerializedAsync(
                """{"type":"request","requestId":"culture","method":"system.snapshot","payload":{}}""");

            using var document = JsonDocument.Parse(json);
            Assert.Equal("culture", document.RootElement.GetProperty("requestId").GetString());
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void JsonProtocol_IsInvariantAcrossAllSpecificCultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in CultureInfo.GetCultures(
                         CultureTypes.SpecificCultures))
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                var json = JsonSerializer.Serialize(
                    new
                    {
                        number = 1234.5,
                        timestamp = new DateTimeOffset(
                            2026, 10, 2, 20, 15, 30, TimeSpan.Zero)
                    },
                    HostBridge.JsonOptions);

                using var document = JsonDocument.Parse(json);
                Assert.Equal(
                    1234.5,
                    document.RootElement.GetProperty("number").GetDouble());
                Assert.Equal(
                    new DateTimeOffset(
                        2026, 10, 2, 20, 15, 30, TimeSpan.Zero),
                    document.RootElement
                        .GetProperty("timestamp")
                        .GetDateTimeOffset());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
