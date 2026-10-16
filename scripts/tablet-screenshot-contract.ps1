# Non-destructive regression: QA captures must never overwrite prior evidence.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$qaSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tablet-display-qa.ps1'))
if (-not $qaSource.Contains('. (Join-Path $PSScriptRoot ''qa-screenshot.ps1'')') -or
    -not $qaSource.Contains('Save-QAScreenshot') -or
    $qaSource.Contains('tablet-spacedesk-live.png') -or
    $qaSource.Contains('tablet-spacedesk-developer-live.png')) {
    throw 'TABLET_SCREENSHOT_CONTRACT_FAIL QA still risks overwriting a prior screenshot'
}
. (Join-Path $PSScriptRoot 'qa-screenshot.ps1')
Add-Type -AssemblyName System.Drawing
$dir = Join-Path $env:TEMP ('wpcc-capture-contract-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($dir) | Out-Null
$original = Join-Path $dir 'tablet-spacedesk-live.png'
$sentinel = [byte[]](1, 2, 3, 4)
[IO.File]::WriteAllBytes($original, $sentinel)
$bmp = [Drawing.Bitmap]::new(2, 2)
try {
    $one = Save-QAScreenshot -Bitmap $bmp -Directory $dir -Prefix 'tablet-spacedesk-live'
    $two = Save-QAScreenshot -Bitmap $bmp -Directory $dir -Prefix 'tablet-spacedesk-live'
    if ($one -eq $two -or $one -eq $original -or $two -eq $original) {
        throw 'TABLET_SCREENSHOT_CONTRACT_FAIL capture path was reused'
    }
    if (-not (Test-Path -LiteralPath $one) -or -not (Test-Path -LiteralPath $two)) {
        throw 'TABLET_SCREENSHOT_CONTRACT_FAIL capture missing'
    }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($original)) -ne [Convert]::ToBase64String($sentinel)) {
        throw 'TABLET_SCREENSHOT_CONTRACT_FAIL existing evidence modified'
    }
    Write-Output 'TABLET_SCREENSHOT_CONTRACT_OK two unique captures; existing evidence unchanged'
} finally {
    $bmp.Dispose()
    [IO.Directory]::Delete($dir, $true)
}
