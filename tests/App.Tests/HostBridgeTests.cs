using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;

namespace App.Tests;

public sealed class HostBridgeTests
{
    [Fact]
    public async Task HandleAsync_RejectsUnknownMethod()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "test-unknown-method",
            method = "system.unsupported",
            payload = new { }
        });

        var response = await bridge.HandleAsync(request);

        Assert.False(response.Ok);
        Assert.Equal("test-unknown-method", response.RequestId);
        Assert.Contains("no permitido", response.Error);
    }

    [Fact]
    public async Task HandleAsync_RejectsNonCatalogActionId()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "test-action-id",
            method = "actions.run",
            payload = new { id = "disk.scan.extra" }
        });

        var response = await bridge.HandleAsync(request);

        Assert.False(response.Ok);
        Assert.Contains("no permitida", response.Error);
    }

    [Fact]
    public async Task HandleAsync_AllowsCatalogRead()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "test-catalog",
            method = "actions.catalog",
            payload = new { }
        });

        var response = await bridge.HandleAsync(request);

        Assert.True(response.Ok);
        Assert.NotNull(response.Result);
        Assert.Null(response.Error);
    }

    [Fact]
    public async Task CatalogContract_SerializesEnumsAsStrings()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "test-enums",
            method = "actions.catalog",
            payload = new { }
        });

        var response = await bridge.HandleAsync(request);
        var json = JsonSerializer.Serialize(
            response,
            HostBridge.JsonOptions);

        Assert.Contains("\"risk\":\"SAFE\"", json);
        Assert.Contains("\"mode\":\"DRY_RUN\"", json);
    }

    [Fact]
    public async Task WriteAction_RejectsMissingTypedParameters()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "test-write-missing",
            method = "actions.run",
            payload = new { id = "memory.trim" }
        });

        var response = await bridge.HandleAsync(request);

        Assert.False(response.Ok);
        Assert.Contains("parámetros", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAction_RejectsUnknownParameter()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "test-write-extra",
            method = "actions.run",
            payload = new
            {
                id = "cpu.ecoqos.apply",
                parameters = new
                {
                    processIds = new[] { 1234 },
                    confirmed = true,
                    command = "not-allowed"
                }
            }
        });

        var response = await bridge.HandleAsync(request);

        Assert.False(response.Ok);
        Assert.Contains("no permitido", response.Error);
    }

    [Fact]
    public async Task WriteAction_RejectsFalseConfirmationBeforeExecution()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "test-write-confirm",
            method = "actions.run",
            payload = new
            {
                id = "memory.trim",
                parameters = new
                {
                    processIds = new[] { 1234 },
                    confirmed = false
                }
            }
        });

        var response = await bridge.HandleAsync(request);

        Assert.False(response.Ok);
        Assert.Contains("confirmación", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAction_RejectsMoreThanTwentyProcessIds()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "test-write-limit",
            method = "actions.run",
            payload = new
            {
                id = "memory.trim",
                parameters = new
                {
                    processIds = Enumerable.Range(1000, 21).ToArray(),
                    confirmed = true
                }
            }
        });

        var response = await bridge.HandleAsync(request);

        Assert.False(response.Ok);
        Assert.Contains("Tipo inválido", response.Error);
    }

    [Fact]
    public async Task MemoryTrimPreview_RemainsDryRun()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "test-memory-preview",
            method = "actions.run",
            payload = new { id = "memory.trim.preview" }
        });

        var response = await bridge.HandleAsync(request);

        Assert.True(response.Ok);
        var result = Assert.IsType<
            Win11PerformanceControlCenter.App.Models.ActionResult>(
            response.Result);
        Assert.True(result.Success);
        Assert.True(result.DryRun);
    }
}
