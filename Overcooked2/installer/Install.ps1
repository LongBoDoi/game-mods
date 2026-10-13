<#
    Installs LobbyTools (F6 lobby panel: ping, force host, kick, saved bans) for Overcooked! 2.

    - Finds the game through Steam (or use -GameDir "D:\path\to\Overcooked! 2").
    - Installs BepInEx 5 x86 if it is not there yet (a BepInEx zip placed next to this script, or
      downloaded). This game is a 32-bit Mono build, so the x64 loader will not work.
    - Copies LobbyTools.dll into BepInEx\plugins\LobbyTools\, leaving other plugins alone.

    The mod files are taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after build.cmd).

    -Uninstall removes only this mod's DLL; BepInEx, the config and the ban list are left in place.
#>
param(
    [string]$GameDir,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$BepInExVersion = '5.4.23.2'
$BepInExZipName = "BepInEx_win_x86_$BepInExVersion.zip"
$BepInExUrl     = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/$BepInExZipName"
$AppId          = '728880'
$GameFolderName = 'Overcooked! 2'
$PluginName     = 'LobbyTools'
$PluginId       = 'com.manhl.overcooked2.lobbytools'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

function Test-GameDir($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'Overcooked2.exe')) -and
        (Test-Path -LiteralPath (Join-Path $dir 'Overcooked2_Data\Managed\Assembly-CSharp.dll'))
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
    Write-Step 'Looking for Overcooked! 2 in your Steam libraries...'
    $GameDir = Find-GameDir
}
if (-not (Test-GameDir $GameDir)) {
    Write-Host 'Could not find the Steam (Mono) build of Overcooked! 2 automatically.' -ForegroundColor Yellow
    $GameDir = (Read-Host 'Paste the game folder (the one containing Overcooked2.exe)').Trim('"', ' ')
    if (-not (Test-GameDir $GameDir)) { Fail "Overcooked2.exe not found in '$GameDir'." }
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
Write-Host "    Game folder: $GameDir"

$gameExe = (Resolve-Path -LiteralPath (Join-Path $GameDir 'Overcooked2.exe')).Path
$running = Get-Process -Name 'Overcooked2' -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Path -or $_.Path -eq $gameExe }
if ($running) {
    Fail 'Overcooked! 2 is running. Close the game and run the installer again.'
}

# Keep the mod inside plugins\ even if the plugin name ever gains a path separator.
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $GameDir 'BepInEx\plugins'))
$pluginDir  = [IO.Path]::GetFullPath((Join-Path $pluginRoot $PluginName))
if (-not $pluginDir.StartsWith($pluginRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    Fail 'The mod path would land outside BepInEx\plugins.'
}

# --- Uninstall --------------------------------------------------------------
if ($Uninstall) {
    # Only this mod's DLL: anything else the user put in the folder stays.
    $installed = Join-Path $pluginDir "$PluginName.dll"
    if (Test-Path -LiteralPath $installed) {
        Remove-Item -LiteralPath $installed -Force
        Write-Step "$PluginName removed. (BepInEx, your config and your ban list were left in place.)"
    } else {
        Write-Step "$PluginName is not installed; nothing to remove."
    }
    exit 0
}

$dll = Find-Payload "$PluginName.dll"
if (-not $dll) { Fail "$PluginName.dll not found next to this installer (or in ..\dist - run $PluginName\build.cmd first)." }

# --- BepInEx ----------------------------------------------------------------
$bepCore = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
$loader  = Join-Path $GameDir 'winhttp.dll'
if ((Test-Path -LiteralPath $bepCore) -and (Test-Path -LiteralPath $loader)) {
    Write-Step 'BepInEx is already installed.'
} else {
    Write-Step "Installing BepInEx $BepInExVersion (x86)..."
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
Write-Step "Installing $PluginName..."
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $pluginDir "$PluginName.dll") -Force

# Pre-release builds wrote a [Connection filter] section that the mod no longer reads; drop it
# without touching the hotkey, the force-host choice or the ban list.
$configPath = Join-Path $GameDir "BepInEx\config\$PluginId.cfg"
if (Test-Path -LiteralPath $configPath) {
    $originalConfig = [IO.File]::ReadAllText($configPath)
    $updatedConfig = [regex]::Replace($originalConfig, '(?ms)^\[Connection filter\][^\r\n]*\r?\n.*?(?=^\[|\z)', '')
    if ($updatedConfig -ne $originalConfig) {
        [IO.File]::WriteAllText($configPath, $updatedConfig, (New-Object Text.UTF8Encoding($false)))
    }
}

Write-Host ''
Write-Host 'Done! Launch Overcooked! 2 and press F6 in a lobby you host.' -ForegroundColor Green
Write-Host 'The panel is in Vietnamese: players with their ping, kick, ban/unban, and force host for matchmaking.'
Write-Host 'Only the host needs the mod. Settings and the ban list are saved to:'
Write-Host "    $(Join-Path $GameDir "BepInEx\config\$PluginId.cfg")"
Write-Host "    $(Join-Path $GameDir "BepInEx\config\$PluginId.bans.tsv")"
