using System.IO.Pipes;
using Win11PerformanceControlCenter.App.Core;

namespace App.Tests;

public sealed class ElevatedActionProtocolTests
{
    [Fact]
    public void TryParse_AcceptsExactTypedArguments()
    {
        var token = Guid.NewGuid();
        var args = new[]
        {
            ElevatedActionProtocol.ActionFlag,
            "system.integrity.check",
            ElevatedActionProtocol.TokenFlag,
            token.ToString("N")
        };

        var accepted = ElevatedActionProtocol.TryParse(
            args,
            out var actionId,
            out var parsedToken);

        Assert.True(accepted);
        Assert.Equal("system.integrity.check", actionId);
        Assert.Equal(token, parsedToken);
    }

    [Theory]
    [InlineData("--elevated-action", "system.integrity.check")]
    [InlineData("--elevated-action", "system.integrity.check", "--result-token", "not-a-guid")]
    [InlineData("--other", "system.integrity.check", "--result-token", "00000000000000000000000000000001")]
    public void TryParse_RejectsMalformedArguments(params string[] args)
    {
        Assert.False(ElevatedActionProtocol.TryParse(
            args,
            out _,
            out _));
    }

    [Fact]
    public void PipeName_IsTokenDerivedAndPathFree()
    {
        var token = Guid.NewGuid();
        var name = ElevatedActionProtocol.GetPipeName(token);

        Assert.Equal(
            "WPCC-Elevated-" + token.ToString("N"),
            name);
        Assert.DoesNotContain("..", name);
        Assert.DoesNotContain("\\", name);
        Assert.DoesNotContain("/", name);
    }

    [Fact]
    public void PipeName_RejectsEmptyToken()
    {
        Assert.Throws<ArgumentException>(
            () => ElevatedActionProtocol.GetPipeName(Guid.Empty));
    }

    [Fact]
    public async Task PipeTransport_ExchangesDataForCurrentUser()
    {
        var token = Guid.NewGuid();
        var pipeName = ElevatedActionProtocol.GetPipeName(token);

        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        var acceptTask = server.WaitForConnectionAsync();
        await client.ConnectAsync(2000);
        await acceptTask;

        // The pipe has no buffer: a write only completes once the server is
        // reading, exactly as the elevated client does in production.
        var payload = new byte[] { (byte)'o', (byte)'k' };
        var buffer = new byte[2];
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var readTask = server.ReadAsync(buffer, timeout.Token).AsTask();
        await client.WriteAsync(payload, timeout.Token);
        var read = await readTask;

        Assert.Equal(2, read);
        Assert.Equal(payload, buffer);
    }

}
