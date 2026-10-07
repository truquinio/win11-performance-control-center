using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Win11PerformanceControlCenter.App.Core;

public static class ElevatedActionRunner
{
    public static async Task<int> RunAsync(
        string actionId,
        Guid token,
        string? parametersBase64 = null)
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

                JsonElement? parameters = DecodeParameters(
                    parametersBase64);

                using var bridge = HostBridge.CreateDefault();
                var requestId = token.ToString("N");
                var payload = new Dictionary<string, object?>
                {
                    ["id"] = action.Id
                };
                if (parameters is JsonElement supplied)
                    payload["parameters"] = supplied;

                var request = JsonSerializer.Serialize(
                    new
                    {
                        type = "request",
                        requestId,
                        method = "actions.run",
                        payload
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
    private static JsonElement? DecodeParameters(
        string? parametersBase64)
    {
        if (string.IsNullOrWhiteSpace(parametersBase64))
            return null;

        if (parametersBase64.Length >
            ElevatedActionProtocol.MaxEncodedParametersLength)
        {
            throw new InvalidOperationException(
                "Parámetros elevados demasiado grandes.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(parametersBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Parámetros elevados mal codificados.",
                ex);
        }

        if (bytes.Length > 16 * 1024)
            throw new InvalidOperationException(
                "Payload elevado demasiado grande.");

        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                "Los parámetros elevados deben ser un objeto JSON.");

        return document.RootElement.Clone();
    }

}
