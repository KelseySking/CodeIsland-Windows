# CodeIsland-Windows - package publish artifacts as ZIP
param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "release"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$appPublish = Join-Path $projectRoot "src\CodeIsland.WpfApp\bin\Release\net8.0-windows\$Runtime\publish"
$bridgePublish = Join-Path $projectRoot "src\CodeIsland.Bridge\bin\Release\net8.0\$Runtime\publish"
$runtimeHostPublish = Join-Path $projectRoot "src\CodeIsland.RuntimeHost\bin\Release\net8.0\$Runtime\publish"

if (-not (Test-Path $appPublish)) {
    Write-Host "App publish directory not found: $appPublish" -ForegroundColor Red
    Write-Host "Run publish-single-file.ps1 first." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $bridgePublish)) {
    Write-Host "Bridge publish directory not found: $bridgePublish" -ForegroundColor Red
    Write-Host "Run publish-single-file.ps1 first." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $runtimeHostPublish)) {
    Write-Host "Runtime host publish directory not found: $runtimeHostPublish" -ForegroundColor Red
    Write-Host "Run publish-single-file.ps1 first." -ForegroundColor Yellow
    exit 1
}

$stagingDir = Join-Path $projectRoot ".release-staging"
if (Test-Path $stagingDir) {
    Remove-Item $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir | Out-Null

Write-Host "Copying full publish outputs to staging directory..." -ForegroundColor Cyan

$appSource = Join-Path $appPublish "CodeIsland-Windows.exe"
$bridgeSource = Join-Path $bridgePublish "CodeIsland.Bridge.exe"
$runtimeHostSource = Join-Path $runtimeHostPublish "CodeIsland.RuntimeHost.exe"

if (-not (Test-Path $appSource)) {
    Write-Host "App executable not found: $appSource" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $bridgeSource)) {
    Write-Host "Bridge executable not found: $bridgeSource" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $runtimeHostSource)) {
    Write-Host "Runtime host executable not found: $runtimeHostSource" -ForegroundColor Red
    exit 1
}

Copy-Item -Path (Join-Path $appPublish "*") -Destination $stagingDir -Recurse -Force
Copy-Item -Path (Join-Path $bridgePublish "*") -Destination $stagingDir -Recurse -Force
Copy-Item -Path (Join-Path $runtimeHostPublish "*") -Destination $stagingDir -Recurse -Force

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

Write-Host "Release ZIP created: $zipPath" -ForegroundColor Green
