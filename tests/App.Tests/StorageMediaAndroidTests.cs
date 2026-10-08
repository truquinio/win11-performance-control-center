using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class StorageMediaAndroidTests
{
    [Fact]
    public void PageFilePolicy_PrefersSafeSsdOverRoomierHdd()
    {
        var volumes = new[]
        {
            Volume("C:", "SSD", 128, 28, system: true),
            Volume("D:", "HDD", 1000, 47, system: false)
        };

        var result = PageFilePlacementPolicy.Recommend(
            volumes,
            "C:");

        Assert.True(result.Available);
        Assert.Equal("SSD_PREFERRED", result.Status);
        Assert.Equal("C:", result.TargetDrive);
        Assert.Equal("SSD", result.TargetMediaType);
        var entry = Assert.Single(result.PagingFiles);
        Assert.Equal(
            @"C:pagefile.sys 4096 8192",
            entry);
        Assert.True(result.FreeAfterMaxBytes >= 12L * 1024 * 1024 * 1024);
        Assert.True(result.FreeAfterMaxPercent >= 12d);
    }

    [Fact]
    public void PageFilePolicy_FallsBackToHdd_WhenSsdWouldBeTooFull()
    {
        var volumes = new[]
        {
            Volume("C:", "SSD", 128, 15, system: true),
            Volume("D:", "HDD", 1000, 160, system: false)
        };

        var result = PageFilePlacementPolicy.Recommend(
            volumes,
            "C:");

        Assert.True(result.Available);
        Assert.Equal("SAFE_FALLBACK", result.Status);
        Assert.Equal("D:", result.TargetDrive);
        Assert.Equal("HDD", result.TargetMediaType);
        Assert.Equal(
            new[]
            {
                @"C:pagefile.sys 512 512",
                @"D:pagefile.sys 4096 8192"
            },
            result.PagingFiles);
    }

    [Fact]
    public void PageFilePolicy_FailsClosed_WhenNoDriveKeepsSafetyMargin()
    {
        var volumes = new[]
        {
            Volume("C:", "SSD", 128, 14, system: true),
            Volume("D:", "HDD", 1000, 20, system: false)
        };

        var result = PageFilePlacementPolicy.Recommend(
            volumes,
            "C:");

        Assert.False(result.Available);
        Assert.Equal("NO_SAFE_TARGET", result.Status);
        Assert.Null(result.TargetDrive);
        Assert.Empty(result.PagingFiles);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task AndroidAvdAudit_ProtectsActiveLab_AndNeverDeletes()
    {
        using var root = new TestDataRoot();
        var avdHome = Path.Combine(root.Path, "avd");
        var active = Path.Combine(avdHome, "active.avd");
        var inactive = Path.Combine(avdHome, "inactive.avd");
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(inactive);

        File.WriteAllText(
            Path.Combine(active, "config.ini"),
            "hw.ramSize=2048\nhw.cpu.ncore=4\nhw.gpu.mode=auto\ndisk.dataPartition.size=6G\n");
        File.WriteAllText(
            Path.Combine(inactive, "config.ini"),
            "hw.ramSize=1024\nhw.cpu.ncore=2\nhw.gpu.mode=swiftshader_indirect\ndisk.dataPartition.size=4G\n");
        File.WriteAllBytes(
            Path.Combine(active, "userdata-qemu.img"),
            new byte[512]);
        File.WriteAllBytes(
            Path.Combine(inactive, "userdata-qemu.img"),
            new byte[1024]);

        var service = new AndroidAvdService(
            avdHome,
            () => new HashSet<string>(
                ["active"],
                StringComparer.OrdinalIgnoreCase));

        var report = await service.AnalyzeAsync();

        Assert.True(report.Available);
        Assert.False(report.Partial);
        Assert.Equal(2, report.AvdCount);
        Assert.Equal(1, report.ActiveCount);

        var activeItem = Assert.Single(
            report.Items,
            item => item.Name == "active");
        Assert.True(activeItem.Active);
        Assert.Equal("ACTIVE_PROTECTED", activeItem.Status);
        Assert.Equal(2048, activeItem.RamMb);
        Assert.True(File.Exists(
            Path.Combine(active, "userdata-qemu.img")));

        var inactiveItem = Assert.Single(
            report.Items,
            item => item.Name == "inactive");
        Assert.False(inactiveItem.Active);
        Assert.Equal("INACTIVE_REVIEW", inactiveItem.Status);
        Assert.True(report.InactiveLogicalBytes >= 1024);
        Assert.True(File.Exists(
            Path.Combine(inactive, "userdata-qemu.img")));
    }

    [Theory]
    [InlineData(
        ""C:\\Android\\emulator.exe" -avd spotify_api28_hooklab2 -port 5566",
        "spotify_api28_hooklab2")]
    [InlineData(
        "qemu-system-x86_64.exe -no-window -avd demo_lab -memory 2048",
        "demo_lab")]
    public void AndroidAvdParser_ExtractsExactAvdName(
        string commandLine,
        string expected)
    {
        Assert.Equal(
            expected,
            AndroidAvdService.TryReadAvdName(commandLine));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HostBridge_AndroidAvdAudit_UsesEvaluationFixture()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var result = await RunAsync(
            bridge,
            "developer.android.avd.audit");

        Assert.True(result.Success);
        var report = Assert.IsType<AndroidAvdReport>(result.Data);
        Assert.Equal(2, report.AvdCount);
        Assert.Equal(1, report.ActiveCount);
        var active = Assert.Single(
            report.Items,
            item => item.Active);
        Assert.Equal("ACTIVE_PROTECTED", active.Status);
    }

    private static StorageVolumeMedia Volume(
        string drive,
        string media,
        long sizeGiB,
        long freeGiB,
        bool system) =>
        new(
            drive,
            drive == "C:" ? 0 : 1,
            "Test " + media,
            media,
            sizeGiB * 1024L * 1024 * 1024,
            freeGiB * 1024L * 1024 * 1024,
            Math.Round(freeGiB * 100d / sizeGiB, 1),
            system);

    private static async Task<ActionResult> RunAsync(
        HostBridge bridge,
        string actionId)
    {
        var request = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                type = "request",
                requestId = Guid.NewGuid().ToString("N"),
                method = "actions.run",
                payload = new { id = actionId }
            },
            HostBridge.JsonOptions);

        var response = await bridge.HandleAsync(request);
        Assert.True(response.Ok, response.Error);
        return Assert.IsType<ActionResult>(response.Result);
    }
}
