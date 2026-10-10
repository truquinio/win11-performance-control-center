using System.Diagnostics;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

string root = Path.Combine(Path.GetTempPath(), "wpcc-read-smoke-" + Guid.NewGuid().ToString("N"));
string[] actions = ["system.workload.status", "memory.analyze", "network.test", "thermal.audit"];
var catalog = new ActionCatalog();
try
{
    using var bridge = HostBridge.CreateDefault(root);
    foreach (string id in actions)
    {
        var definition = catalog.GetRequired(id);
        if (definition.Mode != ActionMode.READ || definition.RequiresAdmin)
            throw new InvalidOperationException("Acción de prueba no segura: " + id);
        var stopwatch = Stopwatch.StartNew();
        string request = JsonSerializer.Serialize(new {
            type = "request", requestId = Guid.NewGuid().ToString("N"),
            method = "actions.run", payload = new { id }
        });
        BridgeResponse response = await bridge.HandleAsync(request).WaitAsync(TimeSpan.FromSeconds(30));
        stopwatch.Stop();
        var outcome = response.Result as ActionResult;
        if (!response.Ok || outcome is null)
            throw new InvalidOperationException("IPC falló para " + id + ": " + response.Error);
        Console.WriteLine($"REAL_READ id={id} actionSuccess={outcome.Success} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }
}
finally
{
    try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    catch (IOException) { /* only disposable fixture; allow a busy log to close */ }
    catch (UnauthorizedAccessException) { /* leave fixture for inspection if locked */ }
}
