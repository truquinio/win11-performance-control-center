# Synthetic finalizer contract: success must mean the QA-owned process actually exited.
# No real process is launched or stopped; all process APIs are locally mocked.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'profile-sustained-qa.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) { throw 'STOP_CONFIRMATION_PARSE_FAILED' }
$matches = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and
    $null -ne $node.Finally -and
    $node.Extent.Text.Contains('QA_SUSTAINED_PARTIAL_CSV')
}, $true))
if ($matches.Count -ne 1) { throw "STOP_CONFIRMATION_FINALIZER_NOT_UNIQUE count=$($matches.Count)" }
$body = $matches[0].Finally.Extent.Text
$finalizer = [scriptblock]::Create($body.Substring(1, $body.Length - 2))
function Invoke-SyntheticCase {
    param([bool]$Exits, [scriptblock]$Finalizer)
    $ownedPid = 481516
    $ownedStart = [datetime]'2020-01-01'
    $originalFailure = $null
    $script:waitCalled = 0
    $script:stopCalled = 0
    $script:exits = $Exits
    $fake = [pscustomobject]@{ StartTime = $ownedStart }
    $fake | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param([int]$timeoutMs)
        $script:waitCalled++
        return $script:exits
    }
    function Get-Process { return $fake }
    function Stop-Process { $script:stopCalled++ }
    $observed = $null
    $output = @()
    try { $output = @(& $Finalizer) } catch { $observed = $_.Exception.Message }
    if ($script:stopCalled -ne 1 -or $script:waitCalled -ne 1) {
        throw "STOP_CONFIRMATION_CALLS_WRONG stop=$($script:stopCalled) wait=$($script:waitCalled)"
    }
    if ($Exits) {
        if ($null -ne $observed -or ($output -join ' ') -notmatch 'QA_ONLY_PROCESS_STOPPED') {
            throw "STOP_CONFIRMATION_SUCCESS_NOT_CONFIRMED error=$observed"
        }
    } else {
        if ($observed -notmatch 'QA_SUSTAINED_QA_PROCESS_STOP_UNCONFIRMED' -or ($output -join ' ') -match 'QA_ONLY_PROCESS_STOPPED') {
            throw "STOP_CONFIRMATION_FALSE_SUCCESS error=$observed output=$($output -join ' ')"
        }
    }
}
Invoke-SyntheticCase -Exits $true -Finalizer $finalizer
Invoke-SyntheticCase -Exits $false -Finalizer $finalizer
'QA_SUSTAINED_STOP_CONFIRMATION_FIXTURE_OK cases=2'
