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
    public void ResultPath_IsConfinedToAppDataDirectory()
    {
        var token = Guid.NewGuid();
        var directory = Path.GetFullPath(
            ElevatedActionProtocol.GetResultDirectory());
        var path = Path.GetFullPath(
            ElevatedActionProtocol.GetResultPath(token));

        Assert.Equal(directory, Path.GetDirectoryName(path));
        Assert.Equal(token.ToString("N") + ".json", Path.GetFileName(path));
    }
}
