using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class LoggerRecoveryTests
{
    [Fact]
    public async Task AppLogger_SerializesConcurrentWriters()
    {
        using var dataRoot = new TestDataRoot();
        var logPath = Path.Combine(
            dataRoot.Path,
            "Logs",
            "app.jsonl");
        var operationIds = Enumerable.Range(0, 12)
            .Select(_ => Guid.NewGuid().ToString("N"))
            .ToArray();

        var tasks = operationIds.Select(async operationId =>
        {
            using var logger = new AppLogger(logPath);
            await logger.WriteAsync(
                "test.concurrent.log",
                "STARTED",
                operationId: operationId);
            await logger.WriteAsync(
                "test.concurrent.log",
                "COMPLETED",
                operationId: operationId);
        });

        await Task.WhenAll(tasks);

        var lines = await File.ReadAllLinesAsync(logPath);
        Assert.Equal(operationIds.Length * 2, lines.Length);

        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty(
                "operationId",
                out _));
        }
    }

    [Fact]
    public async Task Recovery_ExcludesTheOperationPerformingTheAudit()
    {
        using var dataRoot = new TestDataRoot();
        var logPath = Path.Combine(
            dataRoot.Path,
            "Logs",
            "app.jsonl");
        var statePath = Path.Combine(
            dataRoot.Path,
            "State",
            "ecoqos.json");
        var operationId = Guid.NewGuid().ToString("N");
        var actionId = "test.recovery." + operationId;

        using var logger = new AppLogger(logPath);
        await logger.WriteAsync(
            actionId,
            "STARTED",
            operationId: operationId);

        try
        {
            var service = new OperationRecoveryService(
                new EcoQosStateStore(statePath),
                logPath);
            var status = service.Analyze(operationId);

            Assert.DoesNotContain(
                status.IncompleteOperations,
                item => item.ActionId == actionId);
        }
        finally
        {
            await logger.WriteAsync(
                actionId,
                "COMPLETED",
                operationId: operationId);
        }
    }

    [Fact]
    public async Task Recovery_IgnoresWellFormedJsonWithInvalidFieldTypes()
    {
        using var dataRoot = new TestDataRoot();
        var logPath = Path.Combine(dataRoot.Path, "Logs", "app.jsonl");
        var statePath = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        await File.WriteAllLinesAsync(
            logPath,
            [
                """{"actionId":123,"status":"STARTED","operationId":"bad-action","timestamp":"2026-10-01T12:00:00Z"}""",
                """{"actionId":"test.invalid","status":456,"operationId":"bad-status","timestamp":"2026-10-01T12:00:00Z"}""",
                """{"actionId":"test.invalid-time","status":"STARTED","operationId":"bad-time","timestamp":789}"""
            ]);

        var service = new OperationRecoveryService(
            new EcoQosStateStore(statePath),
            logPath);

        var status = service.Analyze();

        Assert.Empty(status.IncompleteOperations);
    }

    [Fact]
    public async Task AppLogger_RecordAfterTornWriteStaysReadable()
    {
        using var dataRoot = new TestDataRoot();
        var logPath = Path.Combine(dataRoot.Path, "Logs", "app.jsonl");
        var statePath = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        // A previous process died mid-write: half a record, no terminator.
        await File.WriteAllTextAsync(
            logPath,
            """{"timestamp":"2026-10-01T12:00:00Z","operationId":"torn","actionId":"test.to""");

        var operationId = Guid.NewGuid().ToString("N");
        using (var logger = new AppLogger(logPath))
        {
            await logger.WriteAsync(
                "test.after.torn",
                "STARTED",
                new { mode = "WRITE" },
                operationId);
        }

        var lines = await File.ReadAllLinesAsync(logPath);
        Assert.Equal(2, lines.Length);
        using (var document = JsonDocument.Parse(lines[1]))
        {
            Assert.Equal(
                operationId,
                document.RootElement.GetProperty("operationId").GetString());
        }

        var status = new OperationRecoveryService(
            new EcoQosStateStore(statePath),
            logPath).Analyze();
        var operation = Assert.Single(status.IncompleteOperations);
        Assert.Equal("test.after.torn", operation.ActionId);
    }

    [Fact]
    public async Task Recovery_IgnoresValidJsonLinesThatAreNotRecords()
    {
        using var dataRoot = new TestDataRoot();
        var logPath = Path.Combine(dataRoot.Path, "Logs", "app.jsonl");
        var statePath = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        await File.WriteAllLinesAsync(
            logPath,
            [
                "[]",
                "42",
                "\"text\"",
                "null",
                "true",
                """{"actionId":"test.write","status":"STARTED","operationId":"real","timestamp":"2026-10-01T12:00:00Z","data":[]}"""
            ]);

        var service = new OperationRecoveryService(
            new EcoQosStateStore(statePath),
            logPath);

        var status = service.Analyze();

        // The non-record lines are skipped and the real record survives them.
        var operation = Assert.Single(status.IncompleteOperations);
        Assert.Equal("test.write", operation.ActionId);
    }

    [Theory]
    [InlineData("READ", false)]
    [InlineData("DRY_RUN", false)]
    [InlineData("WRITE", true)]
    [InlineData(null, true)]
    public async Task Recovery_ReportsOnlyInterruptedOperationsThatCouldChangeState(
        string? mode,
        bool expectedReported)
    {
        using var dataRoot = new TestDataRoot();
        var logPath = Path.Combine(dataRoot.Path, "Logs", "app.jsonl");
        var statePath = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var data = mode is null ? "{}" : $$"""{"mode":"{{mode}}"}""";
        await File.WriteAllLinesAsync(
            logPath,
            [
                $$"""{"actionId":"test.interrupted","status":"STARTED","operationId":"interrupted","timestamp":"2026-10-01T12:00:00Z","data":{{data}}}"""
            ]);

        var service = new OperationRecoveryService(
            new EcoQosStateStore(statePath),
            logPath);

        var status = service.Analyze();

        Assert.Equal(
            expectedReported,
            status.IncompleteOperations.Any(
                item => item.ActionId == "test.interrupted"));
    }

    [Fact]
    public async Task Executor_StartedRecordDeclaresTheActionMode()
    {
        // Shape check across the two components: the STARTED record written
        // by the real executor carries the mode the recovery reader relies on.
        using var dataRoot = new TestDataRoot();
        var logPath = Path.Combine(dataRoot.Path, "Logs", "app.jsonl");

        using (var bridge = dataRoot.CreateBridge())
        {
            await bridge.HandleSerializedAsync(
                """{"type":"request","requestId":"r","method":"actions.run","payload":{"id":"privacy.audit"}}""");
        }

        var started = (await File.ReadAllLinesAsync(logPath))
            .Single(line => line.Contains("\"STARTED\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(started);
        Assert.Equal(
            "READ",
            document.RootElement.GetProperty("data").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Recovery_UsesRecordTimestampsWhenRotatedLogsAreOutOfOrder()
    {
        using var dataRoot = new TestDataRoot();
        var logDirectory = Path.Combine(dataRoot.Path, "Logs");
        var logPath = Path.Combine(logDirectory, "app.jsonl");
        var statePath = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        Directory.CreateDirectory(logDirectory);

        var operationId = Guid.NewGuid().ToString("N");
        var completedArchive = Path.Combine(
            logDirectory,
            "app-20261002-120100-000-complete.jsonl");
        var staleArchive = Path.Combine(
            logDirectory,
            "app-20261002-120000-000-started.jsonl");

        await File.WriteAllTextAsync(
            completedArchive,
            JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Parse("2026-10-02T12:01:00Z"),
                operationId,
                actionId = "test.recovery.order",
                status = "COMPLETED"
            }) + Environment.NewLine);
        await File.WriteAllTextAsync(
            staleArchive,
            JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Parse("2026-10-02T12:00:00Z"),
                operationId,
                actionId = "test.recovery.order",
                status = "STARTED"
            }) + Environment.NewLine);

        // Deliberately make filesystem metadata disagree with record time.
        File.SetLastWriteTimeUtc(completedArchive, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(staleArchive, DateTime.UtcNow.AddMinutes(-1));

        var service = new OperationRecoveryService(
            new EcoQosStateStore(statePath),
            logPath);

        var status = service.Analyze();

        Assert.DoesNotContain(
            status.IncompleteOperations,
            item => item.ActionId == "test.recovery.order");
    }

    [Fact]
    public async Task AppLogger_TryWriteAfterDispose_ReturnsFalse()
    {
        using var dataRoot = new TestDataRoot();
        var logPath = Path.Combine(dataRoot.Path, "Logs", "app.jsonl");
        var logger = new AppLogger(logPath);
        logger.Dispose();

        var written = await logger.TryWriteAsync(
            "test.disposed.log",
            "FAILED");

        Assert.False(written);
    }

}
