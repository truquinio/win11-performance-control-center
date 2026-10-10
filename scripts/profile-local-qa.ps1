param(
    [string]$Executable = (Join-Path $PSScriptRoot "..\artifacts\local-win-x64\Win11PerformanceControlCenter.exe"),
    [ValidateRange(1, 15)][int]$Samples = 6
)

$ErrorActionPreference = "Stop"
# Never attach to, stop, or profile an instance already opened by the user.
if (@(Get-Process -Name Win11PerformanceControlCenter -ErrorAction SilentlyContinue).Count -ne 0) {
    throw "QA_PERF_SKIPPED: an application instance already exists."
}

$watch = [Diagnostics.Stopwatch]::StartNew()
$started = & (Join-Path $PSScriptRoot "start-local-minimized.ps1") -Executable $Executable
$qaProcessId = [int]$started.Id
try {
    if ($started.Exited -or $started.MainWindowHandle -eq 0) {
        throw "QA_PERF_FAIL: packaged WPF window did not initialize."
    }
    $startupMs = $watch.ElapsedMilliseconds
    $rows = for ($index = 0; $index -lt $Samples; $index++) {
        Start-Sleep -Milliseconds 550
        $process = Get-Process -Id $qaProcessId -ErrorAction Stop
        [pscustomobject]@{
            WorkingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 1)
            PrivateMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
            Handles = $process.HandleCount
            CpuSeconds = [math]::Round($process.CPU, 2)
        }
    }
    $avgWs = [math]::Round((($rows | Measure-Object WorkingSetMiB -Average).Average), 1)
    $maxPrivate = ($rows | Measure-Object PrivateMiB -Maximum).Maximum
    $maxHandles = ($rows | Measure-Object Handles -Maximum).Maximum
    "QA_PERF_OK startupMs=$startupMs avgWorkingSetMiB=$avgWs maxPrivateMiB=$maxPrivate maxHandles=$maxHandles samples=$Samples"
    "QA_PERF_SCOPE parent WPF process only; WebView2 child memory not included."
} finally {
    $owned = Get-Process -Id $qaProcessId -ErrorAction SilentlyContinue
    if ($owned) { Stop-Process -Id $qaProcessId -ErrorAction Stop }
    "QA_PERF_PROCESS_STOPPED pid=$qaProcessId"
}
