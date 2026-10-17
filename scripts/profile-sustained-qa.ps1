# Non-destructive, bounded WPF + owned WebView2 observation. Not a leak certificate.
[CmdletBinding()]
param(
    [string]$Executable,
    [ValidateRange(3, 120)][int]$Samples = 12,
    [ValidateRange(2, 60)][int]$IntervalSeconds = 5,
    [ValidateRange(1024, 32768)][int]$MinimumFreeMiB = 2048,
    [string]$Output
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $PSScriptRoot '..\src\App\bin\Release\net10.0-windows\Win11PerformanceControlCenter.exe'
}
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $PSScriptRoot ('..\artifacts\profile-sustained-qa-{0}-{1}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0,8))
}
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
if (-not [IO.Path]::IsPathRooted($Output)) {
    # Relative output belongs to this project's artifacts, never the caller's directory.
    $Output = Join-Path $artifactsRoot $Output
}
$Output = [IO.Path]::GetFullPath($Output)
# Windows PowerShell 5.1 uses .NET Framework, which has no Path.GetRelativePath.
# A trailing separator prevents accepting a sibling such as artifacts-old.
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $Output.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'QA_SUSTAINED_OUTPUT_OUTSIDE_ARTIFACTS: evidence must stay inside this project.'
}
function Assert-QAOutputPathHasNoReparseAncestors {
    $cursor = [IO.Path]::GetFullPath((Split-Path -Parent $Output))
    while ($true) {
        $isArtifactsRoot = $cursor.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)
        $isInsideArtifacts = $cursor.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)
        if (-not ($isArtifactsRoot -or $isInsideArtifacts)) { break }
        if (Test-Path -LiteralPath $cursor -PathType Container) {
            $ancestor = Get-Item -LiteralPath $cursor -Force
            if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'QA_SUSTAINED_OUTPUT_REPARSE_POINT: evidence path crosses a junction or symbolic link.'
            }
        }
        if ($isArtifactsRoot) { return }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent.Equals($cursor, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = [IO.Path]::GetFullPath($parent)
    }
}
Assert-QAOutputPathHasNoReparseAncestors
if (Test-Path -LiteralPath $Output) {
    throw 'QA_SUSTAINED_OUTPUT_EXISTS: existing evidence must not be overwritten.'
}
if (@(Get-Process -Name Win11PerformanceControlCenter -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'QA_SUSTAINED_SKIPPED: existing Win11 Control instance; never attach to or terminate it.'
}
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw 'QA_SUSTAINED_SKIPPED: release executable not found.'
}
function FreeMiB {
    [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024, 0)
}
if ((FreeMiB) -lt $MinimumFreeMiB) {
    throw 'QA_SUSTAINED_SKIPPED: insufficient memory headroom; other projects take priority.'
}
$ownedPid = $null
$ownedStart = $null
$startedAt = Get-Date
$rows = [Collections.Generic.List[object]]::new()
$summary = $null
try {
    $started = & (Join-Path $PSScriptRoot 'start-local-minimized.ps1') -Executable $Executable
    $ownedPid = [int]$started.Id
    $ownedProcess = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
    if ($ownedProcess) { $ownedStart = $ownedProcess.StartTime }
    if ($started.Exited -or $started.MainWindowHandle -eq 0 -or -not $ownedProcess) {
        throw 'QA_SUSTAINED_FAIL: QA-owned WPF window did not initialize.'
    }
    $lastCpuMs = $null
    $lastElapsed = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $cores = [math]::Max(1, [Environment]::ProcessorCount)
    for ($i = 0; $i -lt $Samples; $i++) {
        Start-Sleep -Seconds $IntervalSeconds
        $root = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if (-not $root -or $root.StartTime -ne $ownedStart) {
            throw 'QA_SUSTAINED_FAIL: QA-owned WPF process exited during observation.'
        }
        $free = FreeMiB
        if ($free -lt 1024) {
            throw 'QA_SUSTAINED_ABORTED: memory safety threshold crossed; QA process will be stopped.'
        }
        $webviews = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'")
        $owned = [Collections.Generic.HashSet[int]]::new()
        [void]$owned.Add($ownedPid)
        do {
            $added = $false
            foreach ($w in $webviews) {
                if ($owned.Contains([int]$w.ParentProcessId) -and $owned.Add([int]$w.ProcessId)) {
                    $added = $true
                }
            }
        } while ($added)
        $ws = 0L; $private = 0L; $handles = 0; $cpuMs = 0d; $count = 0
        foreach ($id in $owned) {
            $proc = Get-Process -Id $id -ErrorAction SilentlyContinue
            if (-not $proc) { continue }
            $ws += [long]$proc.WorkingSet64
            $private += [long]$proc.PrivateMemorySize64
            $handles += [int]$proc.HandleCount
            $cpuMs += $proc.TotalProcessorTime.TotalMilliseconds
            $count++
        }
        $elapsed = $clock.Elapsed.TotalSeconds
        $cpuPct = if ($null -eq $lastCpuMs) { $null } else {
            [math]::Round([math]::Max(0, ($cpuMs - $lastCpuMs) / (1000 * ($elapsed - $lastElapsed) * $cores) * 100), 2)
        }
        $lastCpuMs = $cpuMs; $lastElapsed = $elapsed
        $rows.Add([pscustomobject]@{
            Timestamp = (Get-Date).ToString('o')
            ElapsedSeconds = [math]::Round($elapsed, 1)
            Processes = $count
            AggregateWorkingSetMiB = [math]::Round($ws / 1MB, 1)
            AggregatePrivateMiB = [math]::Round($private / 1MB, 1)
            AggregateHandles = $handles
            NormalizedCpuPercent = $cpuPct
            HostFreeMiB = $free
        })
    }
    $directory = Split-Path -Parent $Output
    Assert-QAOutputPathHasNoReparseAncestors
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    Assert-QAOutputPathHasNoReparseAncestors
    $rows | Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding UTF8 -NoClobber
    $first = $rows[0]; $last = $rows[$rows.Count - 1]
    # Delay all success markers until the QA-owned process cleanup has succeeded.
    $summary = [pscustomobject]@{
        Samples = $rows.Count
        DurationSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
        PrivateDeltaMiB = [math]::Round($last.AggregatePrivateMiB - $first.AggregatePrivateMiB, 1)
        HandlesDelta = $last.AggregateHandles - $first.AggregateHandles
    }
} catch {
    $originalFailure = $_
    # An aborted run is not certified. Preserve any samples already collected
    # without replacing earlier evidence or suppressing the original failure.
    if ($rows.Count -gt 0) {
        try {
            $directory = Split-Path -Parent $Output
            Assert-QAOutputPathHasNoReparseAncestors
            if (-not (Test-Path -LiteralPath $directory)) {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
            }
            Assert-QAOutputPathHasNoReparseAncestors
            $rows | Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding UTF8 -NoClobber
            "QA_SUSTAINED_PARTIAL_CSV=$Output"
            "QA_SUSTAINED_PARTIAL_SAMPLES=$($rows.Count)"
            'QA_SUSTAINED_PARTIAL_NOT_CERTIFIED: observation interrupted; do not claim stability.'
        } catch {
            Write-Warning "QA_SUSTAINED_PARTIAL_EXPORT_FAILED: $($_.Exception.Message)"
        }
    }
    throw $originalFailure
} finally {
    if ($null -ne $ownedPid) {
        try {
            $ownedNow = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
            if ($ownedNow -and $ownedNow.StartTime -eq $ownedStart) {
                Stop-Process -Id $ownedPid -ErrorAction Stop
                if (-not $ownedNow.WaitForExit(5000)) {
                    throw 'QA_SUSTAINED_QA_PROCESS_STOP_UNCONFIRMED'
                }
                "QA_ONLY_PROCESS_STOPPED=$ownedPid"
            }
        } catch {
            if ($null -ne $originalFailure) {
                Write-Warning "QA_SUSTAINED_QA_PROCESS_STOP_FAILED: $($_.Exception.Message); original failure preserved; QA-owned process may remain."
            } else {
                throw
            }
        }
    }
}
# No success output is emitted when cleanup throws or the observation was interrupted.
if ($null -ne $summary) {
    "QA_SUSTAINED_OBSERVED samples=$($summary.Samples) intervalSeconds=$IntervalSeconds durationSeconds=$($summary.DurationSeconds)"
    "QA_SUSTAINED_PRIVATE_DELTA_MIB=$($summary.PrivateDeltaMiB)"
    "QA_SUSTAINED_HANDLES_DELTA=$($summary.HandlesDelta)"
    "QA_SUSTAINED_CSV=$Output"
    'QA_SUSTAINED_LIMITS: minimized idle observation only; aggregate working sets share pages; no interaction, no load stress, no leak-free claim.'
}
