# Regression: reject a junction in the QA evidence path before launching any executable.
# Creates and removes only uniquely named, empty fixture directories under this repo's artifacts.
$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$fixture = Join-Path $artifacts ('qa-reparse-fixture-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $fixture 'target'
$link = Join-Path $fixture 'junction'
$testOutput = Join-Path $link 'must-not-be-written.csv'
$createdLink = $false
try {
    [void][IO.Directory]::CreateDirectory($target)
    New-Item -ItemType Junction -Path $link -Target $target -ErrorAction Stop | Out-Null
    $createdLink = $true
    if (-not ((Get-Item -LiteralPath $link -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'QA_REPARSE_FIXTURE_INVALID: expected a junction.'
    }
    $failure = $null
    try {
        & (Join-Path $PSScriptRoot 'profile-sustained-qa.ps1') -Executable (Join-Path $fixture 'never-start.exe') -Output $testOutput -Samples 3 -IntervalSeconds 2
    } catch {
        $failure = $_.Exception.Message
    }
    if ($failure -notmatch 'QA_SUSTAINED_OUTPUT_REPARSE_POINT') {
        throw ("QA_REPARSE_CONTRACT_FAIL: expected rejection before executable lookup; observed: " + $failure)
    }
    if (Test-Path -LiteralPath (Join-Path $target 'must-not-be-written.csv')) {
        throw 'QA_REPARSE_CONTRACT_FAIL: output escaped via junction.'
    }
    'QA_SUSTAINED_REPARSE_CONTRACT_OK'
} finally {
    # Directory.Delete(link,false) removes the junction itself, never traverses the target.
    if ($createdLink -and [IO.Directory]::Exists($link)) {
        [IO.Directory]::Delete($link, $false)
    }
    if ([IO.Directory]::Exists($target)) { [IO.Directory]::Delete($target, $false) }
    if ([IO.Directory]::Exists($fixture)) { [IO.Directory]::Delete($fixture, $false) }
}
