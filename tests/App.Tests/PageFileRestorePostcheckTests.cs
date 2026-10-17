using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class PageFileRestorePostcheckTests
{
    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task Apply_UnknownPagingFilesBaselineMustNotChangeAutomaticMode()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "pagefile-unknown.json");
        var automatic = true;
        var mutations = 0;
        var backend = new PageFileTuningBackend(
            () => new PageFileTuningSnapshot(automatic, null, DateTimeOffset.UtcNow),
            value => { mutations++; automatic = value; return true; },
            _ => { mutations++; return true; },
            drive => drive == "D");
        var service = new PageFileTuningService(state, backend);

        var result = await service.ApplyCappedProfileAsync();
        Assert.False(result.Success);
        Assert.Equal("STATE_UNKNOWN", result.Status);
        Assert.Equal(0, mutations);
        Assert.True(automatic);
        Assert.False(File.Exists(state));
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task Restore_MustRetainSnapshotWhenPagingFilesWriteReportsSuccessWithoutApplying()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "pagefile-postcheck.json");
        var automatic = true;
        string[]? pagingFiles = [@"C:\pagefile.sys 16384 16384"];
        var ignoreRestoreWrite = false;

        var backend = new PageFileTuningBackend(
            () => new PageFileTuningSnapshot(
                automatic, pagingFiles?.ToArray(), DateTimeOffset.UtcNow),
            value => { automatic = value; return true; },
            values =>
            {
                if (!ignoreRestoreWrite)
                    pagingFiles = values?.ToArray();
                return true; // Simulates an OS API claiming success without applying the change.
            },
            drive => drive == "D");
        var service = new PageFileTuningService(state, backend);
        var applied = await service.ApplyCappedProfileAsync();
        Assert.True(applied.Success);
        Assert.True(File.Exists(state));

        ignoreRestoreWrite = true;
        var restored = await service.RestoreAsync();
        Assert.False(restored.Success);
        Assert.Equal("VERIFY_FAILED", restored.Status);
        Assert.True(restored.RestoreAvailable);
        Assert.True(File.Exists(state)); // Never discard the only recovery snapshot.
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task Restore_LegacySnapshotWithUnknownPagingFilesMustFailClosed()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "pagefile-legacy.json");
        File.WriteAllText(state,
            """{"automaticManaged":true,"pagingFiles":null,"capturedAt":"2026-10-11T00:00:00Z"}""");
        var mutations = 0;
        var backend = new PageFileTuningBackend(
            () => new PageFileTuningSnapshot(false, [@"D:\pagefile.sys 4096 8192"], DateTimeOffset.UtcNow),
            _ => { mutations++; return true; },
            _ => { mutations++; return true; },
            _ => true);
        var service = new PageFileTuningService(state, backend);

        var result = await service.RestoreAsync();
        Assert.False(result.Success);
        Assert.Equal("SNAPSHOT_INCOMPLETE", result.Status);
        Assert.True(result.RestoreAvailable);
        Assert.Equal(0, mutations);
        Assert.True(File.Exists(state));
    }
}
