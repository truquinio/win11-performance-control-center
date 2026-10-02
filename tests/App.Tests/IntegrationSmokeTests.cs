using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace App.Tests;

public sealed class IntegrationSmokeTests
{
    [Fact]
    public async Task NonAdminReadAndPreviewActions_CompleteThroughTypedBridge()
    {
        var catalog = new ActionCatalog();
        var actions = catalog.All
            .Where(action =>
                !action.RequiresAdmin &&
                action.Mode is ActionMode.READ or ActionMode.DRY_RUN)
            .ToArray();

        Assert.NotEmpty(actions);

        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();

        foreach (var action in actions)
        {
            var request = JsonSerializer.Serialize(
                new
                {
                    type = "request",
                    requestId = "smoke-" + action.Id,
                    method = "actions.run",
                    payload = new { id = action.Id }
                });

            var response = await bridge.HandleAsync(request);

            Assert.True(
                response.Ok,
                $"{action.Id} falló en smoke test: {response.Error}");
            Assert.IsType<ActionResult>(response.Result);
        }
    }
}
