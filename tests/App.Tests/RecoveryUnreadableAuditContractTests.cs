using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class RecoveryUnreadableAuditContractTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"text\"")]
    public void Recovery_NonObjectJsonCannotClaimClean(string record)
    {
        using var root = new TestDataRoot();
        var directory = Path.Combine(root.Path, "Logs");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "app.jsonl");
        File.WriteAllText(logPath, record + Environment.NewLine);
        var recovery = new OperationRecoveryService(
            new EcoQosStateStore(Path.Combine(root.Path, "State", "ecoqos.json")),
            logPath);
        var error = Assert.Throws<IOException>(() => recovery.Analyze());
        Assert.Contains("no está verificado", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"timestamp":"2026-10-11T12:00:00Z","operationId":"op","status":"STARTED","data":{"mode":"WRITE"}}""")]
    [InlineData("""{"timestamp":"2026-10-11T12:00:00Z","operationId":"op","actionId":123,"status":"STARTED","data":{"mode":"WRITE"}}""")]
    [InlineData("""{"timestamp":"2026-10-11T12:00:00Z","operationId":"op","actionId":"test.write","data":{"mode":"WRITE"}}""")]
    [InlineData("""{"timestamp":789,"operationId":"op","actionId":"test.write","status":"STARTED","data":{"mode":"WRITE"}}""")]
    [InlineData("""{"operationId":"op","actionId":"test.write","status":"STARTED","data":{"mode":"WRITE"}}""")]
    public void Recovery_ValidJsonMissingAuditFieldsCannotClaimClean(string malformedRecord)
    {
        using var root = new TestDataRoot();
        var directory = Path.Combine(root.Path, "Logs");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "app.jsonl");
        File.WriteAllText(logPath, malformedRecord + Environment.NewLine);
        var recovery = new OperationRecoveryService(
            new EcoQosStateStore(Path.Combine(root.Path, "State", "ecoqos.json")),
            logPath);
        var error = Assert.Throws<IOException>(() => recovery.Analyze());
        Assert.Contains("no está verificado", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("backup.status")]
    [InlineData("backup.rollback.center")]
    public async Task Recovery_ValidJsonMissingFieldsIsRejectedByIpc(string actionId)
    {
        using var root = new TestDataRoot();
        var directory = Path.Combine(root.Path, "Logs");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "app.jsonl"),
            """{"timestamp":"2026-10-11T12:00:00Z","status":"STARTED","data":{"mode":"WRITE"}}""" + Environment.NewLine);
        using var bridge = root.CreateBridge();
        var responseJson = await bridge.HandleSerializedAsync(
            JsonSerializer.Serialize(new
            {
                type = "request",
                requestId = "invalid-fields-" + actionId,
                method = "actions.run",
                payload = new { id = actionId }
            }));
        using var response = JsonDocument.Parse(responseJson);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("no está verificado",
            response.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recovery_UnreadableRotatedAuditCannotBeReportedAsClean()
    {
        // Isolated synthetic journals; a locked archive may contain WRITE.
        using var root = new TestDataRoot();
        var directory = Path.Combine(root.Path, "Logs");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "app.jsonl");
        var archive = Path.Combine(directory, "app-20261001-000000-000-locked.jsonl");
        File.WriteAllText(archive,
            """{"timestamp":"2026-10-01T00:00:00Z","operationId":"locked","actionId":"test.write","status":"STARTED","data":{"mode":"WRITE"}}""" + Environment.NewLine);

        using var lockStream = new FileStream(archive, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        var recovery = new OperationRecoveryService(
            new EcoQosStateStore(Path.Combine(root.Path, "State", "ecoqos.json")),
            logPath);
        Assert.Throws<IOException>(() => recovery.Analyze());
    }

    [Theory]
    [InlineData("backup.status")]
    [InlineData("backup.rollback.center")]
    public async Task Recovery_IpcRejectsFalseCleanReportWhenArchiveIsLocked(string actionId)
    {
        using var root = new TestDataRoot();
        var directory = Path.Combine(root.Path, "Logs");
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, "app-20261001-000000-000-locked.jsonl");
        await File.WriteAllTextAsync(archive,
            """{"timestamp":"2026-10-01T00:00:00Z","operationId":"locked","actionId":"test.write","status":"STARTED"}""" + Environment.NewLine);

        using var lockStream = new FileStream(archive, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        using var bridge = root.CreateBridge();
        var responseJson = await bridge.HandleSerializedAsync(
            JsonSerializer.Serialize(new
            {
                type = "request",
                requestId = actionId,
                method = "actions.run",
                payload = new { id = actionId }
            }));
        using var response = JsonDocument.Parse(responseJson);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("no está verificado",
            response.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recovery_TruncatedWriteStartCannotBeReportedAsClean()
    {
        using var root = new TestDataRoot();
        var directory = Path.Combine(root.Path, "Logs");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "app.jsonl");
        // A crash can leave the last JSONL record incomplete, including
        // the only evidence of a WRITE beginning before interruption.
        File.WriteAllText(logPath,
            "{\"timestamp\":\"2026-10-11T12:00:00Z\",\"operationId\":\"partial\",\"actionId\":\"test.write\",\"status\":\"STARTED\",\"data\":{\"mode\":\"WRITE\"" + Environment.NewLine);
        var recovery = new OperationRecoveryService(
            new EcoQosStateStore(Path.Combine(root.Path, "State", "ecoqos.json")),
            logPath);
        var error = Assert.Throws<IOException>(() => recovery.Analyze());
        Assert.Contains("no está verificado", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("backup.status")]
    [InlineData("backup.rollback.center")]
    public async Task Recovery_TruncatedAuditIsRejectedByIpc(string actionId)
    {
        using var root = new TestDataRoot();
        var directory = Path.Combine(root.Path, "Logs");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "app.jsonl"),
            "{\"timestamp\":\"2026-10-11T12:00:00Z\",\"actionId\":\"test.write\",\"status\":\"STARTED\"" + Environment.NewLine);
        using var bridge = root.CreateBridge();
        var responseJson = await bridge.HandleSerializedAsync(
            JsonSerializer.Serialize(new
            {
                type = "request",
                requestId = "truncated-" + actionId,
                method = "actions.run",
                payload = new { id = actionId }
            }));
        using var response = JsonDocument.Parse(responseJson);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("no está verificado",
            response.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }
}
