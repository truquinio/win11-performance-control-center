using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class BrowserExtensionRemediationServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "wpcc-edge-remediation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task OrphanData_QuarantinesAndRestoresWithoutDeletion()
    {
        var edge = Path.Combine(root, "EdgeUserData");
        var quarantine = Path.Combine(root, "Quarantine");
        var profile = Path.Combine(edge, "Default");
        var id = "abcdefghijklmnopabcdefghijklmnop";
        var orphan = Path.Combine(
            profile,
            "Local Extension Settings",
            id);
        Directory.CreateDirectory(orphan);
        await File.WriteAllBytesAsync(
            Path.Combine(orphan, "data.bin"),
            new byte[4096]);

        var service = new BrowserExtensionRemediationService(
            edge,
            quarantine,
            () => false,
            () => new DateTimeOffset(
                2026, 10, 7, 18, 0, 0, TimeSpan.Zero));

        var preview = service.Preview();
        Assert.Equal(1, preview.CandidateCount);
        Assert.True(preview.TotalBytes >= 4096);
        Assert.False(preview.EdgeRunning);

        var moved = await service.QuarantineAsync();
        Assert.True(moved.Success);
        Assert.Equal(1, moved.MovedCount);
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(quarantine));

        var after = service.Preview();
        Assert.Equal(0, after.CandidateCount);
        Assert.True(after.RestoreAvailable);

        var restored = await service.RestoreLatestAsync();
        Assert.True(restored.Success);
        Assert.Equal(1, restored.RestoredCount);
        Assert.True(Directory.Exists(orphan));
        Assert.True(File.Exists(Path.Combine(orphan, "data.bin")));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task EdgeRunning_BlocksQuarantineAndPreservesData()
    {
        var edge = Path.Combine(root, "EdgeUserData");
        var profile = Path.Combine(edge, "Default");
        var id = "bcdefghijklmnopabcdefghijklmnopa";
        var orphan = Path.Combine(
            profile,
            "Local Extension Settings",
            id);
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(
            Path.Combine(orphan, "state.txt"),
            "keep");

        var service = new BrowserExtensionRemediationService(
            edge,
            Path.Combine(root, "Quarantine"),
            () => true);

        var result = await service.QuarantineAsync();

        Assert.False(result.Success);
        Assert.Equal("EDGE_RUNNING", result.Status);
        Assert.True(Directory.Exists(orphan));
        Assert.True(File.Exists(Path.Combine(orphan, "state.txt")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }
}
