# Save a QA-owned bitmap as an immutable, uniquely named evidence file.
# FileMode.CreateNew prevents replacement even if another QA run races this one.
function Save-QAScreenshot {
    param(
        [Parameter(Mandatory=$true)][System.Drawing.Bitmap]$Bitmap,
        [Parameter(Mandatory=$true)][string]$Directory,
        [Parameter(Mandatory=$true)][string]$Prefix
    )
    if (-not [IO.Directory]::Exists($Directory)) {
        throw "QA_CAPTURE_DIRECTORY_MISSING: $Directory"
    }
    if ($Prefix -notmatch '^[a-zA-Z0-9_-]+$') {
        throw 'QA_CAPTURE_INVALID_PREFIX'
    }
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $name = '{0}-{1}-{2}.png' -f $Prefix, (Get-Date -Format 'yyyyMMdd-HHmmss-fff'), [guid]::NewGuid().ToString('N').Substring(0, 12)
        $out = Join-Path $Directory $name
        try {
            $stream = [IO.File]::Open($out, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        } catch [IO.IOException] {
            if (Test-Path -LiteralPath $out) { continue }
            throw
        }
        try {
            $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $stream.Dispose()
        }
        return $out
    }
    throw 'QA_CAPTURE_UNIQUE_NAME_EXHAUSTED'
}
