# CodeIsland-Windows - generate bucket/codeisland.json from a release ZIP.
#
# Computes the SHA256 of the release ZIP, writes version/url/hash, and inlines
# scripts/scoop/pre-install.ps1 (with its param() block stripped) as the
# manifest's pre_install script.
#
# Usage:
#   .\scripts\scoop\update-manifest.ps1                       # newest ZIP in release\
#   .\scripts\scoop\update-manifest.ps1 -ZipPath <path> -Version 1.4.2
#   .\scripts\scoop\update-manifest.ps1 -DryRun               # print, do not write
param(
    [string]$ZipPath,
    [string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repo = 'KelseySking/CodeIsland-Windows'
$zipRegex = '^CodeIsland-Windows-win-x64-v(?<version>\d+\.\d+\.\d+)\.zip$'

# Everything below is absolute: -File can hand us a relative command path, and
# the .NET file APIs used here need a rooted path.
$scriptDir = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$projectRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$releaseDir = Join-Path $projectRoot 'release'
$manifestPath = Join-Path $projectRoot 'bucket\codeisland.json'
$preInstallPath = Join-Path $scriptDir 'pre-install.ps1'

# Deliberately not Get-FileHash: that cmdlet is absent on the PS 5.1 build used
# to author this. .NET SHA256 is always available and version-independent.
function Get-Sha256Lower {
    param([string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
}

# Returns the full path of the newest matching release ZIP, or throws.
function Find-LatestReleaseZip {
    if (-not (Test-Path -LiteralPath $releaseDir)) {
        throw "Release directory not found: $releaseDir. Run .\scripts\create-release-zip.ps1 first."
    }

    $best = $null
    $bestVersion = $null
    foreach ($file in Get-ChildItem -LiteralPath $releaseDir -Filter '*.zip') {
        $match = [regex]::Match($file.Name, $zipRegex)
        if (-not $match.Success) { continue }

        $candidate = [version]$match.Groups['version'].Value
        if ($null -eq $bestVersion -or $candidate -gt $bestVersion) {
            $bestVersion = $candidate
            $best = $file.FullName
        }
    }

    if (-not $best) {
        throw "No ZIP matching $zipRegex found in $releaseDir. Run .\scripts\create-release-zip.ps1 first."
    }

    return $best
}

if (-not $ZipPath) {
    $ZipPath = Find-LatestReleaseZip
    Write-Host "Using newest release ZIP: $(Split-Path -Leaf $ZipPath)" -ForegroundColor Cyan
}
elseif (-not [System.IO.Path]::IsPathRooted($ZipPath)) {
    $ZipPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $ZipPath))
}

if (-not (Test-Path -LiteralPath $ZipPath)) {
    throw "Release ZIP not found: $ZipPath"
}
$ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path

$zipName = Split-Path -Leaf $ZipPath
if (-not $Version) {
    $match = [regex]::Match($zipName, $zipRegex)
    if (-not $match.Success) {
        throw "Cannot parse a version from '$zipName'. Pass -Version explicitly."
    }
    $Version = $match.Groups['version'].Value
}

# Sanity: the ZIP must actually contain the app and the bundled CodeOrbit.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName })
}
finally {
    $zip.Dispose()
}

foreach ($required in @('CodeIsland-Windows.exe', 'runtime/current/runtime-manifest.json')) {
    if ($entries -notcontains $required) {
        throw "Release ZIP is missing '$required': $ZipPath"
    }
}

$hash = Get-Sha256Lower -Path $ZipPath
$url = "https://github.com/$repo/releases/download/v$Version/CodeIsland-Windows-win-x64-v$Version.zip"

# Inline pre-install.ps1 as an array of lines: keeps the manifest diff readable
# and lets the cleanup logic stay a standalone, runnable file.
#
# Strip the param() block by range, not by pattern. A bare '^\s*\)\s*$' rule also
# matches the closing paren of any array literal declared as `= @(` - which this
# script has.
$preInstallSource = @(Get-Content -LiteralPath $preInstallPath)
$preInstallLines = @($preInstallSource)

$paramStart = -1
for ($i = 0; $i -lt $preInstallSource.Count; $i++) {
    if ($preInstallSource[$i] -match '^\s*param\s*\(') { $paramStart = $i; break }
}
if ($paramStart -lt 0) {
    throw "No param() block found in $preInstallPath. The inlined copy would still declare -DryRun."
}
$paramEnd = -1
for ($i = $paramStart; $i -lt $preInstallSource.Count; $i++) {
    if ($preInstallSource[$i] -match '^\s*\)\s*$') { $paramEnd = $i; break }
}
if ($paramEnd -lt 0) {
    throw "Unterminated param() block in $preInstallPath (opened at line $($paramStart + 1))."
}
$preInstallLines = @()
if ($paramStart -gt 0) { $preInstallLines += $preInstallSource[0..($paramStart - 1)] }
if ($paramEnd -lt $preInstallSource.Count - 1) { $preInstallLines += $preInstallSource[($paramEnd + 1)..($preInstallSource.Count - 1)] }

# The inlined copy must still parse: scoop runs it via [scriptblock]::Create,
# where a syntax error aborts the install.
$null = [scriptblock]::Create($preInstallLines -join "`n")

# Hand-rolled emitter instead of ConvertTo-Json. On PS 5.1, ConvertTo-Json
# replicates string arrays roughly 2^depth times: 108 lines at Depth 5 emitted
# 49 MB instead of ~5 KB. Depth 2 is correct here, but that is a magic constant
# nobody would sanity-check. Emitting directly has no depth semantics to get wrong.
function ConvertTo-JsonString {
    param([string]$Value)

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($ch in $Value.ToCharArray()) {
        switch ($ch) {
            '"'  { [void]$sb.Append('\"') }
            '\'  { [void]$sb.Append('\\') }
            "`b" { [void]$sb.Append('\b') }
            "`f" { [void]$sb.Append('\f') }
            "`n" { [void]$sb.Append('\n') }
            "`r" { [void]$sb.Append('\r') }
            "`t" { [void]$sb.Append('\t') }
            default {
                if ([int]$ch -lt 0x20 -or [int]$ch -gt 0x7E) {
                    [void]$sb.AppendFormat('\u{0:x4}', [int]$ch)
                }
                else {
                    [void]$sb.Append($ch)
                }
            }
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function ConvertTo-JsonLineArray {
    param([string[]]$Values, [string]$Indent)

    if ($Values.Count -eq 0) { return '[]' }

    $inner = ($Values | ForEach-Object { "$Indent    $(ConvertTo-JsonString -Value $_)" }) -join ",`n"
    return "[`n$inner`n$Indent]"
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('{')
[void]$sb.AppendLine("    `"version`": $(ConvertTo-JsonString -Value $Version),")
[void]$sb.AppendLine("    `"description`": $(ConvertTo-JsonString -Value 'Desktop HUD showing real-time status of AI coding agents (Claude Code, Codex CLI, and more)'),")
[void]$sb.AppendLine("    `"homepage`": $(ConvertTo-JsonString -Value "https://github.com/$repo"),")
[void]$sb.AppendLine("    `"license`": `"MIT`",")
[void]$sb.AppendLine("    `"url`": $(ConvertTo-JsonString -Value $url),")
[void]$sb.AppendLine("    `"hash`": $(ConvertTo-JsonString -Value $hash),")
[void]$sb.AppendLine("    `"shortcuts`": [[$(ConvertTo-JsonString -Value 'CodeIsland-Windows.exe'), $(ConvertTo-JsonString -Value 'CodeIsland')]],")
[void]$sb.AppendLine("    `"pre_install`": $(ConvertTo-JsonLineArray -Values $preInstallLines -Indent '    '),")
[void]$sb.AppendLine("    `"checkver`": { `"github`": $(ConvertTo-JsonString -Value "https://github.com/$repo") },")
[void]$sb.Append("    `"autoupdate`": { `"url`": $(ConvertTo-JsonString -Value "https://github.com/$repo/releases/download/v`$version/CodeIsland-Windows-win-x64-v`$version.zip") }")
[void]$sb.AppendLine()
[void]$sb.AppendLine('}')

$json = $sb.ToString()

if ($DryRun) {
    Write-Host "--- would write $manifestPath ---" -ForegroundColor Yellow
    Write-Host $json
    return
}

# No BOM: scoop's JSON reader and git diffs both prefer bare UTF-8.
$enc = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $json, $enc)

Write-Host "Updated $manifestPath" -ForegroundColor Green
Write-Host "  version = $Version"
Write-Host "  hash    = $hash"
Write-Host "  url     = $url"
