using System.Diagnostics;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class FaultInjectionTests
{
    private static EcoQosOriginalState State(int processId) =>
        new(
            processId,
            "fault-injection",
            DateTimeOffset.UtcNow.AddMinutes(-3),
            0,
            0,
            DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("[null]")]
    [InlineData("null")]
    [InlineData("[{\"processId\":0,\"name\":\"x\",\"processStartTime\":\"2026-10-01T12:00:00Z\",\"controlMask\":0,\"stateMask\":0,\"capturedAt\":\"2026-10-01T12:00:00Z\"}]")]
    [InlineData("[{\"processId\":77,\"name\":null,\"processStartTime\":\"2026-10-01T12:00:00Z\",\"controlMask\":0,\"stateMask\":0,\"capturedAt\":\"2026-10-01T12:00:00Z\"}]")]
    [InlineData("")]
    public void EcoQosStore_LoadsUnusableStateAsEmpty(string content)
    {
        using var dataRoot = new TestDataRoot();
        var path = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var store = new EcoQosStateStore(path);

        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void EcoQosStore_FailedPersistLeavesNoInMemoryBaseline()
    {
        using var dataRoot = new TestDataRoot();
        var path = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        var store = new EcoQosStateStore(path);

        // A directory squatting on the temp file makes every persist fail.
        Directory.CreateDirectory(path + ".tmp");

        Assert.ThrowsAny<Exception>(() => store.SaveBaseline(State(4101)));
        Assert.False(store.TryGet(4101, out _));

        Directory.Delete(path + ".tmp");

        Assert.True(store.SaveBaseline(State(4101)));
        Assert.True(new EcoQosStateStore(path).TryGet(4101, out _));
    }

    [Fact]
    public void EcoQosStore_FailedRemoveKeepsRollbackSnapshot()
    {
        using var dataRoot = new TestDataRoot();
        var path = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        var store = new EcoQosStateStore(path);
        Assert.True(store.SaveBaseline(State(4102)));

        Directory.CreateDirectory(path + ".tmp");

        Assert.ThrowsAny<Exception>(() => store.Remove(4102));
        Assert.True(store.TryGet(4102, out _));
        Assert.True(new EcoQosStateStore(path).TryGet(4102, out _));
    }

    [Fact]
    public void EcoQosStore_ConcurrentWritersNeverCorruptTheFile()
    {
        using var dataRoot = new TestDataRoot();
        var path = Path.Combine(dataRoot.Path, "State", "ecoqos.json");
        var store = new EcoQosStateStore(path);

        Parallel.For(1, 41, index =>
        {
            store.SaveBaseline(State(5000 + index));
            if (index % 2 == 0)
                store.Remove(5000 + index);
        });

        var reloaded = new EcoQosStateStore(path);
        Assert.Equal(20, reloaded.Snapshot().Count);
        Assert.All(
            reloaded.Snapshot(),
            item => Assert.True(item.ProcessId % 2 == 1));
    }

    [Fact]
    public async Task AppLogger_RotationFailureDoesNotFailTheWrite()
    {
        using var dataRoot = new TestDataRoot();
        var logPath = Path.Combine(dataRoot.Path, "Logs", "app.jsonl");
        using var logger = new AppLogger(logPath) { MaxLogBytes = 1 };
        File.WriteAllText(logPath, string.Empty);

        // A reader without FileShare.Delete blocks the rename, so the
        // rotation that this write triggers cannot complete.
        using (new FileStream(
                   logPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        {
            await logger.WriteAsync("test.rotation", "STARTED");
        }

        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(logPath)!,
            "app-*.jsonl"));
        Assert.Single(File.ReadAllLines(logPath));

        // Once the reader is gone the pending rotation goes through.
        await logger.WriteAsync("test.rotation", "COMPLETED");
        var archive = Assert.Single(Directory.GetFiles(
            Path.GetDirectoryName(logPath)!,
            "app-*.jsonl"));
        Assert.Equal(2, File.ReadAllLines(archive).Length);
    }

    [Fact]
    public async Task AppLogger_RetentionKeepsNewestArchivesByName()
    {
        using var dataRoot = new TestDataRoot();
        var directory = Path.Combine(dataRoot.Path, "Logs");
        var logPath = Path.Combine(directory, "app.jsonl");
        Directory.CreateDirectory(directory);

        // Creation times are deliberately inverted, as NTFS tunnelling can
        // do: only the timestamp in the name says which archive is older.
        for (var index = 1; index <= 7; index++)
        {
            var archive = Path.Combine(
                directory,
                $"app-2020010{index}-000000-000-aaaaaaa{index}.jsonl");
            File.WriteAllText(archive, "{}");
            File.SetCreationTimeUtc(
                archive,
                DateTime.UtcNow.AddDays(-index));
        }

        using var logger = new AppLogger(logPath) { MaxLogBytes = 1 };
        await logger.WriteAsync("test.retention", "STARTED");

        var remaining = Directory.GetFiles(directory, "app-*.jsonl")
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(5, remaining.Length);
        Assert.DoesNotContain(remaining, name => name!.StartsWith("app-20200101", StringComparison.Ordinal));
        Assert.DoesNotContain(remaining, name => name!.StartsWith("app-20200102", StringComparison.Ordinal));
        Assert.DoesNotContain(remaining, name => name!.StartsWith("app-20200103", StringComparison.Ordinal));
        Assert.Contains(remaining, name => name!.StartsWith("app-20200107", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAction_StillRunsWhenAuditLogIsUnwritable()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();
        BlockLog(dataRoot);

        var response = await bridge.HandleAsync(Request(
            "read-without-log",
            new { id = "memory.analyze" }));

        Assert.True(response.Ok, response.Error);
    }

    [Fact]
    public async Task WriteAction_IsRefusedWhenAuditLogIsUnwritable()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();

        var preview = await bridge.HandleAsync(Request(
            "preview",
            new { id = "cpu.ecoqos.analyze" }));
        Assert.True(preview.Ok, preview.Error);

        BlockLog(dataRoot);

        var response = await bridge.HandleAsync(Request(
            "write-without-log",
            new
            {
                id = "cpu.ecoqos.restore",
                parameters = new
                {
                    processIds = new[] { 2147483000 },
                    confirmed = true
                }
            }));

        // The audit record is written before anything else happens, so the
        // request fails outright instead of returning a per-process result.
        Assert.False(response.Ok);
        Assert.Null(response.Result);
    }

    [Fact]
    public async Task Bridge_RejectsOverlappingActionsAndRecovers()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();

        var first = bridge.HandleAsync(Request(
            "overlap-1",
            new { id = "disk.scan" }));
        var second = bridge.HandleAsync(Request(
            "overlap-2",
            new { id = "network.test" }));
        var responses = await Task.WhenAll(first, second);

        Assert.True(responses[0].Ok, responses[0].Error);
        Assert.False(responses[1].Ok);
        Assert.Contains("operación activa", responses[1].Error);

        var third = await bridge.HandleAsync(Request(
            "overlap-3",
            new { id = "network.test" }));
        Assert.True(third.Ok, third.Error);
    }

    [Fact]
    public async Task Bridge_SnapshotStaysAvailableWhileAnActionRuns()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();

        var action = bridge.HandleAsync(Request(
            "busy",
            new { id = "disk.scan" }));
        var snapshot = await bridge.HandleAsync(JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "snapshot-while-busy",
            method = "system.snapshot",
            payload = new { }
        }));
        await action;

        Assert.True(snapshot.Ok, snapshot.Error);
        Assert.IsType<SystemSnapshot>(snapshot.Result);
    }

    [Theory]
    [InlineData("""{"id":"memory.trim","parameters":{"processIds":["a"],"confirmed":true}}""", "Tipo inválido")]
    [InlineData("""{"id":"memory.trim","parameters":{"processIds":[1.5],"confirmed":true}}""", "Tipo inválido")]
    [InlineData("""{"id":"memory.trim","parameters":{"processIds":[],"confirmed":true}}""", "Tipo inválido")]
    [InlineData("""{"id":"memory.trim","parameters":{"processIds":[4294967296],"confirmed":true}}""", "Tipo inválido")]
    [InlineData("""{"id":"memory.trim","parameters":{"processIds":[12],"confirmed":"true"}}""", "Tipo inválido")]
    [InlineData("""{"id":"memory.trim","parameters":[1,2]}""", "objeto tipado")]
    [InlineData("""{"id":42}""", "Action ID")]
    [InlineData("""{"id":null}""", "Action ID")]
    [InlineData("""{"id":"disk.scan","parameters":{"path":"C:\\"}}""", "no acepta parámetros")]
    [InlineData("""[]""", "Action ID")]
    public async Task Bridge_RejectsMalformedPayloadsWithTypedErrors(
        string payload,
        string expected)
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();

        var response = await bridge.HandleAsync(
            """{"type":"request","requestId":"malformed","method":"actions.run","payload":""" +
            payload + "}");

        Assert.False(response.Ok);
        Assert.Equal("malformed", response.RequestId);
        Assert.Contains(expected, response.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("""{"type":"request","requestId":7,"method":"actions.catalog"}""")]
    [InlineData("""{"type":"request","requestId":"x","method":"actions.run"}""")]
    [InlineData("""{"type":"response","requestId":"x","method":"actions.catalog","payload":{}}""")]
    public async Task Bridge_SerializedHandlerAlwaysAnswersWithJson(string message)
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();

        var json = await bridge.HandleSerializedAsync(message);

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            JsonValueKind.String,
            document.RootElement.GetProperty("requestId").ValueKind);
    }

    [Fact]
    public async Task HealthScan_DoesNotExposePartialProductKey()
    {
        using var dataRoot = new TestDataRoot();
        using var bridge = dataRoot.CreateBridge();

        var json = await bridge.HandleSerializedAsync(Request(
            "health",
            new { id = "system.health.scan" }));

        Assert.DoesNotContain("partialProductKey", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Watchdog_ReturnsResultOfWorkThatFinishesInTime()
    {
        var result = await OperationWatchdog.RunAsync(
            Task.FromResult(42),
            TimeSpan.FromSeconds(5),
            "test");

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Watchdog_AbandonsWorkThatNeverAnswers()
    {
        var never = new TaskCompletionSource<int>();
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => OperationWatchdog.RunAsync(
                never.Task,
                TimeSpan.FromMilliseconds(150),
                "wmi-colgado"));

        Assert.Contains("wmi-colgado", error.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));

        // A late failure of the abandoned work must stay contained.
        never.SetException(new InvalidOperationException("late"));
    }

    [Fact]
    public async Task Watchdog_PropagatesFailureOfTheWorkItself()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => OperationWatchdog.RunAsync(
                Task.FromException<int>(new InvalidOperationException("boom")),
                TimeSpan.FromSeconds(5),
                "test"));
    }

    [Fact]
    public void BrowserExtensionHealth_ToleratesUnexpectedJsonShapes()
    {
        using var dataRoot = new TestDataRoot();
        const string extensionId = "abcdefghijklmnopabcdefghijklmnop";
        var defaultProfile = Path.Combine(dataRoot.Path, "Default");
        var secondProfile = Path.Combine(dataRoot.Path, "Profile 1");
        var thirdProfile = Path.Combine(dataRoot.Path, "Profile 2");
        var version = Path.Combine(defaultProfile, "Extensions", extensionId, "1.0.0_0");
        Directory.CreateDirectory(version);
        Directory.CreateDirectory(secondProfile);
        Directory.CreateDirectory(thirdProfile);

        File.WriteAllText(Path.Combine(defaultProfile, "Preferences"), "[]");
        File.WriteAllText(Path.Combine(defaultProfile, "Secure Preferences"), "\"text\"");
        File.WriteAllText(Path.Combine(version, "manifest.json"), "[1,2,3]");
        File.WriteAllText(Path.Combine(secondProfile, "Preferences"), """{"extensions":7}""");
        File.WriteAllText(
            Path.Combine(thirdProfile, "Preferences"),
            """{"extensions":{"ui":[],"settings":{"abcdefghijklmnopabcdefghijklmnop":{"manifest":"x","path":5,"location":"4"}}}}""");

        var health = new BrowserExtensionHealthService(
            dataRoot.Path,
            new HashSet<string>()).AnalyzeEdge();

        Assert.Equal(3, health.ProfilesScanned);
        Assert.Null(health.DeveloperMode);
        Assert.Contains(health.Items, item => item.ExtensionId == extensionId);
    }

    private static void BlockLog(TestDataRoot dataRoot)
    {
        // A directory squatting on the lock path makes every log write fail
        // immediately with access denied.
        var lockPath = Path.Combine(dataRoot.Path, "Logs", "app.jsonl.lock");
        if (File.Exists(lockPath))
            File.Delete(lockPath);
        Directory.CreateDirectory(lockPath);
    }

    private static string Request(string requestId, object payload) =>
        JsonSerializer.Serialize(new
        {
            type = "request",
            requestId,
            method = "actions.run",
            payload
        });
}
