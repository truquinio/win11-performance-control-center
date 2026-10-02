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
    private readonly HostBridge _bridge = HostBridge.CreateDefault();

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
        if (message == WmGetMinMaxInfo)
        {
            ApplyMonitorWorkingArea(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyMonitorWorkingArea(
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

    private void ConstrainToWorkingArea()
    {
        var workArea = SystemParameters.WorkArea;
        const double margin = 16d;

        var availableWidth = Math.Max(800d, workArea.Width - margin);
        var availableHeight = Math.Max(560d, workArea.Height - margin);

        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;

        if (WindowState == WindowState.Normal)
        {
            Width = Math.Min(Math.Max(Width, MinWidth), availableWidth);
            Height = Math.Min(Math.Max(Height, MinHeight), availableHeight);

            Left = workArea.Left + Math.Max(0d, (workArea.Width - Width) / 2d);
            Top = workArea.Top + Math.Max(0d, (workArea.Height - Height) / 2d);
        }
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
        core.WebMessageReceived += OnWebMessageReceived;

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

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrustedLocalUri(e.Source))
            return;

        try
        {
            if (TryHandleUiMessage(e.WebMessageAsJson))
                return;

            var response = await _bridge.HandleAsync(e.WebMessageAsJson);
            var json = JsonSerializer.Serialize(response, HostBridge.JsonOptions);
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
    }

    private bool TryHandleUiMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeNode) ||
                !string.Equals(
                    typeNode.GetString(),
                    "ui-state",
                    StringComparison.Ordinal))
            {
                return false;
            }

            var state = root.TryGetProperty("state", out var stateNode)
                ? stateNode.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(state))
                return true;

            var normalized = state.Trim().ToUpperInvariant();
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
