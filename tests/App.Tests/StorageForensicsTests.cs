using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class StorageForensicsTests
{
    [Fact]
    public async Task Scan_GroupsOnlyByteIdenticalFiles_WithoutModifyingThem()
    {
        using var fixture = new TestDataRoot();
        var one = Path.Combine(fixture.Path, "a.bin");
        var two = Path.Combine(fixture.Path, "b.bin");
        var different = Path.Combine(fixture.Path, "c.bin");
        File.WriteAllText(one, "matching-payload");
        File.WriteAllText(two, "matching-payload");
        File.WriteAllText(different, "different-bytes");
        var report = await new StorageForensicsService(TimeSpan.FromSeconds(3), 100)
            .ScanAsync([fixture.Path]);
        Assert.False(report.Partial);
        var group = Assert.Single(report.Duplicates);
        Assert.Equal(2, group.Paths.Count);
        Assert.Contains(one, group.Paths);
        Assert.Contains(two, group.Paths);
        Assert.Equal(new FileInfo(one).Length, group.ReclaimableLogicalBytes);
        Assert.Equal("matching-payload", File.ReadAllText(one));
        Assert.Equal("matching-payload", File.ReadAllText(two));
        Assert.Equal("different-bytes", File.ReadAllText(different));
    }

    [Fact]
    public async Task Scan_StopsAtBudgetAndLabelsResultPartial()
    {
        using var fixture = new TestDataRoot();
        for (var n = 0; n < 12; n++)
            File.WriteAllText(Path.Combine(fixture.Path, $"{n:D2}.txt"), $"{n:D2}");
        var report = await new StorageForensicsService(TimeSpan.FromSeconds(3), 3)
            .ScanAsync([fixture.Path]);
        Assert.True(report.Partial);
        Assert.InRange(report.ScannedFiles, 1, 3);
        Assert.Empty(report.Duplicates);
    }

    [Fact]
    public async Task Scan_RejectsCriticalWindowsRoots_WithoutTraversing()
    {
        var report = await new StorageForensicsService(TimeSpan.FromSeconds(3), 100)
            .ScanAsync([Environment.GetFolderPath(Environment.SpecialFolder.Windows)]);
        Assert.Empty(report.Duplicates);
        Assert.Equal(0, report.ScannedFiles);
        Assert.True(report.Partial);
    }

    [Fact]
    public async Task Scan_SameSizeDifferentHash_NotReportedAsDuplicate()
    {
        using var fixture = new TestDataRoot();
        File.WriteAllText(Path.Combine(fixture.Path, "a.txt"), "AAAA");
        File.WriteAllText(Path.Combine(fixture.Path, "b.txt"), "BBBB");
        var report = await new StorageForensicsService(TimeSpan.FromSeconds(3), 100)
            .ScanAsync([fixture.Path]);
        Assert.Empty(report.Duplicates);
    }

    [Fact]
    public async Task Scan_DoesNotFollowReparseDirectories()
    {
        using var fixture = new TestDataRoot();
        using var outside = new TestDataRoot();
        File.WriteAllText(Path.Combine(outside.Path, "private.txt"), "data");
        var link = Path.Combine(fixture.Path, "link");
        try { Directory.CreateSymbolicLink(link, outside.Path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { return; } // Host does not permit symlink creation.
        var report = await new StorageForensicsService(TimeSpan.FromSeconds(3), 100)
            .ScanAsync([fixture.Path]);
        Assert.Equal(0, report.ScannedFiles);
        Assert.True(report.SkippedLinks > 0);
    }
}
