using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class AndroidEmulatorSafetyTests
{
    [Theory]
    [InlineData("adb")]
    [InlineData("emulator")]
    [InlineData("qemu-system-x86_64")]
    [InlineData("qemu-system-x86_64-headless")]
    public void ProcessSafetyPolicy_ProtectsAndroidDevelopmentProcesses(
        string processName)
    {
        var candidate = new ProcessCandidate(
            4321,
            processName,
            512L * 1024 * 1024,
            false);

        Assert.False(ProcessSafetyPolicy.IsEligible(
            candidate,
            requireBackground: true));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task CrashIntelligence_RecognizesAndroidQemuFailure()
    {
        var events = new[]
        {
            new ReliabilityEventDto(
                DateTimeOffset.UtcNow,
                1000,
                "Application Error",
                EvidenceClassification.HECHO,
                "qemu-system-x86_64-headless.exe stopped unexpectedly")
        };

        var service = new CrashIntelligenceService(
            _ => Task.FromResult<IReadOnlyList<ReliabilityEventDto>>(
                events));

        var report = await service.AnalyzeAsync();

        var insight = Assert.Single(
            report.Insights,
            item => item.Title == "Android Emulator / QEMU crash");
        Assert.Equal("memory.analyze", insight.RecommendedActionId);
        Assert.Equal("MEDIUM", insight.Severity);
        Assert.Contains(
            "no demuestra una causa raíz",
            insight.Rationale,
            StringComparison.OrdinalIgnoreCase);
    }
}
