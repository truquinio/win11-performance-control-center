param(
    [string]$Executable = (Join-Path $PSScriptRoot "..\src\App\bin\Release\net10.0-windows\Win11PerformanceControlCenter.exe")
)

$resolved = (Resolve-Path $Executable).Path
$process = Start-Process $resolved -PassThru

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowLaunch {
    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
'@

for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 100
    $process.Refresh()
    if ($process.HasExited) { break }
    if ($process.MainWindowHandle -ne 0) {
        [WindowLaunch]::ShowWindowAsync($process.MainWindowHandle, 6) | Out-Null
        break
    }
}

$process.Refresh()
[pscustomobject]@{
    Id = $process.Id
    Exited = $process.HasExited
    MainWindowHandle = $process.MainWindowHandle
}
