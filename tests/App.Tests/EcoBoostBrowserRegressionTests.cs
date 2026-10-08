using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;
using Win11PerformanceControlCenter.App.Services;

namespace App.Tests;

public sealed class EcoBoostBrowserRegressionTests
{
    [Theory]
    [InlineData("chrome")]
    [InlineData("chrome-headless-shell")]
    [InlineData("msedge")]
    [InlineData("node")]
    [InlineData("nodew")]
    [InlineData("pm2")]
    [InlineData("python")]
    [InlineData("pythonw")]
    [InlineData("cloudflared")]
    [InlineData("adb")]
    [InlineData("emulator")]
    [InlineData("qemu-system-x86_64")]
    [InlineData("qemu-system-aarch64")]
    [InlineData("ollama")]
    [InlineData("msedgewebview2")]
    public void GenericMemoryTrimAndEcoQos_ExcludeActiveAutomation(string name)
    {
        var candidate = new ProcessCandidate(
            12345,
            name,
            2L * 1024 * 1024 * 1024,
            HasMainWindow: false);

        Assert.False(ProcessSafetyPolicy.IsEligible(
            candidate,
            requireBackground: true));
        Assert.False(ProcessSafetyPolicy.IsEligible(
            candidate,
            requireBackground: false));
    }

    [Fact]
    public void PlaywrightAndSepe_AreIdentifiedByDifferentAncestry()
    {
        var t = DateTimeOffset.Parse("2026-10-08T18:55:00Z");
        var parents = new BrowserObservedProcess[]
        {
            P(100, 4, "node.exe", @"C:\node.exe",
                @"node.exe D:\DOCS\Z-CV\bot_linkedin\src\telegram\listener.js",
                t.AddMinutes(-10), 80, 65),
            P(200, 4, "node.exe", @"C:\node.exe",
                "node.exe cita-sepe-agent/server.js",
                t.AddMinutes(-15), 80, 65),
            P(101, 100, "chrome.exe",
                @"C:\Users\demo\AppData\Local\ms-playwright\chromium-1243\chrome-win\chrome.exe",
                "--type=renderer", t, 370, 230),
            P(201, 200, "chrome-headless-shell.exe",
                @"C:\Users\demo\AppData\Local\ms-playwright\chromium_headless_shell-1243\chrome-headless-shell-win64\chrome-headless-shell.exe",
                "--type=renderer", t, 213, 180),
            P(300, 4, "msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                "--type=renderer", t, 100, 90)
        };
        var byId = parents.ToDictionary(item => item.ProcessId);

        var linkedIn = BrowserAutomationTelemetryService.ToRow(
            parents[2],
            byId);
        var sepe = BrowserAutomationTelemetryService.ToRow(
            parents[3],
            byId);
        var edge = BrowserAutomationTelemetryService.ToRow(
            parents[4],
            byId);

        Assert.Equal("PLAYWRIGHT_CHROME", linkedIn.Family);
        Assert.Equal("LINKEDIN_BOT", linkedIn.Owner);
        Assert.Equal("CONFIRMED", linkedIn.Attribution);
        Assert.Equal("PLAYWRIGHT_HEADLESS", sepe.Family);
        Assert.Equal("SEPE_AGENT", sepe.Owner);
        Assert.Equal("CONFIRMED", sepe.Attribution);
        Assert.Equal("EDGE", edge.Family);
        Assert.Equal("UNKNOWN", edge.Owner);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public void SepeIncident_376To145MiB_IsRecoveryNotMemoryLeak()
    {
        using var root = new TestDataRoot();
        var at = DateTimeOffset.Parse("2026-10-08T18:55:04Z");
        long wsMiB = 145;
        var service = new BrowserAutomationTelemetryService(
            Path.Combine(root.Path, "browser-history.json"),
            () => [P(17940, 7568, "chrome-headless-shell.exe",
                @"C:\ms-playwright\chromium_headless_shell\chrome-headless-shell.exe",
                "--type=browser --token=PRIVATE",
                at.AddMinutes(-10), wsMiB, 70)],
            () => at);

        service.Analyze();
        at = at.AddSeconds(75);
        wsMiB = 376;
        var peak = service.Analyze();
        at = at.AddSeconds(45);
        wsMiB = 145;
        var recovered = service.Analyze();

        Assert.Equal("OBSERVED", Assert.Single(
            peak.Families, f => f.Family == "PLAYWRIGHT_HEADLESS").Status);
        Assert.Equal("TRANSIENT_RECOVERED", Assert.Single(
            recovered.Families, f => f.Family == "PLAYWRIGHT_HEADLESS").Status);

        var state = File.ReadAllText(
            Path.Combine(root.Path, "browser-history.json"));
        Assert.DoesNotContain("PRIVATE", state);
        Assert.DoesNotContain("ms-playwright", state);
        Assert.DoesNotContain("17940", state);
    }

    [Fact]
    [Trait("Layer", "RealWorldEval")]
    public void LinkedInIncident_2180MiB_RecoversAfterWork()
    {
        using var root = new TestDataRoot();
        var at = DateTimeOffset.Parse("2026-10-08T19:09:00Z");
        long ws = 2180;
        long privateMb = 1740;
        var service = new BrowserAutomationTelemetryService(
            Path.Combine(root.Path, "browser-history.json"),
            () => [P(123, 4, "chrome.exe",
                @"C:\ms-playwright\chromium-1243\chrome-win\chrome.exe",
                "--type=renderer", at.AddMinutes(-1), ws, privateMb)],
            () => at);

        service.Analyze();
        at = at.AddMinutes(1);
        ws = 1153;
        privateMb = 900;
        service.Analyze();
        at = at.AddMinutes(1);
        ws = 144;
        privateMb = 100;
        var report = service.Analyze();

        Assert.Equal("TRANSIENT_RECOVERED", Assert.Single(
            report.Families, f => f.Family == "PLAYWRIGHT_CHROME").Status);
    }

    [Fact]
    public void SustainedHighPrivateMemory_RequiresAtLeastThreeSpacedSamples()
    {
        using var root = new TestDataRoot();
        var at = DateTimeOffset.Parse("2026-10-08T19:00:00Z");
        var service = new BrowserAutomationTelemetryService(
            Path.Combine(root.Path, "browser-history.json"),
            () => [P(111, 4, "chrome.exe",
                @"C:\ms-playwright\chromium-1243\chrome-win\chrome.exe",
                "--type=renderer", at.AddHours(-1), 2200, 1800)],
            () => at);

        var first = service.Analyze();
        Assert.Equal("OBSERVED", Assert.Single(
            first.Families, f => f.Family == "PLAYWRIGHT_CHROME").Status);

        at = at.AddMinutes(1);
        service.Analyze();
        at = at.AddMinutes(1);
        var third = service.Analyze();

        Assert.Equal("SUSTAINED_REVIEW", Assert.Single(
            third.Families, f => f.Family == "PLAYWRIGHT_CHROME").Status);
    }

    [Fact]
    public async Task EvaluationHostBridge_ReportsAbsentBrowserWithoutLaunchingIt()
    {
        using var root = new TestDataRoot();
        using var bridge = HostBridge.CreateDefault(root.Path);

        var request = JsonSerializer.Serialize(new
        {
            type = "request",
            requestId = "eco-fixture",
            method = "actions.run",
            payload = new { id = "browsers.automation.audit" }
        }, HostBridge.JsonOptions);
        var response = await bridge.HandleAsync(request);

        Assert.True(response.Ok, response.Error);
        var result = Assert.IsType<ActionResult>(response.Result);
        Assert.True(result.Success);
        var report = Assert.IsType<BrowserAutomationReport>(result.Data);
        Assert.Equal(0, report.TotalBrowserProcesses);
        Assert.All(report.Families, group =>
            Assert.Equal("NOT_RUNNING", group.Status));
    }

    private static BrowserObservedProcess P(
        int pid,
        int parent,
        string name,
        string path,
        string command,
        DateTimeOffset at,
        long workingSetMiB,
        long privateMiB) =>
        new(pid, parent, name, path, command, at,
            workingSetMiB * 1048576,
            privateMiB * 1048576);
}
