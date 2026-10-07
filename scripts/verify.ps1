param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = "C:\\Program Files\\dotnet\\dotnet.exe"

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Command
    )

    Write-Host ""
    Write-Host "=== $Name ==="
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

Set-Location $root

Invoke-Step "Frontend" { npm run build:frontend }

Invoke-Step "Frontend contract" { npm run test:frontend-contract }

Invoke-Step "UI smoke" { npm run test:ui-smoke }

Invoke-Step "Real-world eval manifest contract" { npm run test:real-world-evals }

Invoke-Step "Package safety contract" { npm run test:package-contract }

Invoke-Step "Resource safety contract" { npm run test:resource-contract }

Invoke-Step "CI parity contract" { npm run test:ci-contract }

Invoke-Step "WebView security contract" { npm run test:webview-security }

Invoke-Step "Platform compatibility contract" { npm run test:platform-contract }

Invoke-Step "Visual QA isolation contract" {
    powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\visual-qa-contract.ps1"
}

Invoke-Step "Release build" {
    & $dotnet build ".\\Win11PerformanceControlCenter.slnx" -c $Configuration --nologo --verbosity minimal -warnaserror
}

Invoke-Step "Real-world evals" {
    & $dotnet test ".\\tests\\App.Tests\\App.Tests.csproj" -c $Configuration --no-build --nologo --verbosity minimal --filter "Layer=RealWorldEval"
}

Invoke-Step "Tests" {
    & $dotnet test ".\\tests\\App.Tests\\App.Tests.csproj" -c $Configuration --no-build --nologo --verbosity minimal
}

Invoke-Step "Whitespace format" {
    & $dotnet format ".\\Win11PerformanceControlCenter.slnx" whitespace --verify-no-changes --no-restore
}

Invoke-Step "Code-style gate" {
    & $dotnet format ".\\Win11PerformanceControlCenter.slnx" style --verify-no-changes --severity warn --no-restore
}

Invoke-Step "Analyzer gate" {
    & $dotnet format ".\\Win11PerformanceControlCenter.slnx" analyzers --verify-no-changes --severity warn --no-restore
}

Invoke-Step "NuGet vulnerability audit" {
    & $dotnet list ".\\Win11PerformanceControlCenter.slnx" package --vulnerable --include-transitive
}

Invoke-Step "NPM production audit" { npm audit --omit=dev }

Write-Host ""
Write-Host "VERIFY_OK"
