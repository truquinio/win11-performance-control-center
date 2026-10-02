using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> KnownUiStates =
        new(StringComparer.Ordinal)
        {
            "IDLE",
            "ANALYZING",
            "OPTIMIZING",
            "MAINTENANCE",
            "REBOOT_REQUIRED",
            "ERROR"
        };

    private readonly HostBridge _bridge = HostBridge.CreateDefault();
    private readonly List<DateTime> _rendererRecoveries = [];
    private IntPtr _limitsMonitor;
    private bool _inMoveSizeLoop;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        StateChanged += (_, _) =>
        {
            UpdateWindowChromeForState();
            ConstrainToWorkingArea();
        };
        Closed += (_, _) => _bridge.Dispose();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        const int WmSettingChange = 0x001A;
        const int WmDisplayChange = 0x007E;
        const int WmDpiChanged = 0x02E0;
        const int WmMove = 0x0003;
        const int WmEnterSizeMove = 0x0231;
        const int WmExitSizeMove = 0x0232;
        const uint MonitorDefaultToNearest = 0x00000002;

        if (message == WmGetMinMaxInfo)
        {
            ApplyMonitorWorkingArea(hwnd, lParam);
            handled = true;
        }
        else if (message == WmEnterSizeMove)
        {
            _inMoveSizeLoop = true;
        }
        else if (message == WmExitSizeMove)
        {
            _inMoveSizeLoop = false;
            QueueConstrain(reposition: false);
        }
        else if (message == WmMove)
        {
            // Moving to another monitor with the same DPI raises none of the
            // display messages below, yet the min/max limits were computed
            // for the previous monitor's work area.
            if (MonitorFromWindow(hwnd, MonitorDefaultToNearest) != _limitsMonitor)
                QueueConstrain(reposition: false);
        }
        else if (message is WmSettingChange or WmDisplayChange or WmDpiChanged)
        {
            // Never pull the window back while the user is dragging it
            // across a monitor boundary.
            QueueConstrain(reposition: !_inMoveSizeLoop);
        }

        return IntPtr.Zero;
    }

    private void QueueConstrain(bool reposition)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded && WindowState != WindowState.Minimized)
                ConstrainToWorkingArea(reposition);
        });
    }

    private void ApplyMonitorWorkingArea(
        IntPtr hwnd,
        IntPtr lParam)
    {
        const uint MonitorDefaultToNearest = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return;

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = monitorInfo.WorkArea;
        var bounds = monitorInfo.MonitorArea;

        info.MaxPosition.X = work.Left - bounds.Left;
        info.MaxPosition.Y = work.Top - bounds.Top;
        info.MaxSize.X = work.Right - work.Left;
        info.MaxSize.Y = work.Bottom - work.Top;
        info.MaxTrackSize = info.MaxSize;

        // Handling this message replaces WPF's own processing, which is what
        // normally turns MinWidth/MinHeight into the minimum tracking size.
        // Without it the window could be resized below the layout minimum.
        var dpi = VisualTreeHelper.GetDpi(this);
        var minWidth = double.IsFinite(MinWidth) ? MinWidth : 0d;
        var minHeight = double.IsFinite(MinHeight) ? MinHeight : 0d;
        info.MinTrackSize.X = WindowGeometryPolicy.ScaleMinimumToPixels(
            minWidth,
            dpi.DpiScaleX,
            info.MaxSize.X);
        info.MinTrackSize.Y = WindowGeometryPolicy.ScaleMinimumToPixels(
            minHeight,
            dpi.DpiScaleY,
            info.MaxSize.Y);

        Marshal.StructureToPtr(info, lParam, false);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ConstrainToWorkingArea();
        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "No se pudo iniciar la interfaz WebView2.\n\n" + ex.Message,
                "Win11 Performance Control Center",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void UpdateWindowChromeForState()
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is null)
            return;

        chrome.ResizeBorderThickness = WindowState == WindowState.Maximized
            ? new Thickness(0)
            : new Thickness(6);
    }

    private void ConstrainToWorkingArea(bool reposition = true)
    {
        var workArea = GetCurrentMonitorWorkArea();
        var layout = WindowGeometryPolicy.Constrain(
            workArea.Left,
            workArea.Top,
            workArea.Width,
            workArea.Height,
            Width,
            Height,
            Left,
            Top);

        MinWidth = layout.MinWidth;
        MinHeight = layout.MinHeight;
        MaxWidth = layout.MaxWidth;
        MaxHeight = layout.MaxHeight;

        if (reposition && WindowState == WindowState.Normal)
        {
            Width = layout.Width;
            Height = layout.Height;
            Left = layout.Left;
            Top = layout.Top;
        }
    }

    private Rect GetCurrentMonitorWorkArea()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return SystemParameters.WorkArea;

        const uint MonitorDefaultToNearest = 0x00000002;
        var monitor = MonitorFromWindow(source.Handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return SystemParameters.WorkArea;

        _limitsMonitor = monitor;

        var info = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref info) ||
            source.CompositionTarget is null)
        {
            return SystemParameters.WorkArea;
        }

        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(
            new Point(info.WorkArea.Left, info.WorkArea.Top));
        var bottomRight = fromDevice.Transform(
            new Point(info.WorkArea.Right, info.WorkArea.Bottom));

        return new Rect(topLeft, bottomRight);
    }

    private async Task InitializeWebViewAsync()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
        var index = Path.Combine(webRoot, "index.html");
        if (!File.Exists(index))
            throw new FileNotFoundException("No se encontró el build del frontend.", index);

        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Microsoft Edge WebView2 Runtime no está disponible en este equipo.",
                ex);
        }

        AppPaths.EnsureDirectories();
        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: AppPaths.WebView2UserData);
        await WebView.EnsureCoreWebView2Async(environment);

        WebView.ZoomFactor = 1d;

        var core = WebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        // The local UI uses confirm() only for explicit WRITE acknowledgements.
        core.Settings.AreDefaultScriptDialogsEnabled = true;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(
            "wpcc.local",
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnWebViewProcessFailed;

        WebView.Source = new Uri(
            "https://wpcc.local/index.html?surface=local");
    }

    private static bool IsTrustedLocalUri(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, "wpcc.local", StringComparison.OrdinalIgnoreCase);
    }

    private static void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsTrustedLocalUri(e.Uri))
            e.Cancel = true;
    }

    private static void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private static void OnPermissionRequested(
        object? sender,
        CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
    }

    private static void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
    }

    private void OnWebViewProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                RecoverRenderer();
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The whole WebView2 host is gone and cannot be reloaded.
                MessageBox.Show(
                    this,
                    "El runtime WebView2 dejó de responder. La aplicación se cerrará; podés volver a abrirla.",
                    "Win11 Performance Control Center",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
                break;
            default:
                // Utility/GPU processes are restarted by WebView2 itself.
                break;
        }
    }

    private void RecoverRenderer()
    {
        // Without a reload the window would stay blank for the rest of the
        // session. The cap stops a renderer that dies on load from looping.
        var now = DateTime.UtcNow;
        _rendererRecoveries.RemoveAll(time =>
            now - time > TimeSpan.FromMinutes(1));
        if (_rendererRecoveries.Count >= 3)
        {
            MessageBox.Show(
                this,
                "La interfaz falló repetidamente al cargar. La aplicación se cerrará.",
                "Win11 Performance Control Center",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
            return;
        }

        _rendererRecoveries.Add(now);
        try
        {
            WebView.CoreWebView2?.Reload();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            ObjectDisposedException or
            COMException)
        {
            Close();
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrustedLocalUri(e.Source))
            return;

        try
        {
            if (TryHandleUiMessage(e.WebMessageAsJson))
                return;

            var json = await _bridge.HandleSerializedAsync(
                e.WebMessageAsJson);
            WebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (ObjectDisposedException)
        {
            // Closing the window wins over a late IPC response.
        }
        catch (InvalidOperationException)
        {
            // The WebView can be unavailable while a long-running action completes.
        }
        catch (COMException)
        {
            // The WebView2 process went away before the response was posted.
        }
    }

    private bool TryHandleUiMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeNode) ||
                typeNode.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    typeNode.GetString(),
                    "ui-state",
                    StringComparison.Ordinal))
            {
                return false;
            }

            var state = root.TryGetProperty("state", out var stateNode) &&
                        stateNode.ValueKind == JsonValueKind.String
                ? stateNode.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(state))
                return true;

            var normalized = state.Trim().ToUpperInvariant();
            // The title bar only mirrors the documented operation states.
            if (!KnownUiStates.Contains(normalized))
                return true;

            TitleStateText.Text = "Estado: " + normalized;

            var (foreground, background, border) = normalized switch
            {
                "IDLE" => ("#4BF49A", "#0D563A", "#176F4B"),
                "ERROR" => ("#FF7B7B", "#4A1F2A", "#793442"),
                "REBOOT_REQUIRED" => ("#FFD15A", "#4A3814", "#7A5B16"),
                _ => ("#56C7FF", "#0A3551", "#175E86")
            };

            var brushConverter = new BrushConverter();
            TitleStateText.Foreground =
                (Brush)brushConverter.ConvertFromString(foreground)!;
            TitleStateDot.Fill =
                (Brush)brushConverter.ConvertFromString(foreground)!;
            TitleStateBadge.Background =
                (Brush)brushConverter.ConvertFromString(background)!;
            TitleStateBadge.BorderBrush =
                (Brush)brushConverter.ConvertFromString(border)!;

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr hwnd,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo monitorInfo);
}
