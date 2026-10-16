using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class PageFileSnapshotRetentionTests
{
    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task Restore_WhenSnapshotDeleteIsDenied_ReportsRetainedRecoveryEvidence()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "pagefile-retained.json");
        var automatic = true;
        var writeCalls = 0;
        string[]? pagingFiles = [@"C:\pagefile.sys 16384 16384"];
        var backend = new PageFileTuningBackend(
            () => new PageFileTuningSnapshot(automatic, pagingFiles?.ToArray(), DateTimeOffset.UtcNow),
            value => { writeCalls++; automatic = value; return true; },
            values => { writeCalls++; pagingFiles = values?.ToArray(); return true; },
            drive => drive == "D");
        var service = new PageFileTuningService(state, backend);

        var applied = await service.ApplyCappedProfileAsync();
        Assert.True(applied.Success);
        Assert.True(File.Exists(state));

        // A read-sharing handle permits loading the snapshot but denies deletion on Windows.
        using (var lockSnapshot = new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var restored = await service.RestoreAsync();
            Assert.True(restored.Success);
            Assert.Equal("RESTORED_SNAPSHOT_RETAINED", restored.Status);
            Assert.True(restored.RestoreAvailable);
            Assert.True(File.Exists(state));
        }

        Assert.True(automatic);
        Assert.Equal([@"C:\pagefile.sys 16384 16384"], pagingFiles);

        // A retained recovery record is not permission to apply another WRITE.
        // The user must retry/verify the restore before changing pagefile again.
        var originalEvidence = await File.ReadAllBytesAsync(state);
        var writesBeforeRetry = writeCalls;
        var retriedApply = await service.ApplyCappedProfileAsync();
        Assert.False(retriedApply.Success);
        Assert.Equal("SNAPSHOT_RETAINED_REVIEW_REQUIRED", retriedApply.Status);
        Assert.True(retriedApply.RestoreAvailable);
        Assert.Equal(writesBeforeRetry, writeCalls);
        Assert.Equal(originalEvidence, await File.ReadAllBytesAsync(state));
    }
}
