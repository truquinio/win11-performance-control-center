using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class StorageDirectoryTrendTests
{
    [Fact]
    public async Task Observe_PersistsBaselineAndReportsOnlyComparableCompleteGrowth()
    {
        using var fixture = new TestDataRoot();
        var watched = Path.Combine(fixture.Path, "watched");
        var state = Path.Combine(fixture.Path, "state", "directories.json");
        Directory.CreateDirectory(watched);
        File.WriteAllBytes(Path.Combine(watched, "old.dat"), new byte[12]);

        var service = new StorageDirectoryTrendService(state, [watched],
            TimeSpan.FromSeconds(2), maxFilesPerRoot: 100);
        var first = await service.ObserveAsync();
        Assert.True(first.BaselineCreated);
        var initial = Assert.Single(first.Items);
        Assert.True(initial.Complete);
        Assert.Equal(12L, initial.MeasuredBytes);
        Assert.Null(initial.GrowthBytes);

        File.WriteAllBytes(Path.Combine(watched, "new.dat"), new byte[27]);
        var second = await service.ObserveAsync();
        var grown = Assert.Single(second.Items);
        Assert.False(second.BaselineCreated);
        Assert.True(grown.Complete);
        Assert.Equal(39L, grown.MeasuredBytes);
        Assert.Equal(27L, grown.GrowthBytes);
        Assert.True(File.Exists(state));
    }

    [Fact]
    public async Task Observe_IncompleteMeasurementsDoNotInventGrowth()
    {
        using var fixture = new TestDataRoot();
        var watched = Path.Combine(fixture.Path, "watched");
        Directory.CreateDirectory(watched);
        for (var n = 0; n < 12; n++)
            File.WriteAllBytes(Path.Combine(watched, $"{n:D2}.dat"), new byte[6]);
        var service = new StorageDirectoryTrendService(
            Path.Combine(fixture.Path, "state.json"), [watched],
            TimeSpan.FromSeconds(1), maxFilesPerRoot: 2);
        await service.ObserveAsync();
        var second = await service.ObserveAsync();
        var item = Assert.Single(second.Items);
        Assert.False(item.Complete);
        Assert.Null(item.GrowthBytes);
        Assert.True(second.Partial);
    }

    [Fact]
    public async Task Observe_SkipsJunctionBeforeReadingExternalData()
    {
        using var fixture = new TestDataRoot();
        using var outside = new TestDataRoot();
        var link = Path.Combine(fixture.Path, "junction");
        File.WriteAllText(Path.Combine(outside.Path, "important.txt"), "not scanned");
        try { Directory.CreateSymbolicLink(link, outside.Path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }
        var service = new StorageDirectoryTrendService(
            Path.Combine(fixture.Path, "state.json"), [link],
            TimeSpan.FromSeconds(1), maxFilesPerRoot: 200);
        var report = await service.ObserveAsync();
        var item = Assert.Single(report.Items);
        Assert.False(item.Complete);
        Assert.Equal(0L, item.MeasuredBytes);
        Assert.Null(item.GrowthBytes);
    }
}
