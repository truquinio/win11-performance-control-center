using System.Text;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class PageFileCorruptSnapshotTests
{
    [Theory]
    [InlineData("{ definitely not JSON")]
    [InlineData("null")]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task Apply_UnreadableSnapshotPreservesRecoveryBytesAndNeverMutates(string contents)
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "pagefile-corrupt-apply.json");
        var original = Encoding.UTF8.GetBytes(contents);
        File.WriteAllBytes(state, original);
        var mutations = 0;
        var backend = new PageFileTuningBackend(
            () => new PageFileTuningSnapshot(true, [@"C:\pagefile.sys 0 0"], DateTimeOffset.UtcNow),
            _ => { mutations++; return true; },
            _ => { mutations++; return true; },
            _ => true);
        var service = new PageFileTuningService(state, backend);

        var result = await service.ApplyCappedProfileAsync();

        Assert.False(result.Success);
        Assert.Equal("SNAPSHOT_UNREADABLE", result.Status);
        Assert.True(result.RestoreAvailable);
        Assert.Equal(0, mutations);
        Assert.Equal(original, File.ReadAllBytes(state));
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task Apply_ConcurrentSnapshotCreationMustNotReplaceRecoveryRecord()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "pagefile-racing.json");
        var recoveryBytes = Encoding.UTF8.GetBytes(
            """{"automaticManaged":true,"pagingFiles":["C:\\pagefile.sys 0 0"],"capturedAt":"2026-10-11T00:00:00Z"}""");
        var mutations = 0;
        var backend = new PageFileTuningBackend(
            () =>
            {
                File.WriteAllBytes(state, recoveryBytes); // Simulate a competing writer after the initial read.
                return new PageFileTuningSnapshot(true, [@"C:\pagefile.sys 0 0"], DateTimeOffset.UtcNow);
            },
            _ => { mutations++; return true; },
            _ => { mutations++; return true; },
            _ => true);
        var service = new PageFileTuningService(state, backend);

        var result = await service.ApplyCappedProfileAsync();

        Assert.False(result.Success);
        Assert.Equal("SNAPSHOT_SAVE_FAILED", result.Status);
        Assert.True(result.RestoreAvailable);
        Assert.Equal(0, mutations);
        Assert.Equal(recoveryBytes, File.ReadAllBytes(state));
    }

    [Theory]
    [InlineData("{ definitely not JSON")]
    [InlineData("null")]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task Restore_UnreadableSnapshotPreservesRecoveryBytesAndNeverMutates(string contents)
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "pagefile-corrupt-restore.json");
        var original = Encoding.UTF8.GetBytes(contents);
        File.WriteAllBytes(state, original);
        var mutations = 0;
        var backend = new PageFileTuningBackend(
            () => new PageFileTuningSnapshot(false, [@"D:\pagefile.sys 4096 8192"], DateTimeOffset.UtcNow),
            _ => { mutations++; return true; },
            _ => { mutations++; return true; },
            _ => true);
        var service = new PageFileTuningService(state, backend);

        var result = await service.RestoreAsync();

        Assert.False(result.Success);
        Assert.Equal("SNAPSHOT_UNREADABLE", result.Status);
        Assert.True(result.RestoreAvailable);
        Assert.Equal(0, mutations);
        Assert.Equal(original, File.ReadAllBytes(state));
    }
}
