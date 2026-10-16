using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class OutcomeAuditCorruptionTests
{
    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"text\"")]
    [InlineData("{\"timestamp\":\"2026-10-11T10:00:00+00:00\",\"status\":\"COMPLETED\"}")]
    public void OutcomeAudit_DamagedJournalCannotReportVerifiedCoverage(string damaged)
    {
        using var fixture = new TestDataRoot();
        var log = Path.Combine(fixture.Path, "Logs", "app.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log,
        [
            "{\"timestamp\":\"2026-10-11T10:00:00+00:00\",\"operationId\":\"op-1\",\"actionId\":\"startup.task.disable\",\"status\":\"STARTED\"}",
            damaged
        ]);

        // Fail closed: missing/corrupt records can conceal the terminal state
        // of a WRITE. Never return an apparently clean operation history.
        var ex = Assert.Throws<IOException>(() =>
            new OutcomeAuditService(new ActionCatalog(), log).Analyze());
        Assert.Contains("unverified", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("app.jsonl", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void OutcomeAudit_NonStringOperationIdFailsClosed(string malformedId)
    {
        using var fixture = new TestDataRoot();
        var log = Path.Combine(fixture.Path, "Logs", "app.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log,
            "{\"timestamp\":\"2026-10-11T10:00:00+00:00\",\"operationId\":" +
            malformedId +
            ",\"actionId\":\"startup.task.disable\",\"status\":\"COMPLETED\"}");

        var ex = Assert.Throws<IOException>(() =>
            new OutcomeAuditService(new ActionCatalog(), log).Analyze());
        Assert.Contains("unverified", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("app.jsonl", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line 1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutcomeAudit_FailedRejectedAndCancelledAreTerminal()
    {
        using var fixture = new TestDataRoot();
        var log = Path.Combine(fixture.Path, "Logs", "app.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        const string action = "startup.task.disable";
        File.WriteAllLines(log, new[]
        {
            "{\"timestamp\":\"2026-10-11T10:00:00+00:00\",\"operationId\":\"failure\",\"actionId\":\"" + action + "\",\"status\":\"STARTED\"}",
            "{\"timestamp\":\"2026-10-11T10:01:00+00:00\",\"operationId\":\"failure\",\"actionId\":\"" + action + "\",\"status\":\"FAILED\"}",
            "{\"timestamp\":\"2026-10-11T10:00:00+00:00\",\"operationId\":\"rejection\",\"actionId\":\"" + action + "\",\"status\":\"REJECTED\"}",
            "{\"timestamp\":\"2026-10-11T10:00:00+00:00\",\"operationId\":\"cancellation\",\"actionId\":\"" + action + "\",\"status\":\"CANCELLED\"}"
        });

        var audit = new OutcomeAuditService(new ActionCatalog(), log).Analyze();
        var item = Assert.Single(audit.Actions, item => item.ActionId == action);
        Assert.Equal(0, item.CompletedRuns);
        Assert.Equal(1, item.FailedRuns);
        Assert.Equal(1, item.RejectedRuns);
        var serialized = JsonSerializer.SerializeToElement(item, HostBridge.JsonOptions);
        Assert.Equal(1, serialized.GetProperty("cancelledRuns").GetInt32());
        Assert.Equal(0, item.IncompleteRuns);
        Assert.Equal(0, audit.IncompleteRuns);
    }

    [Fact]
    public void OutcomeAudit_ReusedOperationIdAcrossActionsFailsClosed()
    {
        using var fixture = new TestDataRoot();
        var log = Path.Combine(fixture.Path, "Logs", "app.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log,
        [
            "{\"timestamp\":\"2026-10-11T10:00:00+00:00\",\"operationId\":\"op-collision\",\"actionId\":\"startup.task.disable\",\"status\":\"STARTED\"}",
            "{\"timestamp\":\"2026-10-11T10:01:00+00:00\",\"operationId\":\"op-collision\",\"actionId\":\"startup.service.setmode\",\"status\":\"COMPLETED\"}"
        ]);

        var ex = Assert.Throws<IOException>(() =>
            new OutcomeAuditService(new ActionCatalog(), log).Analyze());
        Assert.Contains("unverified", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("app.jsonl", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticBundle_DamagedOutcomeJournalKeepsSanitizedEvidence()
    {
        using var fixture = new TestDataRoot();
        var directory = Path.Combine(fixture.Path, "Logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "app-20261011.jsonl"),
            "{broken");
        using var bridge = HostBridge.CreateDefault(fixture.Path);
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "diagnostic-damaged-audit",
            method = "actions.run",
            payload = new
            {
                id = "diagnostics.bundle.create",
                parameters = new { confirmed = true }
            }
        }, HostBridge.JsonOptions);
        var response = await bridge.HandleAsync(request);
        Assert.True(response.Ok, response.Error);
        var result = Assert.IsType<ActionResult>(response.Result);
        Assert.True(result.Success, result.Message);
        var bundle = Assert.IsType<DiagnosticBundleResult>(result.Data);
        using var zip = ZipFile.OpenRead(bundle.Path);
        var entry = Assert.Single(zip.Entries,
            item => item.FullName == "outcomes.json");
        using var reader = new StreamReader(entry.Open());
        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal("UNVERIFIED",
            json.RootElement.GetProperty("verificationStatus").GetString());
        Assert.Equal(JsonValueKind.Null,
            json.RootElement.GetProperty("coveragePercent").ValueKind);
    }

    [Fact]
    public void OutcomeAudit_DamagedRotatedJournalCannotReportVerifiedCoverage()
    {
        using var fixture = new TestDataRoot();
        var directory = Path.Combine(fixture.Path, "Logs");
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "app.jsonl");
        File.WriteAllText(log, "");
        File.WriteAllText(Path.Combine(directory, "app-20261011.jsonl"),
            "{\"timestamp\":\"2026-10-11T10:00:00+00:00\",\"operationId\":\"op-1\",\"actionId\":\"startup.task.disable\",\"status\":\"STARTED\"}\n{broken");

        var ex = Assert.Throws<IOException>(() =>
            new OutcomeAuditService(new ActionCatalog(), log).Analyze());
        Assert.Contains("unverified", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("app-20261011.jsonl", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}