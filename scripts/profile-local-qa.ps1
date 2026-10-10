param(
    [string]$Executable = (Join-Path $PSScriptRoot "..\artifacts\local-win-x64\Win11PerformanceControlCenter.exe"),
    [ValidateRange(1, 15)][int]$Samples = 6
)

$ErrorActionPreference = "Stop"
# A user's instance is not a benchmark fixture: never attach to or stop it.
if (@(Get-Process -Name Win11PerformanceControlCenter -ErrorAction SilentlyContinue).Count -ne 0) {
    throw "QA_PERF_SKIPPED: an application instance already exists."
}

function Get-QAProcessSample([int]$rootId) {
    # Query only WebView2 executables; walk parent IDs from our OWN WPF PID,
    # never include Edge or WebView2 processes belonging to other applications.
    $webview = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'")
    $descendants = [Collections.Generic.HashSet[int]]::new()
    [void]$descendants.Add($rootId)
    do {
        $changed = $false
        foreach ($item in $webview) {
            $id = [int]$item.ProcessId
            if ($descendants.Contains([int]$item.ParentProcessId) -and
                $descendants.Add($id)) { $changed = $true }
        }
    } while ($changed)
    $totalWorking = 0L
    $totalPrivate = 0L
    $totalHandles = 0
    $count = 0
    foreach ($id in $descendants) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $process) { continue }
        $totalWorking += [long]$process.WorkingSet64
        $totalPrivate += [long]$process.PrivateMemorySize64
        $totalHandles += [int]$process.HandleCount
        $count++
    }
    [pscustomobject]@{
        ProcessCount = $count
        WorkingSetMiB = [math]::Round($totalWorking / 1MB, 1)
        PrivateMiB = [math]::Round($totalPrivate / 1MB, 1)
        Handles = $totalHandles
    }
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
        $root = Get-Process -Id $qaProcessId -ErrorAction Stop
        Get-QAProcessSample $qaProcessId
    }
    $avgWs = [math]::Round(($rows | Measure-Object WorkingSetMiB -Average).Average, 1)
    $maxPrivate = ($rows | Measure-Object PrivateMiB -Maximum).Maximum
    $maxHandles = ($rows | Measure-Object Handles -Maximum).Maximum
    $maxProcesses = ($rows | Measure-Object ProcessCount -Maximum).Maximum
    "QA_PERF_OK startupMs=$startupMs avgAggregateWorkingSetMiB=$avgWs maxAggregatePrivateMiB=$maxPrivate maxAggregateHandles=$maxHandles maxProcessCount=$maxProcesses samples=$Samples"
    "QA_PERF_SCOPE only QA-owned WPF and WebView2 descendants. Working sets may share pages and are not physical RAM totals."
} finally {
    # Only the WPF process started by this run may be stopped.
    $owned = Get-Process -Id $qaProcessId -ErrorAction SilentlyContinue
    if ($owned) { Stop-Process -Id $qaProcessId -ErrorAction Stop }
    "QA_PERF_PROCESS_STOPPED pid=$qaProcessId"
}
