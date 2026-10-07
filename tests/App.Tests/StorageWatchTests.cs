using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class StorageWatchTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public async Task Watch_FirstCaptureCreatesBaseline_SecondDetectsGrowth()
    {
        using var dataRoot = new TestDataRoot();
        var state = Path.Combine(dataRoot.Path, "storage-watch.json");
        var now = new DateTimeOffset(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);
        var freeBytes = 70L * Gib;

        IReadOnlyList<StorageWatchService.VolumeProbe> Probe() =>
        [
            new(
                "D:\\",
                "DATA",
                "NTFS",
                dataRoot.Path,
                500L * Gib,
                freeBytes)
        ];

        var service = new StorageWatchService(
            state,
            Probe,
            () => now,
            TimeSpan.FromSeconds(1));

        var first = await service.WatchAsync();
        Assert.True(first.BaselineCreated);
        Assert.Null(first.Volumes.Single().FreeBytesDelta);

        freeBytes -= 7L * Gib;
        now = now.AddHours(1);
        var second = await service.WatchAsync();

        Assert.False(second.BaselineCreated);
        var change = Assert.Single(second.Volumes);
        Assert.Equal(-7L * Gib, change.FreeBytesDelta);
        Assert.Equal(7L * Gib, change.GrowthBytes);
        Assert.Equal("GROWTH_ALERT", change.TrendStatus);
        Assert.True(File.Exists(state));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task Watch_LowSpaceVolumeIsFlaggedWithoutDeletingAnything()
    {
        using var dataRoot = new TestDataRoot();
        var marker = Path.Combine(dataRoot.Path, "keep-me.bin");
        await File.WriteAllBytesAsync(marker, new byte[64]);

        var service = new StorageWatchService(
            Path.Combine(dataRoot.Path, "watch.json"),
            () =>
            [
                new StorageWatchService.VolumeProbe(
                    "D:\\",
                    "DATA",
                    "NTFS",
                    dataRoot.Path,
                    100L * Gib,
                    6L * Gib)
            ],
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(500));

        var report = await service.WatchAsync();

        var change = Assert.Single(report.Volumes);
        Assert.Equal("LOW_FREE_SPACE", change.TrendStatus);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task HotspotScan_IsBoundedAndReadOnly()
    {
        using var dataRoot = new TestDataRoot();
        var first = Path.Combine(dataRoot.Path, "A");
        var second = Path.Combine(dataRoot.Path, "B");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await File.WriteAllBytesAsync(
            Path.Combine(first, "one.bin"),
            new byte[1024]);
        await File.WriteAllBytesAsync(
            Path.Combine(second, "two.bin"),
            new byte[2048]);

        var service = new StorageWatchService(
            Path.Combine(dataRoot.Path, "watch.json"),
            () =>
            [
                new StorageWatchService.VolumeProbe(
                    "D:\\",
                    "DATA",
                    "NTFS",
                    dataRoot.Path,
                    100L * Gib,
                    5L * Gib)
            ],
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1));

        var report = await service.ScanLowSpaceHotspotsAsync();

        var volume = Assert.Single(report.Volumes);
        Assert.NotEmpty(volume.Items);
        Assert.True(
            File.Exists(Path.Combine(first, "one.bin")));
        Assert.True(
            File.Exists(Path.Combine(second, "two.bin")));
        Assert.InRange(report.ElapsedMilliseconds, 0, 1500);
    }

    [Fact]
    public async Task CorruptedPrimaryState_FallsBackToFreshBaseline()
    {
        using var dataRoot = new TestDataRoot();
        var state = Path.Combine(dataRoot.Path, "watch.json");
        await File.WriteAllTextAsync(state, "{not-json");

        var service = new StorageWatchService(
            state,
            () =>
            [
                new StorageWatchService.VolumeProbe(
                    "C:\\",
                    "SYSTEM",
                    "NTFS",
                    dataRoot.Path,
                    100L * Gib,
                    40L * Gib)
            ],
            () => DateTimeOffset.UtcNow);

        var report = await service.WatchAsync();

        Assert.True(report.BaselineCreated);
        Assert.Single(report.Volumes);
    }
}
