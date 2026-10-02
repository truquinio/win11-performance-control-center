using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class ProcessSelectionGuardTests
{
    [Fact]
    public void MemoryTrimWrite_RequiresPriorPreview()
    {
        var service = new ProcessAnalysisService();

        var error = Assert.Throws<InvalidOperationException>(
            () => service.ValidateMemoryTrimSelection([12345]));

        Assert.Contains("análisis previo", error.Message);
    }

    [Fact]
    public void EcoQosWrite_RequiresPriorPreview()
    {
        var service = new ProcessAnalysisService();

        var error = Assert.Throws<InvalidOperationException>(
            () => service.ValidateEcoQosSelection([12345]));

        Assert.Contains("análisis previo", error.Message);
    }

    [Fact]
    public void Selection_RejectsPidOutsideLatestPreview()
    {
        var service = new ProcessAnalysisService();
        _ = service.AnalyzeMemoryTrimCandidates();

        var impossiblePid = int.MaxValue;
        var error = Assert.Throws<InvalidOperationException>(
            () => service.ValidateMemoryTrimSelection([impossiblePid]));

        Assert.Contains("no pertenece", error.Message);
    }
}
