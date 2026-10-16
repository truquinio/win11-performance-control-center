using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

/// <summary>
/// Eco-QA services use built-in evaluation fixtures, never real SCM,
/// Registry Run entries, schtasks, or Windows protected dependencies.
/// </summary>
public sealed class EcoQa35SyntheticServiceTests
{
    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task ServiceMode_EvaluationRequiresSavedBaselineForRollback()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "services.json");
        var service = new ServiceStartupRemediationService(
            state, evaluationMode: true);

        var absent = await service.RestoreAsync("DemoVendorSvc");
        Assert.Equal("NO_SNAPSHOT", absent.Status);
        Assert.False(absent.RestoreAvailable);

        var applied = await service.ChangeModeAsync("DemoVendorSvc", "Manual");
        Assert.True(applied.Success);
        Assert.True(applied.RestoreAvailable);
        Assert.True(File.Exists(state));

        var restored = await service.RestoreAsync("DemoVendorSvc");
        Assert.True(restored.Success);
        Assert.Equal("RESTORED", restored.Status);
        Assert.False(restored.RestoreAvailable);
        var duplicate = await service.RestoreAsync("DemoVendorSvc");
        Assert.Equal("NO_SNAPSHOT", duplicate.Status);
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task StartupEntry_DisableRestoreUsesOnlyEvaluationRegistry()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "startup.json");
        var service = new StartupEntryRemediationService(
            state, evaluationMode: true);
        var preview = await service.PreviewAsync();
        var candidate = Assert.Single(preview.Entries, item =>
            item.Name == "DemoVendor" && !item.Protected);

        var disabled = await service.DisableAsync(candidate.EntryId);
        Assert.True(disabled.Success);
        Assert.Equal("DISABLED", disabled.Status);
        Assert.True(File.Exists(state));

        var restored = await service.RestoreAsync(candidate.EntryId);
        Assert.True(restored.Success);
        Assert.Equal("RESTORED", restored.Status);
        var duplicate = await service.RestoreAsync(candidate.EntryId);
        Assert.Equal("NO_SNAPSHOT", duplicate.Status);
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task ScheduledTask_DisableRestoreUsesOnlySyntheticTask()
    {
        using var root = new TestDataRoot();
        var state = Path.Combine(root.Path, "tasks.json");
        var service = new ScheduledTaskRemediationService(
            state, evaluationMode: true);
        var preview = await service.PreviewAsync();
        var candidate = Assert.Single(preview.Tasks, item =>
            item.TaskName == "Updater" && !item.Protected);

        var disabled = await service.DisableAsync(candidate.EntryId);
        Assert.True(disabled.Success);
        Assert.Equal("DISABLED", disabled.Status);
        Assert.True(File.Exists(state));

        var restored = await service.RestoreAsync(candidate.EntryId);
        Assert.True(restored.Success);
        Assert.Equal("RESTORED", restored.Status);
        var duplicate = await service.RestoreAsync(candidate.EntryId);
        Assert.Equal("NO_SNAPSHOT", duplicate.Status);
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task PowerPlanActions_RoundTripThroughAnInjectedSyntheticCommandRunner()
    {
        using var root = new TestDataRoot();
        const string original = "11111111-1111-1111-1111-111111111111";
        const string balanced = "22222222-2222-2222-2222-222222222222";
        const string performance = "33333333-3333-3333-3333-333333333333";
        var active = original;
        var commands = new List<string>();

        Func<IReadOnlyList<string>, TimeSpan,
            Task<(int ExitCode, string Output)>> runner =
            async (arguments, _) =>
            {
                commands.Add(string.Join(" ", arguments));
                await Task.Yield();
                if (arguments.SequenceEqual(["/getactivescheme"]))
                    return (0, "Power Scheme GUID: " + active);
                if (arguments.Count == 2 &&
                    arguments[0] == "/setactive")
                {
                    active = arguments[1] switch
                    {
                        "SCHEME_BALANCED" => balanced,
                        "SCHEME_MIN" => performance,
                        original => original,
                        _ => active
                    };
                    return (0, "synthetic success");
                }

                return (1, "unexpected synthetic command");
            };

        var state = Path.Combine(root.Path, "power-plan.json");
        var service = new PowerPlanTuningService(state, runner);

        var setBalanced = await service.SetBalancedAsync();
        Assert.True(setBalanced.Success);
        Assert.Equal("BALANCED", setBalanced.Status);
        Assert.Equal(original, setBalanced.BeforeGuid);
        Assert.Equal(balanced, setBalanced.AfterGuid);
        Assert.True(setBalanced.RestoreAvailable);
        Assert.True(File.Exists(state));

        var repeat = await service.SetBalancedAsync();
        Assert.True(repeat.Success);
        Assert.Equal("ALREADY_ACTIVE", repeat.Status);

        var restoreBalanced = await service.RestoreAsync();
        Assert.True(restoreBalanced.Success);
        Assert.Equal("RESTORED", restoreBalanced.Status);
        Assert.Equal(original, active);
        Assert.False(File.Exists(state));

        var setPerformance = await service.SetPerformanceAsync();
        Assert.True(setPerformance.Success);
        Assert.Equal("HIGH_PERFORMANCE", setPerformance.Status);
        Assert.Equal(performance, setPerformance.AfterGuid);

        var restorePerformance = await service.RestoreAsync();
        Assert.True(restorePerformance.Success);
        Assert.Equal("RESTORED", restorePerformance.Status);
        Assert.Equal(original, active);
        Assert.False(File.Exists(state));
        Assert.Contains("/setactive SCHEME_BALANCED", commands);
        Assert.Contains("/setactive SCHEME_MIN", commands);
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task PageFileActions_RoundTripThroughAnInjectedSyntheticBackend()
    {
        using var root = new TestDataRoot();
        var automatic = true;
        string[]? pagingFiles = ["C:\\pagefile.sys 16384 16384"];
        var originalPagingFiles = pagingFiles.ToArray();

        PageFileTuningSnapshot Capture() => new(
            automatic, pagingFiles?.ToArray(), DateTimeOffset.UtcNow);
        var backend = new PageFileTuningBackend(
            Capture,
            value => { automatic = value; return true; },
            values => { pagingFiles = values?.ToArray(); return true; },
            drive => drive == "D");
        var state = Path.Combine(root.Path, "pagefile.json");
        var service = new PageFileTuningService(state, backend);

        var applied = await service.ApplyCappedProfileAsync();
        Assert.True(applied.Success, applied.Status);
        Assert.False(automatic);
        Assert.Contains("D:\\pagefile.sys 4096 8192", pagingFiles!);
        Assert.True(File.Exists(state));

        var repeated = await service.ApplyCappedProfileAsync();
        Assert.True(repeated.Success);
        Assert.Equal("ALREADY_CAPPED", repeated.Status);
        Assert.True(File.Exists(state));

        var restored = await service.RestoreAsync();
        Assert.True(restored.Success, restored.Status);
        Assert.True(automatic);
        Assert.Equal(originalPagingFiles, pagingFiles);
        Assert.False(File.Exists(state));
    }

    [Fact]
    [Trait("Layer", "EcoQa35Synthetic")]
    public async Task PageFileActions_WriteFailureCanRecoverFromTheSavedSyntheticSnapshot()
    {
        using var root = new TestDataRoot();
        var automatic = true;
        string[]? pagingFiles = ["C:\\pagefile.sys 16384 16384"];
        var originalPagingFiles = pagingFiles.ToArray();
        var allowWrites = false;

        PageFileTuningSnapshot Capture() => new(
            automatic, pagingFiles?.ToArray(), DateTimeOffset.UtcNow);
        var backend = new PageFileTuningBackend(
            Capture,
            value => { automatic = value; return true; },
            values =>
            {
                if (!allowWrites)
                    return false;
                pagingFiles = values?.ToArray();
                return true;
            },
            drive => drive == "D");
        var state = Path.Combine(root.Path, "pagefile-failure.json");
        var service = new PageFileTuningService(state, backend);

        var failed = await service.ApplyCappedProfileAsync();
        Assert.False(failed.Success);
        Assert.Equal("PAGINGFILES_WRITE_FAILED", failed.Status);
        Assert.True(failed.RestoreAvailable);
        Assert.True(File.Exists(state));

        allowWrites = true;
        var restored = await service.RestoreAsync();
        Assert.True(restored.Success, restored.Status);
        Assert.True(automatic);
        Assert.Equal(originalPagingFiles, pagingFiles);
        Assert.False(File.Exists(state));
    }
}
