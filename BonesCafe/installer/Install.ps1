<#
    Installs the Hold Act mod for Bone's Cafe.

    - Finds the game through Steam (or use -GameDir "D:\path\to\Bone's Cafe").
    - Installs BepInEx 5 if it is not there yet (a BepInEx zip placed next to this script, or downloaded).
    - Copies HoldAct.dll into BepInEx\plugins\HoldAct\.

    The mod files are taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after build.cmd).

    -Uninstall removes only the mod; BepInEx is left in place.
#>
param(
    [string]$GameDir,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$BepInExVersion = '5.4.23.2'
$BepInExZipName = "BepInEx_win_x64_$BepInExVersion.zip"
$BepInExUrl     = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/$BepInExZipName"
$AppId          = '1739070'
$GameFolderName = "Bone's Cafe"
$PluginName     = 'HoldAct'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

function Test-GameDir($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'BonesCafe.exe'))
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
        # Prefer the folder named in the app manifest, fall back to the default name.
        $candidates = @()
        $manifest = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path -LiteralPath $manifest) {
            $m = [regex]::Match((Get-Content -LiteralPath $manifest -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $candidates += Join-Path $lib "steamapps\common\$($m.Groups[1].Value)" }
        }
        $candidates += Join-Path $lib "steamapps\common\$GameFolderName"
        foreach ($c in $candidates) {
            if (Test-GameDir $c) { return $c }
        }
    }
    return $null
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Release zip: files sit next to this script. Repo checkout: build.cmd stages them in ..\dist.
function Find-Payload($name) {
    foreach ($dir in $here, (Join-Path $here '..\dist')) {
        $p = Join-Path $dir $name
        if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).Path }
    }
    return $null
}

# --- Locate the game --------------------------------------------------------
if (-not $GameDir) {
    Write-Step 'Looking for Bone''s Cafe in your Steam libraries...'
    $GameDir = Find-GameDir
}
if (-not (Test-GameDir $GameDir)) {
    Write-Host 'Could not find Bone''s Cafe automatically.' -ForegroundColor Yellow
    $GameDir = (Read-Host 'Paste the game folder (the one containing BonesCafe.exe)').Trim('"', ' ')
    if (-not (Test-GameDir $GameDir)) { Fail "BonesCafe.exe not found in '$GameDir'." }
}
Write-Host "    Game folder: $GameDir"

$gameExe = (Resolve-Path -LiteralPath (Join-Path $GameDir 'BonesCafe.exe')).Path
$running = Get-Process -Name 'BonesCafe' -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Path -or $_.Path -eq $gameExe }
if ($running) {
    Fail 'Bone''s Cafe is running. Close the game and run the installer again.'
}

$pluginDir = Join-Path $GameDir "BepInEx\plugins\$PluginName"

# --- Uninstall --------------------------------------------------------------
if ($Uninstall) {
    if (Test-Path -LiteralPath $pluginDir) {
        Remove-Item -LiteralPath $pluginDir -Recurse -Force
        Write-Step 'Hold Act removed. (BepInEx was left installed; delete winhttp.dll in the game folder to disable all mods.)'
    } else {
        Write-Step 'Hold Act is not installed; nothing to remove.'
    }
    exit 0
}

$dll = Find-Payload "$PluginName.dll"
if (-not $dll) { Fail "$PluginName.dll not found next to this installer (or in ..\dist - run HoldAct\build.cmd first)." }

# --- BepInEx ----------------------------------------------------------------
$bepCore = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
$loader  = Join-Path $GameDir 'winhttp.dll'
if ((Test-Path -LiteralPath $bepCore) -and (Test-Path -LiteralPath $loader)) {
    Write-Step 'BepInEx is already installed.'
} else {
    Write-Step "Installing BepInEx $BepInExVersion..."
    $zip = Find-Payload $BepInExZipName
    $downloaded = $false
    if (-not $zip) {
        $zip = Join-Path $env:TEMP $BepInExZipName
        Write-Host "    Downloading $BepInExUrl"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $ProgressPreference = 'SilentlyContinue'   # the progress bar makes 5.1's download crawl
        try {
            Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
        } catch {
            Fail "Download failed: $($_.Exception.Message)"
        }
        $downloaded = $true
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $GameDir -Force
    if ($downloaded) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
}

# --- The mod ----------------------------------------------------------------
Write-Step 'Installing Hold Act...'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath $dll -Destination $pluginDir -Force

Write-Host ''
Write-Host 'Done! Launch Bone''s Cafe and hold the Act button (J / gamepad X) to chop and grind continuously.' -ForegroundColor Green
Write-Host 'Settings appear after the first launch in:'
Write-Host "    $(Join-Path $GameDir 'BepInEx\config\manhl.bonescafe.holdact.cfg')"
