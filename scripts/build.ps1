# CodeIsland - 构建项目
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

# 注意: 测试项目已移除（Core.Tests, Bridge.Tests, Hub.Tests）
# 未来如果添加 WpfApp 单元测试，可以重新启用此部分
if (-not $SkipTests) {
    Write-Host "Checking for test projects..." -ForegroundColor Cyan
    $testProjects = Get-ChildItem -Path tests -Filter *.csproj -Recurse -ErrorAction SilentlyContinue
    if ($testProjects) {
        Write-Host "Running tests..." -ForegroundColor Cyan
        dotnet test --no-build -c Release
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Tests failed!" -ForegroundColor Red
            exit $LASTEXITCODE
        }
    } else {
        Write-Host "No test projects found (skipping tests)." -ForegroundColor Yellow
    }
}

Write-Host "Done." -ForegroundColor Green

