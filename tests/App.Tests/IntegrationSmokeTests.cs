using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Xunit.Abstractions;

namespace App.Tests;

public sealed class IntegrationSmokeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan PerActionBudget = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task NonAdminReadAndPreviewActions_CompleteThroughTypedBridge()
    {
        var catalog = new ActionCatalog();
        var actions = catalog.All
            .Where(action =>
                !action.RequiresAdmin &&
                action.Mode is ActionMode.READ or ActionMode.DRY_RUN &&
                !(action.Parameters?.Any(parameter =>
                    parameter.Required) ?? false))
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

            output.WriteLine("INTEGRATION_SMOKE_START {0}", action.Id);
            BridgeResponse response;
            try
            {
                response = await bridge.HandleAsync(request)
                    .WaitAsync(PerActionBudget);
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{action.Id} excedió el presupuesto de {PerActionBudget.TotalSeconds:F0}s.");
            }
            output.WriteLine("INTEGRATION_SMOKE_OK {0}", action.Id);

            Assert.True(
                response.Ok,
                $"{action.Id} falló en smoke test: {response.Error}");
            Assert.IsType<ActionResult>(response.Result);
        }
    }
}
