# CodeIsland-Windows - sync bundled CodeOrbit Runtime from GitHub releases
# Default: pin file (external/CodeOrbit/runtime-pin.json). Use -Latest only when intentionally floating.
param(
    [string]$Version,
    [switch]$Latest,
    [string]$Repo,
    [string]$OutDir,
    [switch]$SkipWritePin
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) {
    $OutDir = Join-Path $projectRoot "external\CodeOrbit"
}
$pinPath = Join-Path $OutDir "runtime-pin.json"

function Read-Pin {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{
            repo = "KelseySking/CodeOrbit-Rust"
            tag = "v0.1.2"
            assetNamePattern = "CodeOrbit-Rust-*-windows-x64.zip"
        }
    }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Write-Pin {
    param([string]$Path, [string]$RepoName, [string]$Tag)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $obj = [ordered]@{
        repo = $RepoName
        tag = $Tag
        assetNamePattern = "CodeOrbit-Rust-*-windows-x64.zip"
    }
    ($obj | ConvertTo-Json) | Set-Content -LiteralPath $Path -Encoding utf8
}

function Get-LatestTag {
    param([string]$RepoName)
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $tag = gh release view -R $RepoName --json tagName --jq .tagName 2>$null
        if ($LASTEXITCODE -eq 0 -and $tag) { return $tag.Trim() }
    }
    $api = "https://api.github.com/repos/$RepoName/releases/latest"
    $resp = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "CodeIsland-Windows-sync" }
    if (-not $resp.tag_name) { throw "Could not resolve latest release tag for $RepoName" }
    return [string]$resp.tag_name
}

function Find-AssetName {
    param([string]$RepoName, [string]$Tag)
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $names = gh release view $Tag -R $RepoName --json assets --jq ".assets[].name" 2>$null
        if ($LASTEXITCODE -eq 0 -and $names) {
            $match = $names | Where-Object { $_ -like "*windows-x64.zip" } | Select-Object -First 1
            if ($match) { return $match.Trim() }
        }
    }
    $api = "https://api.github.com/repos/$RepoName/releases/tags/$Tag"
    $resp = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "CodeIsland-Windows-sync" }
    $asset = $resp.assets | Where-Object { $_.name -like "*windows-x64.zip" } | Select-Object -First 1
    if (-not $asset) { throw "No windows-x64.zip asset on $RepoName $Tag" }
    return [string]$asset.name
}

function Download-ReleaseZip {
    param([string]$RepoName, [string]$Tag, [string]$AssetName, [string]$DestZip)
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $dir = Split-Path -Parent $DestZip
        gh release download $Tag -R $RepoName -p $AssetName -D $dir --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release download failed for $Tag" }
        $downloaded = Join-Path $dir $AssetName
        if (Test-Path -LiteralPath $downloaded) {
            if ($downloaded -ne $DestZip) {
                Move-Item -LiteralPath $downloaded -Destination $DestZip -Force
            }
            return
        }
    }
    $url = "https://github.com/$RepoName/releases/download/$Tag/$AssetName"
    Write-Host "Downloading $url ..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $DestZip -UseBasicParsing
}

function Get-RuntimeRoot {
    param([string]$ExtractDir)
    $manifest = Get-ChildItem -LiteralPath $ExtractDir -Filter "runtime-manifest.json" -Recurse -File | Select-Object -First 1
    if ($manifest) {
        return $manifest.Directory.FullName
    }
    $hostExe = Get-ChildItem -LiteralPath $ExtractDir -Filter "codeorbit-host.exe" -Recurse -File | Select-Object -First 1
    if ($hostExe) {
        return $hostExe.Directory.FullName
    }
    throw "Extracted zip does not contain runtime-manifest.json or codeorbit-host.exe"
}

function Assert-RuntimeLayout {
    param([string]$Dir)
    $manifestPath = Join-Path $Dir "runtime-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Missing runtime-manifest.json in $Dir"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $hostExe = [string]$manifest.hostExe
    $bridgeExe = [string]$manifest.bridgeExe
    if ([string]::IsNullOrWhiteSpace($hostExe) -or [string]::IsNullOrWhiteSpace($bridgeExe)) {
        throw "runtime-manifest.json must declare hostExe and bridgeExe"
    }
    foreach ($name in @($hostExe, $bridgeExe)) {
        $path = Join-Path $Dir $name
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Missing required Runtime file: $name"
        }
    }
    $version = $manifest.runtimeVersion
    if (-not $version) { $version = $manifest.version }
    return [pscustomobject]@{
        HostExe = $hostExe
        BridgeExe = $bridgeExe
        Version = $version
    }
}

# --- main ---
$pin = Read-Pin -Path $pinPath
if (-not $Repo) { $Repo = [string]$pin.repo }
if ([string]::IsNullOrWhiteSpace($Repo)) { $Repo = "KelseySking/CodeOrbit-Rust" }

$tag = $null
if ($Latest) {
    $tag = Get-LatestTag -RepoName $Repo
}
elseif ($Version) {
    $tag = $Version.Trim()
}
else {
    $tag = [string]$pin.tag
}
if ([string]::IsNullOrWhiteSpace($tag)) {
    throw "No Runtime tag: set pin, pass -Version, or use -Latest"
}
if ($tag -notmatch '^v') {
    # accept 0.1.2 or v0.1.2
    if ($tag -match '^\d') { $tag = "v$tag" }
}

Write-Host "Syncing CodeOrbit Runtime $tag from $Repo → $OutDir" -ForegroundColor Cyan

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("codeorbit-sync-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    $assetName = Find-AssetName -RepoName $Repo -Tag $tag
    $zipPath = Join-Path $tempRoot $assetName
    Download-ReleaseZip -RepoName $Repo -Tag $tag -AssetName $assetName -DestZip $zipPath

    $extractDir = Join-Path $tempRoot "extract"
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force
    $runtimeRoot = Get-RuntimeRoot -ExtractDir $extractDir

    if (-not (Test-Path -LiteralPath $OutDir)) {
        New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    }

    # Preserve pin path content until after copy; wipe other contents
    Get-ChildItem -LiteralPath $OutDir -Force | ForEach-Object {
        if ($_.Name -eq "runtime-pin.json") { return }
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }

    Copy-Item -Path (Join-Path $runtimeRoot "*") -Destination $OutDir -Recurse -Force

    $info = Assert-RuntimeLayout -Dir $OutDir

    if (-not $SkipWritePin) {
        Write-Pin -Path $pinPath -RepoName $Repo -Tag $tag
    }

    Write-Host ""
    Write-Host "Runtime synced." -ForegroundColor Green
    Write-Host "  tag:     $tag" -ForegroundColor Gray
    Write-Host "  version: $($info.Version)" -ForegroundColor Gray
    Write-Host "  host:    $($info.HostExe)" -ForegroundColor Gray
    Write-Host "  bridge:  $($info.BridgeExe)" -ForegroundColor Gray
    Write-Host "  out:     $OutDir" -ForegroundColor Gray
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
