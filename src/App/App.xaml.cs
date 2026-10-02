using System.Windows;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains(
                ElevatedActionProtocol.ActionFlag,
                StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (!ElevatedActionProtocol.TryParse(
                    e.Args,
                    out var actionId,
                    out var token))
            {
                Shutdown(3);
                return;
            }

            _ = RunElevatedAndExitAsync(actionId, token);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private async Task RunElevatedAndExitAsync(
        string actionId,
        Guid token)
    {
        var exitCode = 2;
        try
        {
            exitCode = await ElevatedActionRunner.RunAsync(
                actionId,
                token);
        }
        catch
        {
            // The elevated helper must always terminate, even if its
            // result directory becomes unavailable before it can report.
            exitCode = 2;
        }
        finally
        {
            Shutdown(exitCode);
        }
    }
}
