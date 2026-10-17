# Generates a pending isolated-Windows test manifest from ONE authoritative catalog.
# No WRITE execution, VM boot, process control, downloads or user-data access.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$catalogPath = Join-Path $PSScriptRoot '..\src\Frontend\generated\action-catalog.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$unique = @($catalog | Select-Object -ExpandProperty id -Unique)
if ($unique.Count -ne $catalog.Count) { throw 'ACTION_CATALOG_DUPLICATE_IDS' }
$writes = @($catalog | Where-Object { $_.mode -eq 'WRITE' })
if ($writes.Count -eq 0) { throw 'ACTION_CATALOG_NO_WRITE_ACTIONS' }
$cases = foreach ($a in $writes) {
    [ordered]@{
        id = [string]$a.id
        risk = [string]$a.risk
        requiresAdmin = [bool]$a.requiresAdmin
        reversibleClaim = [bool]$a.reversible
        state = 'PENDING_VM'
        uacDenied = 'NOT_TESTED'
        confirmationDenied = 'NOT_TESTED'
        postconditionVerified = 'NOT_TESTED'
        interruptionRecovered = 'NOT_TESTED'
        rollbackVerified = 'NOT_TESTED'
        snapshotRestored = 'NOT_TESTED'
        evidence = @()
    }
}
[ordered]@{
    schema = 'wpcc-vm-write-certification-v1'
    derivedFrom = 'src/Frontend/generated/action-catalog.json (generated from ActionCatalog.All)'
    status = 'NO_REAL_WRITE_ACTIONS_TESTED'
    actionCount = $writes.Count
    coverage = @($cases)
} | ConvertTo-Json -Depth 5
