# Synthetic, no application launch. Executes the actual interrupted-run handler in isolation.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'profile-sustained-qa.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if (@($errors).Count) { throw 'PARTIAL_FIXTURE_SOURCE_PARSE_FAILED' }
$tryAst = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.TryStatementAst] -and
    $node.CatchClauses.Count -eq 1 -and
    $null -ne $node.Finally -and
    $node.Extent.Text.Contains('QA_SUSTAINED_PARTIAL_CSV')
}, $true))
if ($tryAst.Count -ne 1) { throw "PARTIAL_FIXTURE_HANDLER_NOT_UNIQUE count=$($tryAst.Count)" }
$body = $tryAst[0].CatchClauses[0].Body.Extent.Text
$handler = [scriptblock]::Create($body.Substring(1, $body.Length - 2))
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'PARTIAL_FIXTURE_ARTIFACTS_MISSING' }
function Test-Abort {
    param([object]$Rows, [string]$Output, [scriptblock]$Handler)
    $failure = $null
    try {
        try { throw 'SYNTHETIC_ABORT_SENTINEL' } catch { & $Handler }
    } catch { $failure = $_ }
    if ($null -eq $failure -or $failure.Exception.Message -notmatch 'SYNTHETIC_ABORT_SENTINEL') {
        throw 'PARTIAL_FIXTURE_ORIGINAL_ERROR_LOST'
    }
}
$runId = [guid]::NewGuid().ToString('N')
$rows = [Collections.Generic.List[object]]::new()
$rows.Add([pscustomobject]@{ Timestamp = 'fixture'; HostFreeMiB = 1500 })
$Output = Join-Path $root "profile-partial-fixture-$runId.csv"
Test-Abort -Rows $rows -Output $Output -Handler $handler
if (-not (Test-Path -LiteralPath $Output -PathType Leaf)) { throw 'PARTIAL_FIXTURE_CSV_MISSING' }
$actual = @(Import-Csv -LiteralPath $Output)
if ($actual.Count -ne 1 -or $actual[0].HostFreeMiB -ne '1500') { throw 'PARTIAL_FIXTURE_CSV_CONTENT_MISMATCH' }
$sentinel = Join-Path $root "profile-partial-sentinel-$runId.csv"
[IO.File]::WriteAllText($sentinel, 'PRESERVE_ORIGINAL')
$before = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
Test-Abort -Rows $rows -Output $sentinel -Handler $handler 3>$null
$after = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
if ($before -ne $after) { throw 'PARTIAL_FIXTURE_EVIDENCE_OVERWRITTEN' }
$rows.Clear()
$missing = Join-Path $root "profile-partial-zero-$runId.csv"
Test-Abort -Rows $rows -Output $missing -Handler $handler
if (Test-Path -LiteralPath $missing) { throw 'PARTIAL_FIXTURE_ZERO_ROWS_CREATED_CSV' }
"QA_SUSTAINED_PARTIAL_FIXTURE_OK csv=$Output sentinel=$sentinel cases=3"
