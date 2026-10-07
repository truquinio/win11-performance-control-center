using System.Text.Json;
using Xunit.Abstractions;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class RealWorldStorageEvalTests
{
    private readonly ITestOutputHelper output;

    public RealWorldStorageEvalTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    public static IEnumerable<object[]> ScenarioFiles()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "evals",
            "regressions");
        Assert.True(
            Directory.Exists(directory),
            $"Real-world eval directory not found: {directory}");

        return Directory
            .EnumerateFiles(directory, "*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new object[] { path });
    }

    [Theory]
    [MemberData(nameof(ScenarioFiles))]
    [Trait("Layer", "RealWorldEval")]
    public async Task StorageRegression_ReachesExpectedOutcome(
        string scenarioPath)
    {
        var scenario = JsonSerializer.Deserialize<StorageEvalScenario>(
            await File.ReadAllTextAsync(scenarioPath),
            JsonOptions) ?? throw new InvalidOperationException(
                $"Invalid scenario: {scenarioPath}");

        using var fixture = new TestDataRoot();
        MaterializeFixture(fixture.Path, scenario);

        var targetRoot = ResolveInside(
            fixture.Path,
            scenario.TargetRelativePath);
        var service = new StorageAnalysisService(
            [
                new StorageAnalysisService.CacheTarget(
                    "eval." + scenario.Id,
                    scenario.Id,
                    targetRoot,
                    "SAFE",
                    scenario.ProcessName)
            ],
            _ => scenario.ProcessRunning,
            fixture.Path);

        var preview = await service.AnalyzeSafeAsync();
        var result = await service.CleanupSafeAsync();

        Assert.Equal(scenario.ExpectedDeletedFiles, result.DeletedFiles);
        Assert.Equal(scenario.ExpectedFailedFiles, result.FailedFiles);
        if (scenario.ExpectedSkippedCategories is int expectedSkipped)
        {
            Assert.Equal(
                expectedSkipped,
                result.SkippedCategories.Count);
        }

        foreach (var file in scenario.Files)
        {
            var path = ResolveInside(fixture.Path, file.Path);
            if (string.Equals(
                file.Expect,
                "deleted",
                StringComparison.OrdinalIgnoreCase))
            {
                Assert.False(
                    File.Exists(path),
                    $"{scenario.Id}: expected deletion: {file.Path}");
            }
            else
            {
                Assert.True(
                    File.Exists(path),
                    $"{scenario.Id}: expected preservation: {file.Path}");
            }
        }

        if (scenario.RunTwice)
        {
            var second = await service.CleanupSafeAsync();
            Assert.Equal(0, second.DeletedFiles);
            Assert.Equal(0, second.DeletedBytes);
            Assert.Equal(0, second.FailedFiles);
        }

        output.WriteLine(
            "REAL_WORLD_EVAL_OK id={0} previewBytes={1} deletedFiles={2} failed={3} skipped={4}",
            scenario.Id,
            preview.EstimatedBytes,
            result.DeletedFiles,
            result.FailedFiles,
            result.SkippedCategories.Count);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task ReliabilityLab_RuntimeGateReportsHealthy()
    {
        using var fixture = new TestDataRoot();
        using var bridge = fixture.CreateBridge();
        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "reliability-lab",
            method = "actions.run",
            payload = new { id = "lab.reliability.status" }
        });

        var response = await bridge.HandleAsync(request);
        Assert.True(response.Ok, response.Error);
        var result = Assert.IsType<Win11PerformanceControlCenter.App.Models.ActionResult>(
            response.Result);
        Assert.True(result.Success, result.Message);
        Assert.Contains(
            "Reliability Lab OK",
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public async Task LockedFile_IsPreservedAndReported_NotForced()
    {
        using var fixture = new TestDataRoot();
        var targetRoot = Path.Combine(fixture.Path, "locked-cache");
        Directory.CreateDirectory(targetRoot);
        var file = Path.Combine(targetRoot, "locked.bin");
        await File.WriteAllBytesAsync(file, new byte[256]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30));

        var service = new StorageAnalysisService(
            [
                new StorageAnalysisService.CacheTarget(
                    "eval.locked-file",
                    "locked-file",
                    targetRoot)
            ],
            _ => false,
            fixture.Path);

        using (new FileStream(
            file,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            var result = await service.CleanupSafeAsync();
            Assert.Equal(0, result.DeletedFiles);
            Assert.Equal(1, result.FailedFiles);
            Assert.True(File.Exists(file));
        }

        var retry = await service.CleanupSafeAsync();
        Assert.Equal(1, retry.DeletedFiles);
        Assert.Equal(0, retry.FailedFiles);
        Assert.False(File.Exists(file));
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public void ProductionPolicy_FailsClosedForUnknownOrBroadTargets()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var broad = new StorageAnalysisService.CacheTarget(
            "future.broad-cleanup",
            "unsafe broad target",
            appData);

        Assert.False(
            StorageAnalysisService.IsProductionApprovedTarget(
                broad,
                appData));

        var fakeClaude = Path.Combine(
            appData,
            "Claude",
            "Claude Extensions");
        Assert.True(
            StorageAnalysisService.IsProtectedPath(fakeClaude));
    }

    private static void MaterializeFixture(
        string root,
        StorageEvalScenario scenario)
    {
        foreach (var file in scenario.Files)
        {
            var path = ResolveInside(root, file.Path);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);
            File.WriteAllBytes(
                path,
                new byte[file.SizeBytes]);
            File.SetLastWriteTimeUtc(
                path,
                DateTime.UtcNow.AddDays(-file.AgeDays));
        }
    }

    private static string ResolveInside(
        string root,
        string relative)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(
            Path.Combine(root, relative));
        Assert.True(
            resolved.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                resolved,
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase),
            $"Eval path escaped fixture root: {relative}");
        return resolved;
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private sealed record StorageEvalScenario(
        string Id,
        string Kind,
        string Description,
        string TargetRelativePath,
        string? ProcessName,
        bool ProcessRunning,
        int ExpectedDeletedFiles,
        int ExpectedFailedFiles,
        int? ExpectedSkippedCategories,
        bool RunTwice,
        IReadOnlyList<StorageEvalFile> Files);

    private sealed record StorageEvalFile(
        string Path,
        int AgeDays,
        int SizeBytes,
        string Expect);
}
