# Read-only, fail-closed evidence gate. Does NOT execute any Windows WRITE actions.
# Manual certification evidence is still subject to independent human review.
[CmdletBinding()]
param([string]$EvidenceManifest = 'artifacts\release-certification.json')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sha256-compat.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$prefix = $artifacts.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

function Resolve-EvidencePath([string]$relative) {
    if ([string]::IsNullOrWhiteSpace($relative) -or
        [IO.Path]::IsPathRooted($relative)) { throw 'Evidence path must be relative.' }
    $full = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Evidence path outside project artifacts.'
    }
    # Lexical containment is insufficient when an artifacts child is a junction/symlink.
    # Inspect each existing path component without following it for evidence hashing.
    $current = $artifacts
    $components = @($full.Substring($prefix.Length) -split '[\\/]')
    foreach ($component in @('') + $components) {
        if ($component -ne '') { $current = Join-Path $current $component }
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('Evidence path uses ReparsePoint: ' + $relative)
            }
        }
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw ('Evidence missing: ' + $relative)
    }
    return $full
}
function Assert-Evidence($record, [string]$context) {
    if ($null -eq $record -or $null -eq $record.path -or $null -eq $record.sha256) {
        throw ('Missing evidence path/hash: ' + $context)
    }
    $full = Resolve-EvidencePath ([string]$record.path)
    $sha = [string]$record.sha256
    if ($sha -notmatch '^[a-fA-F0-9]{64}$') {
        throw ('Invalid SHA256: ' + $context)
    }
    if (-not [string]::Equals((Get-WpccSha256 -LiteralPath $full),
                             $sha, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Evidence SHA256 mismatch: ' + $context)
    }
}

try {
    $catalogPath = Join-Path $root 'src\Frontend\generated\action-catalog.json'
    $catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $writes = @($catalog | Where-Object { $_.mode -eq 'WRITE' })
    if ($writes.Count -ne 35) { throw ('Unexpected WRITE count: ' + $writes.Count) }
    $ids = @($writes | ForEach-Object { [string]$_.id })
    if (@($ids | Select-Object -Unique).Count -ne $ids.Count) { throw 'Duplicate catalog IDs.' }
    $manifest = Resolve-EvidencePath $EvidenceManifest
    $data = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($data.schema -ne 'wpcc-release-physical-cert-v1') {
        throw 'PENDING_VM: physical release certification manifest absent/invalid.'
    }
    if ($data.catalogSha256 -ne (Get-WpccSha256 -LiteralPath $catalogPath)) {
        throw 'Catalog changed since certification.'
    }
    $certified = @($data.actions)
    if ($certified.Count -ne $writes.Count) {
        throw ('WRITE evidence count mismatch: ' + $certified.Count + '/' + $writes.Count)
    }
    foreach ($id in $ids) {
        $matching = @($certified | Where-Object { $_.id -ceq $id })
        if ($matching.Count -ne 1) { throw ('Missing or duplicate certification: ' + $id) }
        $a = $matching[0]
        if ($a.state -ne 'VERIFIED_IN_ISOLATED_VM') {
            throw ('PENDING_VM: ' + $id)
        }
        foreach ($check in @('postconditionVerified','confirmationDenied',
                             'snapshotRestored','interruptionRecovered')) {
            if ($a.$check -ne 'PASS') { throw ('WRITE condition not certified: ' + $id + '/' + $check) }
        }
        Assert-Evidence $a.evidence ('WRITE ' + $id)
        $source = @($writes | Where-Object { $_.id -ceq $id })[0]
        if ($source.requiresAdmin) {
            if ($a.uacGranted -ne 'PASS' -or $a.uacDenied -ne 'PASS') {
                throw ('UAC positive/denial not certified: ' + $id)
            }
            Assert-Evidence $a.uacGrantEvidence ('UAC grant ' + $id)
            Assert-Evidence $a.uacDenialEvidence ('UAC denial ' + $id)
        }
        if ($source.reversible) {
            if ($a.rollbackVerified -ne 'PASS') {
                throw ('Rollback not certified: ' + $id)
            }
            Assert-Evidence $a.rollbackEvidence ('Rollback ' + $id)
        }
    }
    # Manual release signoff must be attached to actual QA artifacts; no DEMO counts.
    foreach ($check in @(
        'VM_ISOLATION_VERIFIED', 'SNAPSHOT_RESTORED', 'UAC_VERIFIED',
        'MANUAL_SIGNOFF', 'MOCKUP_APPROVED', 'NVDA_VERIFIED',
        'HIGH_CONTRAST_VERIFIED', 'DPI_100_125_150_200',
        'LOAD_INTERACTIVE_VERIFIED', 'PACKAGE_HASH_VERIFIED'
    )) {
        $entry = $data.checks.$check
        if ($null -eq $entry -or $entry.result -ne 'PASS') {
            throw ('Physical release check not certified: ' + $check)
        }
        Assert-Evidence $entry.evidence $check
    }
    Write-Output 'RELEASE_READY_EVIDENCE_COMPLETE: requires final independent review before publishing'
    exit 0
} catch {
    Write-Output ('RELEASE_BLOCKED: ' + $_.Exception.Message)
    exit 2
}
