param(
    [int]$Width = 1648,
    [int]$Height = 927,
    [int]$Port = 8767
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$demo = Join-Path $root "artifacts\demo"
$artifactDir = Join-Path $root "artifacts\visual-qa"
New-Item -ItemType Directory -Force $artifactDir | Out-Null

Set-Location $root
npm run build:demo
if ($LASTEXITCODE -ne 0) { throw "Demo build failed." }

$edge = Get-ChildItem "$env:LOCALAPPDATA\ms-playwright" -Recurse -Filter chrome-headless-shell.exe -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $edge) {
    $edge = @(
        "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        "C:\Program Files\Microsoft\Edge\Application\msedge.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $edge) { throw "A Chromium headless browser was not found." }

$server = $null
try {
    $server = Start-Process python -ArgumentList @(
        '-m','http.server',
        [string]$Port,
        '--bind','127.0.0.1'
    ) -WorkingDirectory $demo -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 900

    foreach ($mode in @('compact','developer')) {
        $output = Join-Path $artifactDir ("$mode-$Width`x$Height.png")
        Remove-Item $output -Force -ErrorAction SilentlyContinue
        $token = [guid]::NewGuid().ToString("N")
        $tempOutput = Join-Path $env:TEMP ("wpcc-$mode-$token.png")
        $url = "http://127.0.0.1:$Port/?mode=$mode"

        $profileDir = Join-Path $env:TEMP ("wpcc-edge-profile-$token")
        $browserArgs = @(
            '--disable-gpu',
            '--disable-extensions',
            '--no-first-run',
            '--hide-scrollbars',
            "--user-data-dir=$profileDir",
            '--virtual-time-budget=2500',
            "--window-size=$Width,$Height",
            "--screenshot=$tempOutput",
            $url
        )
        $browser = Start-Process $edge -ArgumentList $browserArgs -PassThru -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds(15)
        while (-not (Test-Path $tempOutput) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 150
        }
        if ($browser -and -not $browser.HasExited) {
            Stop-Process -Id $browser.Id -Force -ErrorAction SilentlyContinue
        }
        Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue

        if (-not (Test-Path $tempOutput)) {
            throw "Visual QA capture failed for $mode after 15 seconds."
        }

        Move-Item $tempOutput $output -Force
        Write-Host $output
    }
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
