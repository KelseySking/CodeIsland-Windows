# CodeIsland - 构建并运行测试
param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

Write-Host "Building solution (Release)..." -ForegroundColor Cyan
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not $SkipTests) {
    Write-Host "Running tests..." -ForegroundColor Cyan
    dotnet test --no-build -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed!" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Write-Host "Done." -ForegroundColor Green
