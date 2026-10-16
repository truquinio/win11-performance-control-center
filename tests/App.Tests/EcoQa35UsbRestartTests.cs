using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class EcoQa35UsbRestartTests
{
    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task UsbRestart_EvaluationUsesSyntheticDeviceAndNeverRunsPnPUtil()
    {
        var service = new UsbDiagnosticsService(evaluationMode: true);
        var inventory = await service.AnalyzeAsync();
        var candidate = Assert.Single(
            inventory.Devices,
            item => item.RestartEligible);

        Assert.Equal(2, inventory.DeviceCount);
        Assert.Equal(1, inventory.ProblemCount);
        Assert.Equal(1, inventory.RestartEligibleCount);
        Assert.Equal(@"USB\VID_1234&PID_5678\DEMO", candidate.DeviceInstanceId);

        var result = await service.RestartAsync(candidate.DeviceInstanceId);

        Assert.True(result.Success, result.Detail);
        Assert.Equal("EVALUATION", result.Status);
        Assert.Equal(43u, result.BeforeProblemCode);
        Assert.Equal(0u, result.AfterProblemCode);
        Assert.False(result.RebootRequired);
        Assert.Contains("sintético", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task UsbRestart_RefusesProtectedAndUnknownSyntheticDevices()
    {
        var service = new UsbDiagnosticsService(evaluationMode: true);

        var protectedError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RestartAsync(@"USB\ROOT_HUB30\DEMO"));
        Assert.Contains("protegido", protectedError.Message, StringComparison.OrdinalIgnoreCase);

        var unknownError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RestartAsync(@"USB\VID_9999&PID_9999\UNKNOWN"));
        Assert.Contains("no existe", unknownError.Message, StringComparison.OrdinalIgnoreCase);
    }
}
