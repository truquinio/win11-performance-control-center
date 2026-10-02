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
}
