using System.Security.Cryptography;
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

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task CleanupWithPreview_ConfirmsAndRecyclesOnlySyntheticFile()
    {
        using var fixture = new TestDataRoot();
        using var bridge = fixture.CreateBridge();
        var storageFixture = System.IO.Path.Combine(fixture.Path, "StorageFixture");
        var source = System.IO.Path.Combine(storageFixture, "synthetic-cache.tmp");
        Directory.CreateDirectory(storageFixture);
        var content = System.Text.Encoding.UTF8.GetBytes("synthetic cleanup fixture");
        await File.WriteAllBytesAsync(source, content);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-8));
        var expectedHash = Convert.ToHexString(SHA256.HashData(content));

        var preview = await bridge.HandleAsync(Request("cleanup-preview", "disk.cleanup.safe"));
        Assert.True(preview.Ok, preview.Error);
        var cleanup = await bridge.HandleAsync(Request(
            "cleanup-execute",
            "disk.cleanup.execute",
            new { confirmed = true }));
        Assert.True(cleanup.Ok, cleanup.Error);
        Assert.False(File.Exists(source));
        Assert.Equal(1, ReadDeletedFiles(cleanup.Result));

        var recycleFixture = System.IO.Path.Combine(fixture.Path, "StorageRecycle");
        var recycled = Assert.Single(Directory.GetFiles(recycleFixture));
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(recycled))));

        var secondExecution = await bridge.HandleAsync(Request(
            "cleanup-single-use",
            "disk.cleanup.execute",
            new { confirmed = true }));
        Assert.False(secondExecution.Ok);
        Assert.Contains("previsual", secondExecution.Error!, StringComparison.OrdinalIgnoreCase);
        File.Move(recycled, source);
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
    }

    private static string Request(string requestId, string actionId, object? parameters = null) =>
        JsonSerializer.Serialize(new
        {
            type = "request",
            requestId,
            method = "actions.run",
            payload = new { id = actionId, parameters = parameters ?? new { } }
        });

    private static int ReadDeletedFiles(object? result)
    {
        var json = JsonSerializer.SerializeToElement(result);
        return json.GetProperty("Data").GetProperty("DeletedFiles").GetInt32();
    }
}
