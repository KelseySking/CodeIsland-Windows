# CodeIsland-Windows - package publish artifacts as ZIP with bundled CodeOrbit Runtime
param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "release"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$appPublish = Join-Path $projectRoot "src\CodeIsland.WpfApp\bin\Release\net8.0-windows\$Runtime\publish"
$bundledRuntimeDir = Join-Path $projectRoot "external\CodeOrbit-Runtime"

# Check WpfApp publish
if (-not (Test-Path $appPublish)) {
    Write-Host "App publish directory not found: $appPublish" -ForegroundColor Red
    Write-Host "Run publish-single-file.ps1 first." -ForegroundColor Yellow
    exit 1
}

# Check bundled Runtime
if (-not (Test-Path $bundledRuntimeDir)) {
    Write-Host "Bundled CodeOrbit Runtime not found: $bundledRuntimeDir" -ForegroundColor Red
    Write-Host "The external/CodeOrbit-Runtime directory should contain Runtime files." -ForegroundColor Yellow
    exit 1
}

# Verify Runtime files
$requiredRuntimeFiles = @(
    "CodeOrbit.RuntimeHost.exe",
    "CodeOrbit.Bridge.exe",
    "runtime-manifest.json"
)
foreach ($file in $requiredRuntimeFiles) {
    $path = Join-Path $bundledRuntimeDir $file
    if (-not (Test-Path $path)) {
        Write-Host "Missing required Runtime file: $file in $bundledRuntimeDir" -ForegroundColor Red
        exit 1
    }
}

$stagingDir = Join-Path $projectRoot ".release-staging"
if (Test-Path $stagingDir) {
    Remove-Item $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir | Out-Null

Write-Host "Copying WpfApp to staging directory..." -ForegroundColor Cyan
Copy-Item -Path (Join-Path $appPublish "*") -Destination $stagingDir -Recurse -Force

Write-Host "Copying bundled CodeOrbit Runtime..." -ForegroundColor Cyan
$runtimeCurrentDir = Join-Path $stagingDir "runtime\current"
New-Item -ItemType Directory -Path $runtimeCurrentDir -Force | Out-Null
Copy-Item -Path (Join-Path $bundledRuntimeDir "*") -Destination $runtimeCurrentDir -Recurse -Force

$outputPath = Join-Path $projectRoot $OutputDir
if (-not (Test-Path $outputPath)) {
    New-Item -ItemType Directory -Path $outputPath | Out-Null
}

$stagedAppExe = Join-Path $stagingDir "CodeIsland-Windows.exe"
$versionInfo = (Get-Item -LiteralPath $stagedAppExe).VersionInfo
$version = $versionInfo.ProductVersion
if (-not $version) { $version = $versionInfo.FileVersion }
if (-not $version) { $version = "0.0.0" }

$zipName = "CodeIsland-Windows-$Runtime-v$version.zip"
$zipPath = Join-Path $outputPath $zipName

Write-Host "Creating $zipName..." -ForegroundColor Cyan
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $zipPath

Remove-Item $stagingDir -Recurse -Force

Write-Host ""
Write-Host "Release ZIP created: $zipPath" -ForegroundColor Green
Write-Host ""
Write-Host "Package contents:" -ForegroundColor Cyan
Write-Host "  - CodeIsland-Windows.exe (WPF Display Client)" -ForegroundColor Gray
Write-Host "  - runtime/current/CodeOrbit.RuntimeHost.exe" -ForegroundColor Gray
Write-Host "  - runtime/current/CodeOrbit.Bridge.exe" -ForegroundColor Gray
Write-Host "  - runtime/current/runtime-manifest.json" -ForegroundColor Gray
Write-Host "  - runtime/current/bundled-plugins/" -ForegroundColor Gray

