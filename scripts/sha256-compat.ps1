# Read-only SHA-256 helper, independent of PowerShell module auto-loading.
# Intended for the release evidence gate after file ownership is reconciled.
function Get-WpccSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $stream = $null
    $hasher = $null
    try {
        $stream = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($LiteralPath))
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        return [System.BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        if ($null -ne $hasher) { $hasher.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}
