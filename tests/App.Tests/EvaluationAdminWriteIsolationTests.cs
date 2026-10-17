using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace App.Tests;

/// <summary>
/// A HostBridge built with a test dataRoot must never elevate or execute
/// a host-level administrator WRITE, even when consent is supplied.
/// </summary>
public sealed class EvaluationAdminWriteIsolationTests
{
    public static IEnumerable<object[]> AdminWriteIds() =>
        new ActionCatalog().All
            .Where(action => action.Mode == ActionMode.WRITE && action.RequiresAdmin)
            .OrderBy(action => action.Id, StringComparer.Ordinal)
            .Select(action => new object[] { action.Id })
            .ToArray();

    [Theory]
    [MemberData(nameof(AdminWriteIds))]
    [Trait("Layer", "EcoQa35")]
    public async Task EvaluationBridge_RejectsConfirmedAdminWriteBeforeUac(string actionId)
    {
        using var root = new TestDataRoot();
        using var bridge = root.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "eval-admin-block-" + Guid.NewGuid().ToString("N"),
            method = "actions.run",
            payload = new { id = actionId, parameters = new { confirmed = true } }
        });

        var response = await bridge.HandleAsync(request);

        Assert.False(response.Ok);
        Assert.Contains("EVALUATION_ADMIN_WRITE_BLOCKED", response.Error,
            StringComparison.Ordinal);
        var log = Path.Combine(root.Path, "Logs", "app.jsonl");
        Assert.True(File.Exists(log));
        foreach (var line in File.ReadLines(log))
        {
            using var record = JsonDocument.Parse(line);
            Assert.Equal("bridge", record.RootElement.GetProperty("actionId").GetString());
            Assert.Equal("REJECTED", record.RootElement.GetProperty("status").GetString());
        }
    }
}
