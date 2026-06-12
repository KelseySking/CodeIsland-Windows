# CodeIsland - 发布单文件可执行程序
param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

function Assert-PublishFiles {
    param(
        [string]$PublishDir,
        [string[]]$RequiredFiles,
        [string]$ArtifactName
    )

    foreach ($file in $RequiredFiles) {
        $path = Join-Path $PublishDir $file
        if (-not (Test-Path -LiteralPath $path)) {
            throw "$ArtifactName publish output is missing required file: $path"
        }
    }
}

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$appPublish = Join-Path $projectRoot "src\CodeIsland.WpfApp\bin\Release\net8.0-windows\$Runtime\publish"
$bridgePublish = Join-Path $projectRoot "src\CodeIsland.Bridge\bin\Release\net8.0\$Runtime\publish"
$runtimeHostPublish = Join-Path $projectRoot "src\CodeIsland.RuntimeHost\bin\Release\net8.0\$Runtime\publish"

if (Test-Path -LiteralPath $appPublish) {
    Remove-Item $appPublish -Recurse -Force
}

Write-Host "Publishing CodeIsland-Windows WPF ($Runtime, self-contained single-file)" -ForegroundColor Cyan
dotnet publish (Join-Path $projectRoot "src\CodeIsland.WpfApp") -c Release -r $Runtime --self-contained -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish CodeIsland-Windows failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}

Assert-PublishFiles -PublishDir $appPublish -ArtifactName "CodeIsland-Windows" -RequiredFiles @(
    "CodeIsland-Windows.exe",
    "Assets\sounds\8bit_approval.wav",
    "Assets\sounds\8bit_boot.wav",
    "Assets\sounds\8bit_complete.wav",
    "Assets\sounds\8bit_error.wav",
    "Assets\sounds\8bit_start.wav",
    "Assets\sounds\8bit_submit.wav"
)

if (Test-Path -LiteralPath $bridgePublish) {
    Remove-Item $bridgePublish -Recurse -Force
}

Write-Host "Publishing CodeIsland.Bridge ($Runtime, single-file)..." -ForegroundColor Cyan
dotnet publish (Join-Path $projectRoot "src\CodeIsland.Bridge") -c Release -r $Runtime --self-contained -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish CodeIsland.Bridge failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}

Assert-PublishFiles -PublishDir $bridgePublish -ArtifactName "CodeIsland.Bridge" -RequiredFiles @(
    "CodeIsland.Bridge.exe"
)

if (Test-Path -LiteralPath $runtimeHostPublish) {
    Remove-Item $runtimeHostPublish -Recurse -Force
}

Write-Host "Publishing CodeIsland.RuntimeHost ($Runtime, single-file)..." -ForegroundColor Cyan
dotnet publish (Join-Path $projectRoot "src\CodeIsland.RuntimeHost") -c Release -r $Runtime --self-contained -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish CodeIsland.RuntimeHost failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}

Assert-PublishFiles -PublishDir $runtimeHostPublish -ArtifactName "CodeIsland.RuntimeHost" -RequiredFiles @(
    "CodeIsland.RuntimeHost.exe"
)

Write-Host "Done. Output in bin/Release/*/publish/" -ForegroundColor Green
