using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class StorageRecycleTests
{
    [Fact]
    public async Task Cleanup_UsesRecyclingAdapter_NeverPermanentlyDeletesAndKeepsRecentFiles()
    {
        using var fixture = new TestDataRoot();
        var cache = Path.Combine(fixture.Path, "cache");
        var bin = Path.Combine(fixture.Path, "simulated-bin");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(bin);
        var oldFile = Path.Combine(cache, "old.tmp");
        var recentFile = Path.Combine(cache, "recent.tmp");
        File.WriteAllText(oldFile, "old");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-9));
        File.WriteAllText(recentFile, "recent");
        var service = new StorageAnalysisService(
            [new StorageAnalysisService.CacheTarget("eval.cache", "Fixture cache", cache)],
            _ => false, fixture.Path, path => File.Move(path, Path.Combine(bin, Path.GetFileName(path))));
        var preview = await service.AnalyzeSafeAsync();
        Assert.Equal(3, preview.EstimatedBytes);
        var result = await service.CleanupSafeAsync();
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(3, result.DeletedBytes);
        Assert.False(File.Exists(oldFile));
        Assert.Equal("old", File.ReadAllText(Path.Combine(bin, "old.tmp")));
        Assert.Equal("recent", File.ReadAllText(recentFile));
    }

    [Fact]
    public async Task Cleanup_RecycleFailureLeavesOriginalIntactAndReportsFailedFile()
    {
        using var fixture = new TestDataRoot();
        var cache = Path.Combine(fixture.Path, "cache");
        Directory.CreateDirectory(cache);
        var source = Path.Combine(cache, "old.tmp");
        File.WriteAllText(source, "untouched");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-9));
        var service = new StorageAnalysisService(
            [new StorageAnalysisService.CacheTarget("eval.cache", "Fixture cache", cache)],
            _ => false, fixture.Path, _ => throw new IOException("simulated locked file"));
        var result = await service.CleanupSafeAsync();
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal("untouched", File.ReadAllText(source));
    }

    [Fact]
    public async Task Cleanup_SkipsRunningProcessesAndNeverInvokesRecycler()
    {
        using var fixture = new TestDataRoot();
        var cache = Path.Combine(fixture.Path, "cache");
        Directory.CreateDirectory(cache);
        var source = Path.Combine(cache, "old.tmp");
        File.WriteAllText(source, "unchanged");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-9));
        var invoked = false;
        var service = new StorageAnalysisService(
            [new StorageAnalysisService.CacheTarget("eval.cache", "Fixture cache", cache,
                ProcessName: "fictitious")],
            _ => true, fixture.Path, _ => invoked = true);
        var result = await service.CleanupSafeAsync();
        Assert.False(invoked);
        Assert.DoesNotContain(result.SkippedCategories, x => x.Contains("broken"));
        Assert.NotEmpty(result.SkippedCategories);
        Assert.True(File.Exists(source));
    }
}
