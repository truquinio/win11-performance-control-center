using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Core;

public sealed class ElevatedActionClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public async Task<ActionResult> ExecuteAsync(
        ActionDefinition action,
        CancellationToken cancellationToken = default)
    {
        if (!action.RequiresAdmin)
            throw new InvalidOperationException(
                "La acción no requiere elevación.");

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !File.Exists(executable))
        {
            throw new InvalidOperationException(
                "No se pudo resolver el ejecutable local.");
        }

        var token = Guid.NewGuid();
        var pipeName = ElevatedActionProtocol.GetPipeName(token);
        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add(
            ElevatedActionProtocol.ActionFlag);
        startInfo.ArgumentList.Add(action.Id);
        startInfo.ArgumentList.Add(
            ElevatedActionProtocol.TokenFlag);
        startInfo.ArgumentList.Add(token.ToString("N"));

        using var process = new Process { StartInfo = startInfo };

        try
        {
            // ShellExecute blocks until the UAC prompt is answered. Keeping
            // that wait off the caller's thread leaves the window responsive
            // while the secure desktop is showing.
            var started = await Task.Run(
                () => process.Start(),
                cancellationToken);
            if (!started)
                throw new InvalidOperationException(
                    "Windows no pudo iniciar el helper elevado.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(
                "La elevación UAC fue cancelada por el usuario.",
                ex);
        }
        catch (Win32Exception ex) when (
            ex.NativeErrorCode is 1260 or 577)
        {
            throw new InvalidOperationException(
                "Windows bloqueó el helper elevado mediante una política " +
                "de seguridad o integridad de código (por ejemplo, " +
                "Group Policy, AppLocker o WDAC). No se intentó eludirla.",
                ex);
        }

        using var timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutCts.CancelAfter(Timeout);

        try
        {
            var connectionTask = pipe.WaitForConnectionAsync(
                timeoutCts.Token);
            var exitTask = process.WaitForExitAsync(
                timeoutCts.Token);

            var first = await Task.WhenAny(
                connectionTask,
                exitTask);
            if (first == exitTask &&
                !connectionTask.IsCompletedSuccessfully)
            {
                // A cancelled wait means the budget expired, not that the
                // helper exited: surface that as the timeout it is.
                await exitTask;
                throw new InvalidOperationException(
                    $"El helper elevado terminó sin conectar al canal IPC (exit {process.ExitCode}).");
            }

            await connectionTask;

            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            var json = await ReadLimitedAsync(
                reader,
                timeoutCts.Token);
            await exitTask;

            var response = JsonSerializer.Deserialize<BridgeResponse>(
                json,
                HostBridge.JsonOptions) ?? throw new InvalidOperationException(
                    "Respuesta elevada inválida.");

            if (!string.Equals(
                    response.RequestId,
                    token.ToString("N"),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "El helper elevado devolvió un token inesperado.");
            }

            if (!response.Ok)
                throw new InvalidOperationException(
                    response.Error ?? "La acción elevada falló.");

            if (response.Result is not JsonElement element)
                throw new InvalidOperationException(
                    "Resultado elevado sin payload.");

            return element.Deserialize<ActionResult>(
                       HostBridge.JsonOptions)
                   ?? throw new InvalidOperationException(
                       "No se pudo interpretar el resultado elevado.");
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException(
                "La acción elevada superó el tiempo máximo.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<string> ReadLimitedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        const int MaxChars = 256 * 1024;
        var buffer = new char[4096];
        var builder = new StringBuilder();

        while (true)
        {
            var read = await reader.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (read == 0)
                break;

            if (builder.Length + read > MaxChars)
                throw new InvalidOperationException(
                    "La respuesta elevada excede el tamaño permitido.");

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Preserve the timeout/cancellation as the primary failure.
        }
    }
}
