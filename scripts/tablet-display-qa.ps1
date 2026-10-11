# Opt-in QA for an already-connected spacedesk secondary monitor.
# Launches one minimized Win11 Control instance and moves ONLY that window.
# Does not change display configuration or interfere with other apps.
param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\artifacts\local-win-x64\Win11PerformanceControlCenter.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class QAMonitor {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int hgt,bool repaint);
 [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd,int cmd);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
}
'@
$aware = [QAMonitor]::SetProcessDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$secondary = @([System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary })
if ($secondary.Count -ne 1) { throw "QA_REQUIRES_EXACTLY_ONE_SECONDARY_DISPLAY; detected=$($secondary.Count)" }
if (@(Get-Process Win11PerformanceControlCenter -ErrorAction SilentlyContinue).Count) {
    throw 'QA_ABORT_EXISTING_WIN11_CONTROL_PROCESS'
}
$s = $secondary[0]; $area = $s.WorkingArea
"QA_MONITOR_READY name=$($s.DeviceName) dpiAware=$aware bounds=$($s.Bounds) working=$area"
$qa = & (Join-Path $PSScriptRoot 'start-local-minimized.ps1') -Executable $Executable
$pidOwned = [int]$qa.Id
try {
    if ($qa.Exited -or -not $qa.MainWindowHandle) { throw 'QA_WINDOW_START_FAILED' }
    $handle = [IntPtr]$qa.MainWindowHandle
    $beforeDpi = [QAMonitor]::GetDpiForWindow($handle)
    # SW_SHOWNOACTIVATE restores our own minimized QA window without taking focus.
    [QAMonitor]::ShowWindowAsync($handle, 4) | Out-Null
    Start-Sleep -Milliseconds 450
    $moved = [QAMonitor]::MoveWindow($handle, $area.X, $area.Y, $area.Width, $area.Height, $true)
    if (-not $moved) { throw 'QA_MOVE_WINDOW_FAILED' }
    Start-Sleep -Seconds 3
    $rect = New-Object QAMonitor+RECT
    if (-not [QAMonitor]::GetWindowRect($handle,[ref]$rect)) { throw 'QA_GET_WINDOW_RECT_FAILED' }
    $afterDpi = [QAMonitor]::GetDpiForWindow($handle)
    $newScreen = [System.Windows.Forms.Screen]::FromHandle($handle)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    "QA_WINDOW_ON_TABLET expected=$($s.DeviceName) actual=$($newScreen.DeviceName) rect=$($rect.Left),$($rect.Top),$($width)x$($height) dpiBefore=$beforeDpi dpiAfter=$afterDpi"
    if ($newScreen.DeviceName -ne $s.DeviceName) { throw 'QA_WINDOW_NOT_ON_TABLET' }
    # PrintWindow captures ONLY the QA-owned app, not the rest of the desktop.
    $bmp = [Drawing.Bitmap]::new($width,$height)
    $gfx = [Drawing.Graphics]::FromImage($bmp)
    $hdc = $gfx.GetHdc()
    try { $rendered = [QAMonitor]::PrintWindow($handle,$hdc,2) }
    finally { $gfx.ReleaseHdc($hdc);$gfx.Dispose() }
    $out = Join-Path $PSScriptRoot '..\artifacts\visual-qa\tablet-spacedesk-live.png'
    try { if ($rendered) { $bmp.Save($out,[Drawing.Imaging.ImageFormat]::Png) } }
    finally { $bmp.Dispose() }
    if ($rendered) {
        "QA_CAPTURE_WRITTEN $out"
    } else {
        "QA_CAPTURE_UNAVAILABLE PrintWindow returned false; no desktop capture performed"
    }
    # WebView2 exposes UI Automation. Invoke ONLY the mode switch, never a
    # system action, without moving the user's mouse or focusing a window.
    try {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        $topChildren = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        "QA_UIA_ROOT_CHILDREN=$($topChildren.Count)"
        $cond = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Modo experto')
        $modeButton = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -eq $modeButton) {
            "QA_UIA_MODE_SWITCH_UNAVAILABLE"
        } else {
            $pattern = $modeButton.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)
            $pattern.Invoke()
            Start-Sleep -Milliseconds 1200
            $bmp2 = [Drawing.Bitmap]::new($width,$height)
            $gfx2 = [Drawing.Graphics]::FromImage($bmp2)
            $hdc2 = $gfx2.GetHdc()
            try { $rendered2 = [QAMonitor]::PrintWindow($handle,$hdc2,2) }
            finally { $gfx2.ReleaseHdc($hdc2);$gfx2.Dispose() }
            $out2 = Join-Path $PSScriptRoot '..\artifacts\visual-qa\tablet-spacedesk-developer-live.png'
            try { if ($rendered2) { $bmp2.Save($out2,[Drawing.Imaging.ImageFormat]::Png) } }
            finally { $bmp2.Dispose() }
            "QA_UIA_MODE_INVOKED=YES developerCapture=$rendered2"
        }
    } catch {
        "QA_UIA_MODE_SWITCH_UNVERIFIED $($_.Exception.GetType().Name): $($_.Exception.Message)"
    }
} finally {
    if (Get-Process -Id $pidOwned -ErrorAction SilentlyContinue) {
        Stop-Process -Id $pidOwned -ErrorAction SilentlyContinue
    }
    "QA_ONLY_PROCESS_CLOSED=$pidOwned"
}