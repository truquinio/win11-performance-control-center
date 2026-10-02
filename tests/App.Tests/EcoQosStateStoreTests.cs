using Win11PerformanceControlCenter.App.Core;

namespace App.Tests;

public sealed class EcoQosStateStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "WPCC-Tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveBaseline_PreservesOriginalStateForSameProcessInstance()
    {
        var path = Path.Combine(_directory, "state.json");
        var store = new EcoQosStateStore(path);
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);

        var first = new EcoQosOriginalState(
            1234, "example", started, 1, 0, DateTimeOffset.UtcNow);
        var second = first with
        {
            ControlMask = 99,
            StateMask = 99,
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(1)
        };

        Assert.True(store.SaveBaseline(first));
        Assert.False(store.SaveBaseline(second));
        Assert.True(store.TryGet(1234, out var saved));
        Assert.Equal(first.ControlMask, saved.ControlMask);
        Assert.Equal(first.StateMask, saved.StateMask);
    }

    [Fact]
    public void SaveBaseline_ReplacesStaleSnapshotWhenPidIsReused()
    {
        var path = Path.Combine(_directory, "state.json");
        var store = new EcoQosStateStore(path);
        var first = new EcoQosOriginalState(
            4321,
            "old-process",
            DateTimeOffset.UtcNow.AddHours(-1),
            1,
            1,
            DateTimeOffset.UtcNow.AddHours(-1));
        var replacement = new EcoQosOriginalState(
            4321,
            "new-process",
            DateTimeOffset.UtcNow,
            2,
            0,
            DateTimeOffset.UtcNow);

        Assert.True(store.SaveBaseline(first));
        Assert.True(store.SaveBaseline(replacement));
        Assert.True(store.TryGet(4321, out var saved));
        Assert.Equal("new-process", saved.Name);
        Assert.Equal(replacement.ProcessStartTime, saved.ProcessStartTime);
        Assert.Equal((uint)2, saved.ControlMask);
    }


    [Fact]
    public void Load_FallsBackToBackupWhenPrimaryIsCorrupt()
    {
        var path = Path.Combine(_directory, "state.json");
        var store = new EcoQosStateStore(path);
        var state = new EcoQosOriginalState(
            2468,
            "recoverable-process",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            3,
            1,
            DateTimeOffset.UtcNow);

        Assert.True(store.SaveBaseline(state));
        Assert.True(File.Exists(path + ".bak"));

        File.WriteAllText(path, "{corrupt-json");

        var recovered = new EcoQosStateStore(path);

        Assert.True(recovered.TryGet(2468, out var restored));
        Assert.Equal(state.Name, restored.Name);
        Assert.Equal(state.ProcessStartTime, restored.ProcessStartTime);
        Assert.Equal(state.ControlMask, restored.ControlMask);
        Assert.Equal(state.StateMask, restored.StateMask);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }
}
