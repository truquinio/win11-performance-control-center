# Read-only host assessment for a *separate* disposable Windows VM.
# Does not enable features, elevate, download, install, create VMs or boot guests.
[CmdletBinding()]
param(
    [ValidateRange(4, 32)][int]$GuestRamGiB = 6,
    [ValidateRange(32, 256)][int]$GuestDiskGiB = 64
)
$ErrorActionPreference = 'Stop'
$system = Get-CimInstance Win32_OperatingSystem
$hardware = Get-CimInstance Win32_ComputerSystem
$edition = [string]$system.Caption
$freeRamGiB = [math]::Round([double]$system.FreePhysicalMemory / 1048576, 2)
$ramGiB = [math]::Round([double]$system.TotalVisibleMemorySize / 1048576, 2)
$driveName = (Split-Path -Path $PSScriptRoot -Qualifier).TrimEnd(':')
$drive = Get-PSDrive -Name $driveName -PSProvider FileSystem
$freeDiskGiB = [math]::Round([double]$drive.Free / 1GB, 2)
$hyperVCmd = Get-Command Get-VM -ErrorAction SilentlyContinue
$virtualBoxCmd = Get-Command VBoxManage -ErrorAction SilentlyContinue
$qemuCmd = Get-Command qemu-system-x86_64 -ErrorAction SilentlyContinue
$isSandboxEdition = $edition -match 'Windows (10|11) (Pro|Enterprise|Education)'
$sandboxExe = Test-Path (Join-Path $env:WINDIR 'System32\WindowsSandbox.exe')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal] $identity).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
# Free headroom is essential: this device also hosts unrelated user projects.
$memoryHeadroomOk = $freeRamGiB -ge ($GuestRamGiB + 2)
$diskHeadroomOk = $freeDiskGiB -ge ($GuestDiskGiB + 10)
$provider = if ($hyperVCmd -and $isAdmin) { 'Hyper-V command (guest not verified)' }
    elseif ($virtualBoxCmd) { 'VirtualBox command (guest not verified)' }
    elseif ($qemuCmd) { 'QEMU command (guest not verified)' }
    elseif ($isSandboxEdition -and $sandboxExe) { 'Windows Sandbox binary (feature not verified)' }
    else { 'NONE_CONFIRMED' }
$blockers = @()
if ($provider -eq 'NONE_CONFIRMED') {
    $blockers += 'No supported local Windows VM toolchain is confirmed.'
}
if ($edition -match '\bHome\b') {
    $blockers += 'Windows Home does not include Windows Sandbox or the full Hyper-V VM management feature.'
}
if (-not $memoryHeadroomOk) {
    $blockers += 'Insufficient free RAM for a Windows guest while preserving other active workloads.'
}
if (-not $diskHeadroomOk) {
    $blockers += 'Insufficient free space on the repository drive for a disposable Windows 11 guest and snapshots.'
}
$result = [ordered]@{
    mode = 'READ_ONLY'
    status = if ($blockers.Count) { 'BLOCKED_FOR_LOCAL_VM' } else { 'REQUIRES_ISOLATED_GUEST_CONFIRMATION' }
    osEdition = $edition
    ramTotalGiB = $ramGiB
    ramFreeGiB = $freeRamGiB
    drive = $driveName
    diskFreeGiB = $freeDiskGiB
    requestedGuestRamGiB = $GuestRamGiB
    requestedGuestDiskGiB = $GuestDiskGiB
    hypervisorPresent = [bool]$hardware.HypervisorPresent
    isElevated = [bool]$isAdmin
    vmToolchain = $provider
    windowsSandboxSupportedEdition = [bool]$isSandboxEdition
    windowsSandboxBinaryPresent = [bool]$sandboxExe
    blockers = $blockers
    changesMade = $false
    recommendation = 'Use a separate authorized Windows 11 VM with a clean snapshot. Do not run Windows WRITE/UAC experiments on this production host.'
}
$result | ConvertTo-Json -Depth 4
