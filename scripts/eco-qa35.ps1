# Eco-QA 35: controlled fail-closed IPC/UAC guard tests, never executes WRITE.
# Produces immutable TRX, log, and per-action evidence under artifacts.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$catalogPath = Join-Path $root 'src\Frontend\generated\action-catalog.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$writes = @($catalog | Where-Object { $_.mode -eq 'WRITE' })
if ($writes.Count -ne 35 -or
    @($writes | Select-Object -ExpandProperty id -Unique).Count -ne 35) {
    throw 'ECO_QA_CATALOG_CHANGED: expected 35 unique WRITE IDs'
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$output = Join-Path $root ('artifacts\eco-qa35-' + $stamp + '-' + $runId)
if (Test-Path -LiteralPath $output) { throw 'ECO_QA_OUTPUT_ALREADY_EXISTS' }
New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
$project = Join-Path $root 'tests\App.Tests\App.Tests.csproj'
$trx = Join-Path $output 'eco-qa35.trx'
$log = Join-Path $output 'eco-qa35.log'
Push-Location $root
try {
    & dotnet test $project -c Release --no-restore --filter 'FullyQualifiedName~EcoQa35InterlockTests' --verbosity quiet --logger 'trx;LogFileName=eco-qa35.trx' --results-directory $output > $log 2>&1
    $exitCode = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $trx -PathType Leaf)) {
    throw ('ECO_QA_TEST_FAILED exit=' + $exitCode + ' log=' + $log)
}
[xml]$data = Get-Content -LiteralPath $trx -Raw -Encoding UTF8
$results = @($data.SelectNodes('//*[local-name()="UnitTestResult"]'))
$checks = @(
    'RefusedWrite_NeverStartsOrElevates',
    'MissingConsent_NeverStartsOrElevates',
    'InvalidConsentType_NeverStartsOrElevates',
    'UnexpectedParameter_NeverStartsOrElevates'
)
if ($results.Count -ne 141 -or
    @($results | Where-Object { $_.GetAttribute('outcome') -ne 'Passed' }).Count -gt 0) {
    throw ('ECO_QA_RESULT_COUNT_OR_OUTCOME_INVALID: ' + $results.Count)
}
$scenarios = foreach ($action in $writes) {
    $id = [string]$action.id
    $checksResult = [ordered]@{}
    foreach ($check in $checks) {
        $found = @($results | Where-Object {
            $_.GetAttribute('testName').Contains($check) -and
            $_.GetAttribute('testName').Contains($id)
        })
        if ($found.Count -ne 1 -or $found[0].GetAttribute('outcome') -ne 'Passed') {
            throw ('ECO_QA_SCENARIO_MISSING_OR_DUPLICATE ' + $id + ' ' + $check)
        }
        $checksResult[$check] = 'PASS'
    }
    [ordered]@{
        id = $id
        risk = [string]$action.risk
        requiresAdmin = [bool]$action.requiresAdmin
        classification = 'QA_NO_DESTRUCTIVA_APROBADA'
        scope = 'IPC_CONSENT_GUARD_ONLY'
        checks = $checksResult
        positivePhysicalExecution = 'NOT_TESTED'
        realUacAndRollback = 'NOT_CERTIFIED'
    }
}
if (@($scenarios).Count -ne 35) {
    throw 'ECO_QA_COVERAGE_INCOMPLETE'
}
$report = [ordered]@{
    schema = 'wpcc-eco-qa35-consent-v1'
    timestamp = (Get-Date).ToString('o')
    source = 'ActionCatalog generated JSON; xUnit TRX evidence'
    status = 'QA_NO_DESTRUCTIVA_APROBADA'
    meaning = '35/35 WRITE have 4 rejection guards each. This is not positive operation, privileged/UAC, or physical rollback certification.'
    catalogSha256 = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash
    trxSha256 = (Get-FileHash -LiteralPath $trx -Algorithm SHA256).Hash
    logSha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash
    writeActions = 35
    rejectionTests = 140
    inventoryTests = 1
    testFailures = 0
    realWindowsWrites = 0
    actions = @($scenarios)
}
$reportPath = Join-Path $output 'eco-qa35-manifest.json'
$report | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $reportPath -Encoding utf8 -NoClobber
'ECO_QA35_GUARDS_OK actions=35 negativeCases=140 inventory=1 writes=0'
'ECO_QA35_REPORT=' + $reportPath
'ECO_QA35_REPORT_SHA256=' + (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash
'ECO_QA35_LIMITATION=NO_POSITIVE_WINDOWS_WRITES_CERTIFIED'
