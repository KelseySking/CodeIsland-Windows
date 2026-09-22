# CodeIsland-Windows - pre_install cleanup for legacy Inno Setup installs.
#
# Runs before every scoop install/update. Windows has no flock, so we also stop
# our own running processes here (scoop's own running-process check only looks at
# the current version dir, which does not cover the shim -> ...\current path).
#
# MUST be idempotent and MUST NOT abort the install on a missing target: scoop
# runs this via Invoke-Command, where a terminating error aborts the whole install.
#
# -DryRun is for local verification only. scripts/scoop/update-manifest.ps1 strips
# the param() block when inlining this file into bucket/codeisland.json.
param(
    [switch]$DryRun
)

# NOT "Stop": a cleanup failure must not abort scoop install.
$ErrorActionPreference = 'Continue'

# Default false: the inlined copy has its param() block stripped, so it must
# not depend on the parameter existing. -DryRun sets it to true.
if ($null -eq $DryRun) { $DryRun = $false }

$LegacyInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\CodeIsland-Windows'
# AppGuid from the removed installer/CodeIsland-Windows.iss
$LegacyUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B8A0E8F8-36A9-44B9-BD1A-E81EBC8E58C9}_is1'
$RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValueName = 'CodeIsland'
$ScoopAppsDir = Join-Path $HOME 'scoop\apps'

# Covers the iss CloseApplicationsFilter plus the current runtime names.
$ProcessNames = @(
    'CodeIsland-Windows',
    'codeorbit-host',
    'codeorbit-bridge',
    'CodeIsland.Bridge',
    'CodeIsland.RuntimeHost',
    'CodeOrbit.Bridge',
    'CodeOrbit.RuntimeHost'
)

function Invoke-Step {
    param([string]$Description, [scriptblock]$Action)

    if ($DryRun) {
        Write-Host "[dry-run] $Description"
        return
    }

    try {
        $Action.Invoke()
    }
    catch {
        Write-Warning "CodeIsland migration: $Description failed - $($_.Exception.Message)"
    }
}

# 1. Stop leftover processes. Filtered by path so a separately deployed CodeOrbit
#    (not managed by this app) is not killed.
Invoke-Step 'stop running CodeIsland / CodeOrbit processes' {
    $stopped = @()
    foreach ($name in $ProcessNames) {
        foreach ($proc in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            $path = $null
            try { $path = $proc.Path } catch { }
            # Empty path (elevated process we cannot inspect) is treated as ours.
            if ($path -and
                -not $path.StartsWith($LegacyInstallDir, [StringComparison]::OrdinalIgnoreCase) -and
                -not $path.StartsWith($ScoopAppsDir, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            $stopped += "$name($($proc.Id))"
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
    if ($stopped.Count -gt 0) { Write-Host "Stopped: $($stopped -join ', ')" }
}

# 2. Legacy install directory
Invoke-Step "remove '$LegacyInstallDir'" {
    if (Test-Path -LiteralPath $LegacyInstallDir) { Remove-Item -LiteralPath $LegacyInstallDir -Recurse -Force }
}

# 3. Legacy uninstall registry entry ("Apps & features" row)
Invoke-Step 'remove legacy uninstall registry key' {
    if (Test-Path -LiteralPath $LegacyUninstallKey) { Remove-Item -LiteralPath $LegacyUninstallKey -Recurse -Force }
}

# 4. Startup entry pointing at the removed exe.
#    Deliberately not recreated: the user re-enables it from the settings page.
Invoke-Step 'remove legacy HKCU Run\CodeIsland startup entry' {
    if (Get-ItemProperty -Path $RunKeyPath -Name $RunValueName -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $RunKeyPath -Name $RunValueName -Force
    }
}

# 5. Legacy shortcuts
Invoke-Step 'remove legacy start menu folder' {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\CodeIsland-Windows'
    if (Test-Path -LiteralPath $startMenu) { Remove-Item -LiteralPath $startMenu -Recurse -Force }
}
Invoke-Step 'remove legacy desktop shortcut' {
    $desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CodeIsland-Windows.lnk'
    if (Test-Path -LiteralPath $desktopLink) { Remove-Item -LiteralPath $desktopLink -Force }
}

# Intentionally NOT touched (user data, independent of install location).
# Removing either would break already-installed hooks:
#   %APPDATA%\CodeIsland                  - settings, logs
#   %LOCALAPPDATA%\CodeIsland\runtime     - managed CodeOrbit; hooks point at
#                                           runtime\current\codeorbit-bridge.exe

if ($DryRun) { Write-Host '[dry-run] no changes made' }
