using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Win11PerformanceControlCenter.App.Core;

public static class ElevatedActionRunner
{
    public static async Task<int> RunAsync(
        string actionId,
        Guid token)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                ElevatedActionProtocol.GetPipeName(token),
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            using var connectCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(connectCts.Token);

            BridgeResponse response;
            try
            {
                var privilege = new PrivilegeBoundary();
                if (!privilege.IsElevated)
                    throw new InvalidOperationException(
                        "El helper no recibió privilegios administrativos.");

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

            var json = JsonSerializer.Serialize(
                response,
                HostBridge.JsonOptions);
            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                bufferSize: 4096,
                leaveOpen: true);
            await writer.WriteAsync(json);
            await writer.FlushAsync();

            return response.Ok ? 0 : 1;
        }
        catch
        {
            return 2;
        }
    }
}
