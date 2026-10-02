param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root "src\App\Win11PerformanceControlCenter.App.csproj"
$solution = Join-Path $root "Win11PerformanceControlCenter.slnx"
$tests = Join-Path $root "tests\App.Tests\App.Tests.csproj"
$output = Join-Path $root "artifacts\local-win-x64"
$staging = Join-Path $root "artifacts\local-win-x64.staging"
$zip = Join-Path $root "artifacts\Win11PerformanceControlCenter-win-x64.zip"

Push-Location $root
try {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw "Node.js no está disponible en PATH."
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK no está disponible en PATH."
    }

    if (-not (Test-Path "node_modules\typescript\bin\tsc")) {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci falló." }
    }

    npm run build:demo
    if ($LASTEXITCODE -ne 0) { throw "El frontend/demo no compiló." }

    dotnet restore $solution --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore falló." }

    dotnet build $solution -c Release --no-restore --nologo --verbosity minimal -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Release build falló." }

    if (-not $SkipTests) {
        dotnet test $tests -c Release --no-build --nologo --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Los tests fallaron." }
    }

    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish $app -c Release -r win-x64 --self-contained false --nologo -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló." }

    # Keep the distributable runtime-only: symbols and NuGet XML docs are
    # development artifacts, not application dependencies.
    Get-ChildItem $staging -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Extension -eq ".pdb" -or
            ($_.Extension -eq ".xml" -and
             $_.Name.StartsWith("Microsoft.Web.WebView2.", [StringComparison]::OrdinalIgnoreCase))
        } |
        Remove-Item -Force -ErrorAction Stop

    Get-Process Win11PerformanceControlCenter -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400

    if (Test-Path $output) {
        Remove-Item $output -Recurse -Force -ErrorAction Stop
    }

    Move-Item $staging $output -ErrorAction Stop

    $required = @(
        "Win11PerformanceControlCenter.exe",
        "Win11PerformanceControlCenter.dll",
        "Win11PerformanceControlCenter.deps.json",
        "Win11PerformanceControlCenter.runtimeconfig.json",
        "web\index.html",
        "web\app.js",
        "web\style.css"
    )
    foreach ($relative in $required) {
        if (-not (Test-Path (Join-Path $output $relative))) {
            throw "Paquete inválido: falta $relative en la raíz de distribución."
        }
    }

    if (Test-Path (Join-Path $output "local-win-x64.staging")) {
        throw "Paquete inválido: staging anidado dentro del artefacto final."
    }

    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -CompressionLevel Optimal

    $files = Get-ChildItem $output -Recurse -File
    $bytes = ($files | Measure-Object Length -Sum).Sum
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash

    Write-Host ("PACKAGE_MB={0:N2}" -f ($bytes / 1MB))
    Write-Host ("PACKAGE_FILES={0}" -f $files.Count)
    Write-Host ("ZIP_MB={0:N2}" -f ((Get-Item $zip).Length / 1MB))
    Write-Host ("ZIP_SHA256={0}" -f $hash)
    Write-Host "BUILD_LOCAL=PASS"
}
finally {
    Pop-Location
}
