<#
    Installs LemonCoop (local split-screen co-op) for Lemon Cake.

    - Finds the game through Steam (or use -GameDir "D:\path\to\Lemon Cake").
    - Installs UE4SS v3.0.1 into LemonCake\Binaries\Win64 if it is missing, with the settings the
      mod needs (hot reload off, UObject array cache off).
    - Copies the mod to Binaries\Win64\Mods\LemonCoop and enables it in Mods\mods.txt.
    - Copies LemonCake-WindowsNoEditor_P.pak (gamepad bindings) to LemonCake\Content\Paks.

    The mod files are taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after LemonCoop\build.cmd).

    -Uninstall removes the mod and the pak; UE4SS is left in place.
#>
param(
    [string]$GameDir,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$AppId          = '1338330'
$GameFolderName = 'Lemon Cake'
$UE4SSVersion   = '3.0.1'
$UE4SSUrl       = "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v$UE4SSVersion/UE4SS_v$UE4SSVersion.zip"
$PakName        = 'LemonCake-WindowsNoEditor_P.pak'
$ModName        = 'LemonCoop'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Note($msg) { Write-Host "    $msg" }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

function Test-GameDir($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'LemonCake\Binaries\Win64\LemonCake-Win64-Shipping.exe'))
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

function Read-Text($path)        { return [IO.File]::ReadAllText($path) }
function Write-Text($path, $text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false))) }

# Sets "<Mod> : <value>" in mods.txt, adding the line above the built-in Keybinds entry
# (UE4SS requires Keybinds to stay last). $value = $null removes the line.
function Set-ModEntry($modsTxt, $name, $value) {
    $lines = [Collections.Generic.List[string]]((Read-Text $modsTxt) -split "`r?`n")
    $idx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^\s*$([regex]::Escape($name))\s*:") { $idx = $i; break }
    }
    if ($null -eq $value) {
        if ($idx -ge 0) { $lines.RemoveAt($idx) }
    } elseif ($idx -ge 0) {
        $lines[$idx] = "$name : $value"
    } else {
        $at = 0   # top of the file unless we find a better spot
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*Keybinds\s*:' -or $lines[$i] -match '^\s*;.*[Kk]eybinds') { break }
            if ($lines[$i] -match '^\s*[^;\s].*:\s*\d') { $at = $i + 1 }
        }
        $lines.Insert($at, "$name : $value")
    }
    Write-Text $modsTxt ($lines -join "`r`n")
}

function Set-IniValue($ini, $key, $value) {
    $text = Read-Text $ini
    $new  = [regex]::Replace($text, "(?m)^(\s*$([regex]::Escape($key))\s*=\s*).*$", "`${1}$value")
    if ($new -ne $text) { Write-Text $ini $new }
}

# --- Locate the game --------------------------------------------------------
if (-not $GameDir) {
    Write-Step 'Looking for Lemon Cake in your Steam libraries...'
    $GameDir = Find-GameDir
}
if (-not (Test-GameDir $GameDir)) {
    Write-Host 'Could not find Lemon Cake automatically.' -ForegroundColor Yellow
    $GameDir = (Read-Host 'Paste the game folder (the one containing LemonCake.exe)').Trim('"', ' ')
    if (-not (Test-GameDir $GameDir)) { Fail "Lemon Cake not found in '$GameDir'." }
}
Write-Note "Game folder: $GameDir"

$win64   = Join-Path $GameDir 'LemonCake\Binaries\Win64'
$modsDir = Join-Path $win64 'Mods'
$modsTxt = Join-Path $modsDir 'mods.txt'
$modDir  = Join-Path $modsDir $ModName
$pakDst  = Join-Path $GameDir "LemonCake\Content\Paks\$PakName"

$shipping = Join-Path $win64 'LemonCake-Win64-Shipping.exe'
if (Get-Process -Name 'LemonCake-Win64-Shipping' -ErrorAction SilentlyContinue |
        Where-Object { -not $_.Path -or $_.Path -eq $shipping }) {
    Fail 'Lemon Cake is running. Close the game and run this again.'
}

# A junction here means this PC is where the mod is developed (Mods\LemonCoop -> repo); never
# copy over or delete through it.
$modIsDevLink = (Test-Path -LiteralPath $modDir) -and ((Get-Item -LiteralPath $modDir).LinkType -eq 'Junction')

# --- Uninstall --------------------------------------------------------------
if ($Uninstall) {
    if ($modIsDevLink) {
        Write-Note "Mods\$ModName is a development link to $(@((Get-Item -LiteralPath $modDir).Target)[0]); left in place."
    } elseif (Test-Path -LiteralPath $modDir) {
        Remove-Item -LiteralPath $modDir -Recurse -Force
        Write-Step "Removed $modDir"
    }
    if (Test-Path -LiteralPath $modsTxt) { Set-ModEntry $modsTxt $ModName $null }
    if (Test-Path -LiteralPath $pakDst) { Remove-Item -LiteralPath $pakDst -Force; Write-Step "Removed $PakName" }
    Write-Step 'LemonCoop removed. UE4SS was left installed (delete dwmapi.dll in Binaries\Win64 to disable it).'
    exit 0
}

# --- Payload ----------------------------------------------------------------
$srcMod = Find-Payload $ModName
$srcPak = Find-Payload $PakName
if (-not $srcMod -or -not (Test-Path -LiteralPath (Join-Path $srcMod 'Scripts\main.lua'))) {
    Fail "The $ModName folder was not found next to this installer (or in ..\dist - run LemonCoop\build.cmd first)."
}
if (-not $srcPak) { Fail "$PakName was not found next to this installer (or in ..\dist)." }

# --- UE4SS ------------------------------------------------------------------
$settings = Join-Path $win64 'UE4SS-settings.ini'
if ((Test-Path -LiteralPath (Join-Path $win64 'UE4SS.dll')) -and (Test-Path -LiteralPath (Join-Path $win64 'dwmapi.dll'))) {
    Write-Step 'UE4SS is already installed.'
} else {
    Write-Step "Installing UE4SS $UE4SSVersion..."
    $zip = Join-Path $env:TEMP "UE4SS_v$UE4SSVersion.zip"
    Write-Note "Downloading $UE4SSUrl"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes 5.1's download crawl
    try { Invoke-WebRequest -Uri $UE4SSUrl -OutFile $zip -UseBasicParsing }
    catch { Fail "Download failed: $($_.Exception.Message)" }
    Expand-Archive -LiteralPath $zip -DestinationPath $win64 -Force
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

    # Match the setup the mod was tested with: UE4SS's Blueprint mod loader and cheat-manager
    # mods off (nothing here needs them).
    foreach ($m in 'CheatManagerEnablerMod', 'BPModLoaderMod', 'BPML_GenericFunctions') { Set-ModEntry $modsTxt $m 0 }
}
if (Test-Path -LiteralPath $settings) {
    # Hot reload breaks native-function hooks in UE4SS 3.0.1, and the UObject array cache
    # crashes when the game churns objects - both are required off for LemonCoop.
    Set-IniValue $settings 'EnableHotReloadSystem' '0'
    Set-IniValue $settings 'bUseUObjectArrayCache' 'false'
}

# --- The mod ----------------------------------------------------------------
if ($modIsDevLink) {
    Write-Note "Mods\$ModName is a development link to the source; not copying over it."
} else {
    Write-Step "Installing $ModName..."
    if (Test-Path -LiteralPath $modDir) { Remove-Item -LiteralPath $modDir -Recurse -Force }
    Copy-Item -LiteralPath $srcMod -Destination $modDir -Recurse
}
if (-not (Test-Path -LiteralPath $modsTxt)) { Write-Text $modsTxt "; Built-in keybinds, do not move up!`r`nKeybinds : 1`r`n" }
Set-ModEntry $modsTxt $ModName 1

Write-Step "Installing $PakName (gamepad bindings)..."
New-Item -ItemType Directory -Force -Path (Split-Path $pakDst) | Out-Null
Copy-Item -LiteralPath $srcPak -Destination $pakDst -Force

Write-Host ''
Write-Host 'Done!' -ForegroundColor Green
Write-Note 'IMPORTANT: turn Steam Input off for Lemon Cake (Steam > Lemon Cake > Properties > Controller >'
Write-Note '"Disable Steam Input"), otherwise Steam turns the gamepad into a fake keyboard/mouse.'
Write-Note 'Player 2 joins automatically once you are in the game (first gamepad = player 2).'
Write-Note 'Keys: F1 split layout, F2 join, F3 remove player 2, F4 teleport player 2 to player 1, F8 dual monitor.'
Write-Note "Options are at the top of $(Join-Path $modDir 'Scripts\main.lua')."
