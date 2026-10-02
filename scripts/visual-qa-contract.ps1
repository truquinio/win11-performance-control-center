$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dummy = Join-Path $env:TEMP ("wpcc-visual-contract-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $dummy | Out-Null
Set-Content (Join-Path $dummy "index.html") "<html>WRONG SERVER</html>" -Encoding utf8

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$occupier = Start-Process python -ArgumentList @("-m","http.server",[string]$port,"--bind","127.0.0.1") -WorkingDirectory $dummy -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 500
    $qa = Start-Process powershell -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy","Bypass",
        "-File",(Join-Path $root "scripts\visual-qa.ps1"),
        "-Port",[string]$port,
        "-Width","800",
        "-Height","600"
    ) -Wait -PassThru -WindowStyle Hidden
    if ($qa.ExitCode -eq 0) {
        throw "VISUAL_QA_CONTRACT_FAIL occupied port was accepted as a valid QA server."
    }
    Write-Host "VISUAL_QA_CONTRACT_OK occupied port fails closed"
}
finally {
    if ($occupier -and -not $occupier.HasExited) {
        Stop-Process -Id $occupier.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $dummy -Recurse -Force -ErrorAction SilentlyContinue
}
