using System.Diagnostics;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class RecoveryArchiveContractTests
{
    [Fact]
    public async Task Recovery_PreservesInterruptedAndCancelledWritesAcrossManyRotations()
    {
        // All journals are synthetic, created under TestDataRoot. No host WRITE.
        using var root = new TestDataRoot();
        var directory = Path.Combine(root.Path, "Logs");
        var logPath = Path.Combine(directory, "app.jsonl");
        var statePath = Path.Combine(root.Path, "State", "ecoqos.json");

        using (var logger = new AppLogger(logPath) { MaxLogBytes = 1 })
        {
            await logger.WriteAsync("test.early.interrupted", "STARTED",
                new { mode = "WRITE" }, "early-interrupted");
            await logger.WriteAsync("test.early.cancelled", "STARTED",
                new { mode = "WRITE" }, "early-cancelled");
            for (var i = 0; i < 9; i++)
            {
                await logger.WriteAsync("test.filler.read", "STARTED",
                    new { mode = "READ" }, "read-" + i);
                await logger.WriteAsync("test.filler.read", "COMPLETED",
                    operationId: "read-" + i);
            }
            await logger.WriteAsync("test.early.cancelled", "CANCELLED",
                operationId: "early-cancelled");
        }

        var archives = Directory.GetFiles(directory, "app-*.jsonl");
        Assert.True(archives.Length > 5,
            "Fixture must cross more than five log rotations.");
        var before = archives.ToDictionary(
            path => path,
            path => File.ReadAllBytes(path));

        var watch = Stopwatch.StartNew();
        var status = new OperationRecoveryService(
            new EcoQosStateStore(statePath), logPath).Analyze();
        watch.Stop();

        var interrupted = Assert.Single(status.IncompleteOperations);
        Assert.Equal("test.early.interrupted", interrupted.ActionId);
        Assert.Equal("STARTED", interrupted.LastStatus);
        var cancelled = Assert.Single(status.PotentiallyChangedCancelledOperations);
        Assert.Equal("test.early.cancelled", cancelled.ActionId);
        Assert.False(cancelled.Incomplete);
        Assert.Empty(status.PotentiallyChangedFailedOperations);

        // Recovery is READ-only: neither archives nor their bytes may change.
        foreach (var entry in before)
            Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key));
        Assert.Equal(archives.Length,
            Directory.GetFiles(directory, "app-*.jsonl").Length);
        Trace.WriteLine($"RECOVERY_ARCHIVE_SCAN_MS={watch.Elapsed.TotalMilliseconds:F2}; ARCHIVES={archives.Length}");
    }

    [Fact]
    public async Task Recovery_UnrelatedTerminalReadBeforeWriteStartCannotHideInterruptedWrite()
    {
        using var root = new TestDataRoot();
        var directory = Path.Combine(root.Path, "Logs");
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, "app-20261001-120000-000-other.jsonl");
        var active = Path.Combine(directory, "app.jsonl");
        var statePath = Path.Combine(root.Path, "State", "ecoqos.json");

        // Filesystem enumeration visits the unrelated terminal first.
        // A reused operationId must not hide a WRITE STARTED later.
        await File.WriteAllTextAsync(archive,
            JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Parse("2026-10-01T12:01:00Z"),
                operationId = "collision",
                actionId = "test.unrelated",
                status = "COMPLETED"
            }) + Environment.NewLine);
        await File.WriteAllTextAsync(active,
            JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Parse("2026-10-01T12:00:00Z"),
                operationId = "collision",
                actionId = "test.interrupted.write",
                status = "STARTED",
                data = new { mode = "WRITE" }
            }) + Environment.NewLine);

        var recovery = new OperationRecoveryService(
            new EcoQosStateStore(statePath), active).Analyze();
        var interrupted = Assert.Single(recovery.IncompleteOperations);
        Assert.Equal("test.interrupted.write", interrupted.ActionId);
    }
}
