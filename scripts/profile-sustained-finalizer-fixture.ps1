# Synthetic test of the actual profiler finalizer. Never launches or stops a real process.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'profile-sustained-qa.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) { throw 'FINALIZER_FIXTURE_PARSE_FAILED' }
$matches = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and
    $null -ne $node.Finally -and
    $node.Extent.Text.Contains('QA_SUSTAINED_PARTIAL_CSV')
}, $true))
if ($matches.Count -ne 1) { throw "FINALIZER_FIXTURE_NOT_UNIQUE count=$($matches.Count)" }
$body = $matches[0].Finally.Extent.Text
$finalizer = [scriptblock]::Create($body.Substring(1, $body.Length - 2))
function Invoke-SyntheticCase {
    param([bool]$PrimaryFailure, [scriptblock]$Finalizer)
    $ownedPid = 481516
    $ownedStart = [datetime]'2020-01-01'
    $originalFailure = $null
    $script:syntheticStopCalls = 0
    function Get-Process { [pscustomobject]@{ StartTime = [datetime]'2020-01-01' } }
    function Stop-Process { $script:syntheticStopCalls++; throw 'SYNTHETIC_STOP_FAILURE' }
    $observed = $null
    $warnings = @()
    try {
        try {
            if ($PrimaryFailure) { throw 'SYNTHETIC_PRIMARY_FAILURE' }
        } catch {
            $originalFailure = $_
            throw $originalFailure
        } finally {
            $warnings = @(& $Finalizer 3>&1)
        }
    } catch {
        $observed = $_.Exception.Message
    }
    if ($script:syntheticStopCalls -ne 1) { throw 'FINALIZER_FIXTURE_STOP_NOT_INVOKED' }
    if ($PrimaryFailure) {
        if ($observed -notmatch 'SYNTHETIC_PRIMARY_FAILURE') { throw "FINALIZER_FIXTURE_PRIMARY_MASKED: $observed" }
        if (($warnings | Out-String) -notmatch 'QA_SUSTAINED_QA_PROCESS_STOP_FAILED') {
            throw 'FINALIZER_FIXTURE_WARNING_MISSING'
        }
    } elseif ($observed -notmatch 'SYNTHETIC_STOP_FAILURE') {
        throw "FINALIZER_FIXTURE_CLEANUP_NOT_PROPAGATED: $observed"
    }
}
Invoke-SyntheticCase -PrimaryFailure $true -Finalizer $finalizer
Invoke-SyntheticCase -PrimaryFailure $false -Finalizer $finalizer
'QA_SUSTAINED_FINALIZER_FIXTURE_OK cases=2'
