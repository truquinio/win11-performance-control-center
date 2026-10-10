using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class StorageTempSafetyTests
{
    [Fact]
    public async Task TempCleanup_ProtectsProjectDatabasesAndEmulatorDisks_WhileRecyclingStaleTmp()
    {
        using var fixture = new TestDataRoot();
        var temp = Path.Combine(fixture.Path, "Temp");
        var bin = Path.Combine(fixture.Path, "Bin");
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(bin);
        foreach (var name in new[] { "stale.tmp", "bot_linkedin_test_123.sqlite", "emulator.qcow2", "secret.keystore" })
        {
            var path = Path.Combine(temp, name);
            File.WriteAllText(path, "fixture");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        }
        var service = new StorageAnalysisService(
            [new StorageAnalysisService.CacheTarget("eval.temp.user", "Fixture Temp", temp)],
            _ => false, fixture.Path,
            path => File.Move(path, Path.Combine(bin, Path.GetFileName(path))));
        var result = await service.CleanupSafeAsync();
        Assert.Single(Directory.GetFiles(bin));
        Assert.True(File.Exists(Path.Combine(bin, "stale.tmp")));
        Assert.Equal(1, result.DeletedFiles);
        Assert.True(File.Exists(Path.Combine(temp, "bot_linkedin_test_123.sqlite")));
        Assert.True(File.Exists(Path.Combine(temp, "emulator.qcow2")));
        Assert.True(File.Exists(Path.Combine(temp, "secret.keystore")));
    }
}
