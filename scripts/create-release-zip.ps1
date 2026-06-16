# CodeIsland-Windows - package publish artifacts as ZIP with CodeOrbit Runtime
param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "release",
    [string]$CodeOrbitRuntimeZip = "D:\OtherWork\CodeOrbit\release\CodeOrbit-win-x64-v1.0.0.zip"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$appPublish = Join-Path $projectRoot "src\CodeIsland.WpfApp\bin\Release\net8.0-windows\$Runtime\publish"

# Check WpfApp publish
if (-not (Test-Path $appPublish)) {
    Write-Host "App publish directory not found: $appPublish" -ForegroundColor Red
    Write-Host "Run publish-single-file.ps1 first." -ForegroundColor Yellow
    exit 1
}

# Check CodeOrbit Runtime ZIP
if (-not (Test-Path $CodeOrbitRuntimeZip)) {
    Write-Host "CodeOrbit Runtime ZIP not found: $CodeOrbitRuntimeZip" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please obtain CodeOrbit Runtime from:" -ForegroundColor Yellow
    Write-Host "  https://github.com/KelseySking/CodeOrbit" -ForegroundColor Cyan
    Write-Host "  or build it from D:\OtherWork\CodeOrbit" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Or specify a custom path with -CodeOrbitRuntimeZip parameter" -ForegroundColor Yellow
    exit 1
}

$stagingDir = Join-Path $projectRoot ".release-staging"
if (Test-Path $stagingDir) {
    Remove-Item $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir | Out-Null

Write-Host "Copying WpfApp to staging directory..." -ForegroundColor Cyan
Copy-Item -Path (Join-Path $appPublish "*") -Destination $stagingDir -Recurse -Force

Write-Host "Extracting CodeOrbit Runtime..." -ForegroundColor Cyan
$runtimeCurrentDir = Join-Path $stagingDir "runtime\current"
New-Item -ItemType Directory -Path $runtimeCurrentDir -Force | Out-Null
Expand-Archive -Path $CodeOrbitRuntimeZip -DestinationPath $runtimeCurrentDir -Force

# Verify Runtime files
$requiredRuntimeFiles = @(
    "CodeOrbit.RuntimeHost.exe",
    "CodeOrbit.Bridge.exe",
    "runtime-manifest.json"
)
foreach ($file in $requiredRuntimeFiles) {
    $path = Join-Path $runtimeCurrentDir $file
    if (-not (Test-Path $path)) {
        Write-Host "Missing required Runtime file: $file" -ForegroundColor Red
        Write-Host "The CodeOrbit Runtime ZIP may be incomplete or corrupted." -ForegroundColor Yellow
        Remove-Item $stagingDir -Recurse -Force
        exit 1
    }
}

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
