using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
        var directory = ElevatedActionProtocol.GetResultDirectory();
        var resultPath = ElevatedActionProtocol.GetResultPath(token);
        Directory.CreateDirectory(directory);
        TryDelete(resultPath);
        TryDelete(resultPath + ".tmp");
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
            if (!process.Start())
                throw new InvalidOperationException(
                    "Windows no pudo iniciar el helper elevado.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(
                "La elevación UAC fue cancelada por el usuario.",
                ex);
        }

        using var timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException(
                    $"El helper elevado terminó sin resultado (exit {process.ExitCode}).");
            }

            var json = await File.ReadAllTextAsync(
                resultPath,
                cancellationToken);
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
        finally
        {
            TryDelete(resultPath);
            TryDelete(resultPath + ".tmp");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup of our own transient result is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // The elevated helper can briefly retain the file handle.
        }
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
