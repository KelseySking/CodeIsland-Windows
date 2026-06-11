# CodeIsland-Windows - create Windows installer
param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "release",
    [string]$InnoSetupCompiler,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

function Resolve-InnoSetupCompiler {
    param([string]$ExplicitPath)

    if ($ExplicitPath) {
        if (Test-Path -LiteralPath $ExplicitPath) {
            return (Resolve-Path -LiteralPath $ExplicitPath).Path
        }

        throw "Inno Setup compiler not found: $ExplicitPath"
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @()
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    }
    if ($env:ProgramFiles) {
        $candidates += Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "ISCC.exe not found. Install Inno Setup 6 or pass -InnoSetupCompiler <path>."
}

function Test-WindowsIcoFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    return $bytes.Length -ge 4 -and
        $bytes[0] -eq 0x00 -and
        $bytes[1] -eq 0x00 -and
        $bytes[2] -eq 0x01 -and
        $bytes[3] -eq 0x00
}

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$publishScript = Join-Path $projectRoot "scripts\publish-single-file.ps1"
$installerScript = Join-Path $projectRoot "installer\CodeIsland-Windows.iss"
$setupIconFile = Join-Path $projectRoot "src\CodeIsland.WpfApp\Assets\app.ico"

if (-not (Test-Path -LiteralPath $installerScript)) {
    throw "Installer script not found: $installerScript"
}

if (-not (Test-WindowsIcoFile -Path $setupIconFile)) {
    throw "Setup icon file must be a valid Windows ICO file: $setupIconFile"
}

$iscc = Resolve-InnoSetupCompiler -ExplicitPath $InnoSetupCompiler

if (-not $SkipPublish) {
    Write-Host "Publishing release artifacts..." -ForegroundColor Cyan
    & $publishScript -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$appPublish = Join-Path $projectRoot "src\CodeIsland.WpfApp\bin\Release\net8.0-windows\$Runtime\publish"
$bridgePublish = Join-Path $projectRoot "src\CodeIsland.Bridge\bin\Release\net8.0\$Runtime\publish"
$appExe = Join-Path $appPublish "CodeIsland-Windows.exe"
$bridgeExe = Join-Path $bridgePublish "CodeIsland.Bridge.exe"

if (-not (Test-Path $appExe)) {
    throw "App executable not found: $appExe"
}

if (-not (Test-Path $bridgeExe)) {
    throw "Bridge executable not found: $bridgeExe"
}

$stagingDir = Join-Path $projectRoot ".installer-staging"
$outputPath = Join-Path $projectRoot $OutputDir

try {
    if (Test-Path $stagingDir) {
        Remove-Item $stagingDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stagingDir | Out-Null

    Write-Host "Copying full publish outputs to installer staging directory..." -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $appPublish "*") -Destination $stagingDir -Recurse -Force
    Copy-Item -Path (Join-Path $bridgePublish "*") -Destination $stagingDir -Recurse -Force

    if (-not (Test-Path $outputPath)) {
        New-Item -ItemType Directory -Path $outputPath | Out-Null
    }

    $versionInfo = (Get-Item (Join-Path $stagingDir "CodeIsland-Windows.exe")).VersionInfo
    $version = $versionInfo.ProductVersion
    if (-not $version) { $version = $versionInfo.FileVersion }
    if (-not $version) { $version = "0.0.0" }

    $setupBaseName = "CodeIsland-Windows-Setup-v$version"
    Write-Host "Creating $setupBaseName.exe..." -ForegroundColor Cyan

    & $iscc "/DSourceDir=$stagingDir" "/DAppVersion=$version" "/DSetupIconFile=$setupIconFile" "/O$outputPath" "/F$setupBaseName" $installerScript
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $installerPath = Join-Path $outputPath "$setupBaseName.exe"
    if (-not (Test-Path $installerPath)) {
        throw "Installer output not found: $installerPath"
    }

    Write-Host "Installer created: $installerPath" -ForegroundColor Green
}
finally {
    if (Test-Path $stagingDir) {
        Remove-Item $stagingDir -Recurse -Force
    }
}
