using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class ReliabilityEvidenceClassifierTests
{
    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 41, EvidenceClassification.INDICIO)]
    [InlineData("EventLog", 6008, EvidenceClassification.HECHO)]
    [InlineData("User32", 1074, EvidenceClassification.HECHO)]
    [InlineData("Microsoft-Windows-Kernel-General", 12, EvidenceClassification.HECHO)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 18, EvidenceClassification.HECHO)]
    [InlineData("Application Error", 1000, EvidenceClassification.HECHO)]
    [InlineData("Application Hang", 1002, EvidenceClassification.HECHO)]
    public void KnownEvidence_IsClassified(
        string provider,
        int eventId,
        EvidenceClassification expected)
    {
        var accepted = ReliabilityEvidenceClassifier.TryClassify(
            provider,
            eventId,
            out var classification);

        Assert.True(accepted);
        Assert.Equal(expected, classification);
    }

    [Fact]
    public void SameEventId_FromUnrelatedProvider_IsRejected()
    {
        var accepted = ReliabilityEvidenceClassifier.TryClassify(
            "Microsoft-Windows-UserModePowerService",
            12,
            out var classification);

        Assert.False(accepted);
        Assert.Equal(
            EvidenceClassification.NO_DETERMINADO,
            classification);
    }

    [Fact]
    public void UnknownEvidence_IsRejected()
    {
        var accepted = ReliabilityEvidenceClassifier.TryClassify(
            "Unknown.Provider",
            9999,
            out _);

        Assert.False(accepted);
    }
}

// Integration smoke test: uses only local read-only Windows Event Log access.
public sealed class ReliabilityServiceSmokeTests
{
    [Fact]
    public async Task RecentEvidence_ContainsOnlyAcceptedClassifications()
    {
        var service = new ReliabilityService();

        var events = await service.GetRecentAsync(100);

        Assert.All(events, item =>
        {
            Assert.NotEqual(
                EvidenceClassification.NO_DETERMINADO,
                item.Classification);
            Assert.True(ReliabilityEvidenceClassifier.TryClassify(
                item.Provider,
                item.Id,
                out var classification));
            Assert.Equal(classification, item.Classification);
        });
    }
}
