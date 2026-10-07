using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class ServiceStartupRemediationServiceTests
{
    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task Preview_SeparatesProtectedAndReviewableServices()
    {
        using var root = new TestDataRoot();
        var service = new ServiceStartupRemediationService(
            Path.Combine(root.Path, "services.json"),
            evaluationMode: true);

        var preview = await service.PreviewAsync();

        Assert.Equal(2, preview.AutomaticCount);
        Assert.Equal(1, preview.EligibleCount);
        Assert.Equal(1, preview.ProtectedCount);
        Assert.Contains(
            preview.Services,
            item => item.ServiceName == "DemoVendorSvc" && !item.Protected);
        Assert.Contains(
            preview.Services,
            item => item.ServiceName == "RpcSs" && item.Protected);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task ProtectedService_CannotBeDisabledEvenWithDirectCall()
    {
        using var root = new TestDataRoot();
        var service = new ServiceStartupRemediationService(
            Path.Combine(root.Path, "services.json"),
            evaluationMode: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ChangeModeAsync("RpcSs", "Disabled"));

        Assert.Contains("protegido", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewableService_CanEnterManualModeInEvaluation()
    {
        using var root = new TestDataRoot();
        var service = new ServiceStartupRemediationService(
            Path.Combine(root.Path, "services.json"),
            evaluationMode: true);

        var result = await service.ChangeModeAsync(
            "DemoVendorSvc",
            "Manual");

        Assert.True(result.Success);
        Assert.Equal("Manual", result.AfterMode);
        Assert.True(result.RestoreAvailable);
    }
}
