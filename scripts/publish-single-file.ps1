# CodeIsland - 发布单文件可执行程序
# 注意: 本脚本现在只发布 WpfApp
# CodeOrbit Runtime (host + bridge) 需要从 external\CodeOrbit 获取
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

Write-Host ""
Write-Host "Done. WpfApp output in: $appPublish" -ForegroundColor Green
Write-Host ""
Write-Host "Note: CodeOrbit Runtime (host + bridge) is bundled from:" -ForegroundColor Yellow
Write-Host "  external\CodeOrbit" -ForegroundColor Cyan
