using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;

namespace App.Tests;

public sealed class StoragePreviewInterlockTests
{
    [Fact]
    public async Task CleanupWithoutPreview_IsRejectedEvenWithConfirmation()
    {
        using var fixture = new TestDataRoot();
        using var bridge = fixture.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "cleanup-no-preview",
            method = "actions.run",
            payload = new { id = "disk.cleanup.execute", parameters = new { confirmed = true } }
        });
        var response = await bridge.HandleAsync(request);
        Assert.False(response.Ok);
        Assert.Contains("previsual", response.Error!, StringComparison.OrdinalIgnoreCase);
    }
}
