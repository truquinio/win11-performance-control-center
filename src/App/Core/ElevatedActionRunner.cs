using System.IO;
using System.Text.Json;

namespace Win11PerformanceControlCenter.App.Core;

public static class ElevatedActionRunner
{
    public static async Task<int> RunAsync(
        string actionId,
        Guid token)
    {
        var resultPath = ElevatedActionProtocol.GetResultPath(token);
        Directory.CreateDirectory(
            ElevatedActionProtocol.GetResultDirectory());

        BridgeResponse response;

        try
        {
            var privilege = new PrivilegeBoundary();
            if (!privilege.IsElevated)
                throw new InvalidOperationException(
                    "El helper no recibió un token administrativo.");

            var catalog = new ActionCatalog();
            var action = catalog.GetRequired(actionId);
            if (!action.RequiresAdmin)
                throw new InvalidOperationException(
                    "La acción solicitada no pertenece al boundary elevado.");

            using var bridge = HostBridge.CreateDefault();
            var requestId = token.ToString("N");
            var request = JsonSerializer.Serialize(
                new
                {
                    type = "request",
                    requestId,
                    method = "actions.run",
                    payload = new { id = action.Id }
                },
                HostBridge.JsonOptions);
            response = await bridge.HandleAsync(request);
        }
        catch (Exception ex)
        {
            response = new BridgeResponse(
                token.ToString("N"),
                false,
                null,
                ex.Message);
        }

        try
        {
            var tempPath = resultPath + ".tmp";
            var json = JsonSerializer.Serialize(
                response,
                HostBridge.JsonOptions);

            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, resultPath, overwrite: true);
            return response.Ok ? 0 : 1;
        }
        catch
        {
            return 2;
        }
    }
}
