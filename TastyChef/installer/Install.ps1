<#
    Installs Tasty Chef Local Co-op (a window per player on one PC, joined through the game's LAN room).

    - Finds the game through Steam (or use -GameDir "D:\path\to\Tasty Chef").
    - Installs BepInEx 5.4.23.5 if it is not there yet, or replaces an older 5.4.23.x that crashes
      this game (Unity 6) at startup. Uses a BepInEx zip placed next to this script, or downloads it.
    - Copies LocalCoop.dll into BepInEx\plugins\LocalCoop\.
    - Lets more than one copy of the game run: removes "single-instance=" from
      TastyChef_Data\boot.config (the mod also does this itself whenever the game starts).

    The mod files are taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after build.cmd).

    -Uninstall removes the mod and puts boot.config back; BepInEx is left in place.
#>
param(
    [string]$GameDir,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$BepInExVersion = '5.4.23.5'
$BepInExZipName = "BepInEx_win_x64_$BepInExVersion.zip"
$BepInExUrl     = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/$BepInExZipName"
$AppId          = '5019920'
$GameFolderName = 'Tasty Chef'
$GameExeName    = 'TastyChef.exe'
$PluginName     = 'LocalCoop'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

function Test-GameDir($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir $GameExeName))
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
    Write-Step 'Looking for Tasty Chef in your Steam libraries...'
    $GameDir = Find-GameDir
}
if (-not (Test-GameDir $GameDir)) {
    Write-Host 'Could not find Tasty Chef automatically.' -ForegroundColor Yellow
    $GameDir = (Read-Host "Paste the game folder (the one containing $GameExeName)").Trim('"', ' ')
    if (-not (Test-GameDir $GameDir)) { Fail "$GameExeName not found in '$GameDir'." }
}
Write-Host "    Game folder: $GameDir"

$gameExe = (Resolve-Path -LiteralPath (Join-Path $GameDir $GameExeName)).Path
$running = Get-Process -Name 'TastyChef' -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Path -or $_.Path -eq $gameExe }
if ($running) {
    Fail 'Tasty Chef is running. Close every Tasty Chef window and run the installer again.'
}

$pluginDir  = Join-Path $GameDir "BepInEx\plugins\$PluginName"
$bootConfig = Join-Path $GameDir 'TastyChef_Data\boot.config'
$bootBackup = "$bootConfig.localcoop-backup"

# --- Uninstall --------------------------------------------------------------
if ($Uninstall) {
    if (Test-Path -LiteralPath $pluginDir) {
        Remove-Item -LiteralPath $pluginDir -Recurse -Force
        Write-Step 'Tasty Chef Local Co-op removed.'
    } else {
        Write-Step 'Tasty Chef Local Co-op is not installed; nothing to remove.'
    }
    # Back to one copy of the game at a time, as the game ships.
    if (Test-Path -LiteralPath $bootConfig) {
        $text = [IO.File]::ReadAllText($bootConfig)
        if ($text -notmatch '(?m)^single-instance=') {
            $text = $text.TrimEnd("`r", "`n") + "`nsingle-instance=`n"
            [IO.File]::WriteAllText($bootConfig, $text)
            Write-Host '    boot.config: the game runs as a single copy again.'
        }
    }
    Remove-Item -LiteralPath $bootBackup -Force -ErrorAction SilentlyContinue
    Write-Host '    BepInEx was left installed; delete winhttp.dll in the game folder to disable all mods.'
    exit 0
}

$dll = Find-Payload "$PluginName.dll"
if (-not $dll) { Fail "$PluginName.dll not found next to this installer (or in ..\dist - run LocalCoop\build.cmd first)." }

# --- BepInEx ----------------------------------------------------------------
$bepCore = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
$loader  = Join-Path $GameDir 'winhttp.dll'
$installed = $null
if ((Test-Path -LiteralPath $bepCore) -and (Test-Path -LiteralPath $loader)) {
    $installed = [Version]((Get-Item -LiteralPath $bepCore).VersionInfo.FileVersion)
}
# Tasty Chef runs on Unity 6000.4; BepInEx 5.4.23.2 and older crash it before the title screen.
if ($installed -and $installed -ge [Version]'5.4.23.5') {
    Write-Step "BepInEx $installed is already installed."
} else {
    if ($installed) { Write-Step "Updating BepInEx $installed to $BepInExVersion (older versions crash this game)..." }
    else            { Write-Step "Installing BepInEx $BepInExVersion..." }
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
    # Overwrites BepInEx's own files only; plugins and config folders are kept.
    Expand-Archive -LiteralPath $zip -DestinationPath $GameDir -Force
    if ($downloaded) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
}

# --- More than one copy of the game ------------------------------------------
if (Test-Path -LiteralPath $bootConfig) {
    $text = [IO.File]::ReadAllText($bootConfig)
    if ($text -match '(?m)^single-instance=') {
        if (-not (Test-Path -LiteralPath $bootBackup)) { Copy-Item -LiteralPath $bootConfig -Destination $bootBackup }
        $lines = ($text -replace "`r`n", "`n") -split "`n" | Where-Object { $_ -notmatch '^single-instance=' }
        [IO.File]::WriteAllText($bootConfig, ($lines -join "`n"))
        Write-Host '    boot.config: more than one copy of the game may run now.'
    }
}

# --- The mod ----------------------------------------------------------------
Write-Step 'Installing Tasty Chef Local Co-op...'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath $dll -Destination $pluginDir -Force

Write-Host ''
Write-Host 'Done! Start Tasty Chef as usual and press F9 at the title screen to open a window for another player.' -ForegroundColor Green
Write-Host 'Each extra player needs a controller: they press LB + RB together on it to take it.'
Write-Host 'Settings (keys, window layout, guest sound) are in:'
Write-Host "    $(Join-Path $GameDir 'BepInEx\config\manhl.tastychef.localcoop.cfg')  (created on the first start)"
