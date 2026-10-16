using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class RecoveryScaleContractTests
{
    [Fact]
    public void Recovery_Scans128RotatedJournalsWithoutLosingWriteEvidenceOrModifyingArchives()
    {
        // Synthetic fixture only. No real host journals or Windows WRITE actions.
        using var root = new TestDataRoot();
        var logs = Path.Combine(root.Path, "Logs");
        Directory.CreateDirectory(logs);
        var active = Path.Combine(logs, "app.jsonl");
        var baseline = DateTimeOffset.Parse("2026-10-01T00:00:00+00:00");
        var archivePaths = new List<string>();

        for (var index = 0; index < 128; index++)
        {
            var timestamp = baseline.AddMinutes(index);
            var lines = new List<string>();
            for (var read = 0; read < 8; read++)
            {
                var id = $"read-{index}-{read}";
                lines.Add(Record(timestamp, id, "test.read", "STARTED", "READ"));
                lines.Add(Record(timestamp.AddSeconds(1), id, "test.read", "COMPLETED"));
            }

            if (index == 0)
            {
                lines.Add(Record(timestamp, "write-incomplete", "test.write.incomplete", "STARTED", "WRITE"));
                lines.Add(Record(timestamp, "write-cancelled", "test.write.cancelled", "STARTED", "WRITE"));
                lines.Add(Record(timestamp, "write-failed", "test.write.failed", "STARTED", "WRITE"));
            }
            if (index == 127)
            {
                lines.Add(Record(timestamp.AddSeconds(2), "write-cancelled", "test.write.cancelled", "CANCELLED"));
                lines.Add(Record(timestamp.AddSeconds(2), "write-failed", "test.write.failed", "FAILED"));
            }

            var archive = Path.Combine(logs, $"app-20261001-{index:000000}-000-scale.jsonl");
            File.WriteAllLines(archive, lines);
            archivePaths.Add(archive);
        }

        File.WriteAllText(active, string.Empty);
        var hashes = archivePaths.ToDictionary(
            path => path,
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var watch = Stopwatch.StartNew();
        var status = new OperationRecoveryService(
            new EcoQosStateStore(Path.Combine(root.Path, "State", "ecoqos.json")),
            active).Analyze();
        watch.Stop();

        Assert.Equal("test.write.incomplete", Assert.Single(status.IncompleteOperations).ActionId);
        Assert.Equal("test.write.cancelled", Assert.Single(status.PotentiallyChangedCancelledOperations).ActionId);
        Assert.Equal("test.write.failed", Assert.Single(status.PotentiallyChangedFailedOperations).ActionId);
        Assert.Equal(128, Directory.GetFiles(logs, "app-*.jsonl").Length);
        foreach (var (path, hash) in hashes)
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

        Trace.WriteLine($"RECOVERY_SCALE_ARCHIVES=128; RECORDS=2053; ELAPSED_MS={watch.Elapsed.TotalMilliseconds:F2}; READ_ONLY=true");
    }

    private static string Record(
        DateTimeOffset timestamp,
        string operationId,
        string actionId,
        string status,
        string? mode = null) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            operationId,
            actionId,
            status,
            data = mode is null ? null : new { mode }
        });
}
