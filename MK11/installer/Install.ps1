<#
    Installs MK11 Tweaks (F1 offline cheat panel) for Mortal Kombat 11.

    - Finds the game through Steam (or use -GameDir "D:\path\to\Mortal Kombat 11").
    - Copies MK11Tweaks.asi into Binaries\Retail, plus the Ultimate ASI Loader (dinput8.dll) if no
      dinput8.dll is there yet.

    The mod files are taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after MK11Tweaks\build.cmd).

    -Uninstall removes MK11Tweaks.asi (and with -RemoveConfig its .ini/.log). dinput8.dll is left
    in place because other ASI mods may use it.
#>
param(
    [string]$GameDir,
    [switch]$Uninstall,
    [switch]$RemoveConfig
)

$ErrorActionPreference = 'Stop'

$AppId          = '976310'
$GameFolderName = 'Mortal Kombat 11'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Note($msg) { Write-Host "    $msg" }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

function Test-GameDir($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'Binaries\Retail\MK11.exe'))
}

function Find-GameDir {
    $steamRoots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($item.SteamPath)   { $steamRoots += $item.SteamPath }
            if ($item.InstallPath) { $steamRoots += $item.InstallPath }
        } catch {}
    }
    $libraries = @()
    foreach ($root in ($steamRoots | Select-Object -Unique)) {
        $root = $root -replace '/', '\'
        $libraries += $root
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $candidates = @()
        $manifest = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path -LiteralPath $manifest) {
            $m = [regex]::Match((Get-Content -LiteralPath $manifest -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $candidates += Join-Path $lib "steamapps\common\$($m.Groups[1].Value)" }
        }
        $candidates += Join-Path $lib "steamapps\common\$GameFolderName"
        foreach ($c in $candidates) { if (Test-GameDir $c) { return $c } }
    }
    return $null
}

# Release zip: files sit next to this script. Repo checkout: build.cmd stages them in ..\dist.
function Find-Payload($name) {
    foreach ($dir in $PSScriptRoot, (Join-Path $PSScriptRoot '..\dist')) {
        $p = Join-Path $dir $name
        if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).Path }
    }
    return $null
}

# --- Locate the game --------------------------------------------------------
if (-not $GameDir) {
    Write-Step 'Looking for Mortal Kombat 11 in your Steam libraries...'
    $GameDir = Find-GameDir
}
if (-not (Test-GameDir $GameDir)) {
    Write-Host 'Could not find Mortal Kombat 11 automatically.' -ForegroundColor Yellow
    $GameDir = (Read-Host 'Paste the game folder (the one containing Binaries\Retail\MK11.exe)').Trim('"', ' ')
    if (-not (Test-GameDir $GameDir)) { Fail "MK11.exe not found under '$GameDir'." }
}
Write-Note "Game folder: $GameDir"

$retail = Join-Path $GameDir 'Binaries\Retail'
if (Get-Process -Name 'MK11', 'MK11_DX12' -ErrorAction SilentlyContinue |
        Where-Object { -not $_.Path -or $_.Path.StartsWith($retail, [StringComparison]::OrdinalIgnoreCase) }) {
    Fail 'Mortal Kombat 11 is running. Close the game and run this again.'
}

# --- Uninstall --------------------------------------------------------------
if ($Uninstall) {
    $asi = Join-Path $retail 'MK11Tweaks.asi'
    if (Test-Path -LiteralPath $asi) { Remove-Item -LiteralPath $asi -Force; Write-Step 'Removed MK11Tweaks.asi' }
    else { Write-Step 'MK11Tweaks.asi is not installed; nothing to remove.' }
    foreach ($ext in 'ini', 'log') {
        $f = Join-Path $retail "MK11Tweaks.$ext"
        if ($RemoveConfig -and (Test-Path -LiteralPath $f)) { Remove-Item -LiteralPath $f -Force; Write-Note "Removed MK11Tweaks.$ext" }
    }
    if (-not $RemoveConfig -and (Test-Path -LiteralPath (Join-Path $retail 'MK11Tweaks.ini'))) {
        Write-Note 'Kept your settings (MK11Tweaks.ini). Use -RemoveConfig to delete them too.'
    }
    Write-Note 'dinput8.dll (ASI loader) was left in place; delete it from Binaries\Retail if no other mod needs it.'
    exit 0
}

# --- Install ----------------------------------------------------------------
$srcAsi    = Find-Payload 'MK11Tweaks.asi'
$srcLoader = Find-Payload 'dinput8.dll'
if (-not $srcAsi) { Fail 'MK11Tweaks.asi not found next to this installer (or in ..\dist - run MK11Tweaks\build.cmd first).' }

Write-Step 'Installing MK11Tweaks.asi...'
Copy-Item -LiteralPath $srcAsi -Destination $retail -Force

$loader = Join-Path $retail 'dinput8.dll'
if (Test-Path -LiteralPath $loader) {
    Write-Step 'dinput8.dll already present (keeping it; it must be an ASI loader for the mod to load).'
} elseif ($srcLoader) {
    Write-Step 'Installing the Ultimate ASI Loader (dinput8.dll)...'
    Copy-Item -LiteralPath $srcLoader -Destination $retail
} else {
    Fail 'dinput8.dll (Ultimate ASI Loader) not found next to this installer.'
}

Write-Host ''
Write-Host 'Done!' -ForegroundColor Green
Write-Note 'Launch the DirectX 11 version of MK11 (MK11.exe) - the mod does nothing under DX12.'
Write-Note 'Press F1 in-game to open the MK11 Tweaks panel. Settings are saved to Binaries\Retail\MK11Tweaks.ini.'
Write-Note 'Offline only: do not use it online or in Towers of Time (it has leaderboards).'
