using System.Text.Json;
using Win11PerformanceControlCenter.App.Core;
var actions = new ActionCatalog().All.ToArray();
Console.WriteLine(JsonSerializer.Serialize(actions, HostBridge.JsonOptions));
