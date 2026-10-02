using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App;

public partial class App : Application
{
    private const string SingleInstanceName =
        @"Local\Win11PerformanceControlCenter.SingleInstance";

    private Mutex? _singleInstance;

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

        // Two UI instances would each keep their own in-memory copy of the
        // EcoQoS rollback snapshots and overwrite each other's state file.
        _singleInstance = new Mutex(
            initiallyOwned: true,
            SingleInstanceName,
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            ActivateRunningInstance();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            // Without this the process would vanish silently when the local
            // state or log directory cannot be opened.
            ShowFatalError(
                "No se pudo iniciar la aplicación.\n\n" + ex.Message);
            Shutdown(4);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread; the handle is still released.
            }

            _singleInstance.Dispose();
            _singleInstance = null;
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowFatalError(
            "Se produjo un error inesperado y la aplicación se cerrará.\n\n" +
            e.Exception.Message);
        Shutdown(5);
    }

    private static void ShowFatalError(string message) =>
        MessageBox.Show(
            message,
            "Win11 Performance Control Center",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private static void ActivateRunningInstance()
    {
        const int SwRestore = 9;

        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(
                         current.ProcessName))
            {
                using (process)
                {
                    if (process.Id == current.Id ||
                        process.SessionId != current.SessionId)
                    {
                        continue;
                    }

                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero)
                        continue;

                    if (IsIconic(handle))
                        ShowWindow(handle, SwRestore);
                    SetForegroundWindow(handle);
                    return;
                }
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            // Bringing the first instance forward is a courtesy only.
        }
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
            // IPC channel becomes unavailable before it can report.
            exitCode = 2;
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
}
