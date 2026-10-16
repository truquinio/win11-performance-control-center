using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;

namespace App.Tests;

public sealed class RecoveryCancellationContractTests
{
    [Fact]
    public async Task CancelledWriteWithUnknownPartialEffectsRemainsVisibleThroughRecoveryIpc()
    {
        // Only a synthetic journal in an isolated TestDataRoot. No Windows WRITE.
        using var root = new TestDataRoot();
        var logPath = Path.Combine(root.Path, "Logs", "app.jsonl");
        using (var logger = new AppLogger(logPath))
        {
            await logger.WriteAsync("test.write.cancelled", "STARTED",
                new { mode = "WRITE" }, "cancelled-1");
            await logger.WriteAsync("test.write.cancelled", "CANCELLED",
                new { partialEffectsVerified = false }, "cancelled-1");
            await logger.WriteAsync("test.read.cancelled", "STARTED",
                new { mode = "READ" }, "cancelled-read");
            await logger.WriteAsync("test.read.cancelled", "CANCELLED",
                operationId: "cancelled-read");
        }

        using var bridge = root.CreateBridge();
        foreach (var action in new[] { "backup.status", "backup.rollback.center" })
        {
            var request = JsonSerializer.Serialize(new
            {
                type = "request",
                requestId = action,
                method = "actions.run",
                payload = new { id = action }
            });
            var responseJson = await bridge.HandleSerializedAsync(request);
            using var response = JsonDocument.Parse(responseJson);
            var rootNode = response.RootElement;
            Assert.True(rootNode.GetProperty("ok").GetBoolean());
            var result = rootNode.GetProperty("result");
            Assert.True(result.GetProperty("success").GetBoolean());
            var data = result.GetProperty("data");

            Assert.True(data.TryGetProperty(
                "potentiallyChangedCancelledOperations", out var cancelled),
                "Cancelled WRITE must not disappear from recovery/rollback IPC.");
            var review = Assert.Single(cancelled.EnumerateArray());
            Assert.Equal("test.write.cancelled",
                review.GetProperty("actionId").GetString());
            Assert.Equal("CANCELLED",
                review.GetProperty("lastStatus").GetString());
            Assert.False(review.GetProperty("incomplete").GetBoolean());
        }
    }
}
