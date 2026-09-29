<#
.SYNOPSIS
    Installs the Kebab Chefs! - Restaurant Simulator mods (Player Tweaks, Load Tweaks).

.DESCRIPTION
    Copies each mod DLL into the game's BepInEx\plugins\<Mod>\ folder. The DLLs are
    taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after running each mod's build.cmd). Mods whose DLL is not found are skipped.

    The game is located automatically through Steam (registry + libraryfolders.vdf).
    Pass -GameDir to override that, e.g. for a non-Steam or relocated copy.

    If BepInEx is missing, the exact bleeding-edge IL2CPP build the mods were made
    against is downloaded. An existing BepInEx of another flavour is never unpacked
    over (that silently produces a game that starts but loads nothing).

.PARAMETER GameDir
    The game folder, i.e. the one containing "Kebab Chefs! - Restaurant Simulator.exe".

.PARAMETER Uninstall
    Removes the mods instead of installing them. Leaves your settings files alone
    unless -RemoveConfig is also given.

.PARAMETER RemoveConfig
    With -Uninstall, also deletes the saved settings.

.EXAMPLE
    .\Install.ps1

.EXAMPLE
    .\Install.ps1 -GameDir "D:\Games\Kebab Chefs!"

.EXAMPLE
    .\Install.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [string] $GameDir,
    [switch] $Uninstall,
    [switch] $RemoveConfig,
    [switch] $SkipBepInEx,
    [switch] $ForceBepInEx
)

$ErrorActionPreference = 'Stop'

$AppId       = '1001270'
$GameExe     = 'Kebab Chefs! - Restaurant Simulator.exe'
$Plugins     = @(
    @{ Name = 'PlayerTweaks'; Config = 'com.manhl.kebabchefs.playertweaks.cfg' },
    @{ Name = 'LoadTweaks';   Config = 'com.manhl.kebabchefs.loadtweaks.cfg' }
)

# Release zip: DLLs sit next to this script. Repo checkout: build.cmd stages them in ..\dist.
function Find-Payload([string] $FileName) {
    foreach ($dir in $PSScriptRoot, (Join-Path $PSScriptRoot '..\dist')) {
        $p = Join-Path $dir $FileName
        if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).Path }
    }
    return $null
}

# Pinned to the exact build this mod was developed and tested against. BepInEx
# bleeding-edge builds are not interchangeable - the interop assemblies it generates
# are what the mod compiles against - so this deliberately does not track "latest".
$BepInExBuild   = '785'
$BepInExVersion = '6.0.0-be.785'
$BepInExZipName = 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785+6abdba4.zip'
$BepInExUrl     = 'https://builds.bepinex.dev/projects/bepinex_be/785/' +
                  [Uri]::EscapeDataString($BepInExZipName)

$script:FreshBepInEx = $false

function Write-Step { param([string]$Text) Write-Host "  $Text" }
function Write-Ok   { param([string]$Text) Write-Host "  [ok] $Text"   -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "  [!]  $Text"   -ForegroundColor Yellow }
function Write-Err  { param([string]$Text) Write-Host "  [x]  $Text"   -ForegroundColor Red }

function Get-SteamRoot {
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try {
            $path = (Get-ItemProperty -Path $key -ErrorAction Stop).SteamPath
            if ($path) { return $path.Replace('/', '\') }
        } catch { }
    }
    return $null
}

function Get-SteamLibraries {
    $roots = @()
    $steam = Get-SteamRoot
    if (-not $steam) { return $roots }

    $roots += $steam

    # libraryfolders.vdf lists any additional drives the user has set up.
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($line in Get-Content $vdf) {
            if ($line -match '"path"\s+"(.+?)"') {
                $roots += $matches[1].Replace('\\', '\')
            }
        }
    }
    return $roots | Select-Object -Unique
}

function Find-GameDir {
    foreach ($lib in Get-SteamLibraries) {
        $manifest = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path $manifest)) { continue }

        # Prefer the folder named in the manifest over guessing it.
        $installDir = $null
        foreach ($line in Get-Content $manifest) {
            if ($line -match '"installdir"\s+"(.+?)"') { $installDir = $matches[1]; break }
        }
        if (-not $installDir) { continue }

        $candidate = Join-Path $lib "steamapps\common\$installDir"
        if (Test-Path (Join-Path $candidate $GameExe)) { return $candidate }
    }
    return $null
}

function Install-BepInEx {
    param([string] $TargetDir)

    # The release zip bundles this exact build: builds.bepinex.dev can crawl (~25 KB/s seen).
    $bundled = Find-Payload $BepInExZipName
    if ($bundled) {
        Write-Step 'Extracting the bundled BepInEx into the game folder...'
        Expand-Archive -Path $bundled -DestinationPath $TargetDir -Force
        return $true
    }

    $zip = Join-Path $env:TEMP "BepInEx-IL2CPP-$BepInExBuild.zip"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

        # The progress bar makes Invoke-WebRequest crawl on Windows PowerShell 5.1,
        # so it is suppressed and we print our own status instead.
        $oldProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'

        Write-Step "Downloading BepInEx $BepInExVersion ..."
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing -TimeoutSec 300

        $ProgressPreference = $oldProgress

        $size = (Get-Item $zip).Length
        if ($size -lt 1MB) {
            Write-Err "Download looks wrong ($size bytes). Aborting rather than unpacking it."
            return $false
        }
        Write-Ok ("Downloaded {0:N1} MB." -f ($size / 1MB))

        Write-Step 'Extracting into the game folder...'
        Expand-Archive -Path $zip -DestinationPath $TargetDir -Force
        return $true
    }
    catch {
        Write-Err "Could not install BepInEx: $($_.Exception.Message)"
        Write-Host ''
        Write-Step 'Install it by hand instead:'
        Write-Step "  1. Download: $BepInExUrl"
        Write-Step "  2. Extract it into: $TargetDir"
        Write-Step '  3. Run this installer again.'
        return $false
    }
    finally {
        if (Test-Path $zip) { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
    }
}

function Assert-GameClosed {
    $proc = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '*Kebab*' -and $_.Name -notlike '*CrashHandler*' }

    if ($proc) {
        Write-Err 'The game is running. Close it and run this again.'
        Write-Step 'The plugin file is locked while the game holds it open.'
        exit 1
    }
}

Write-Host ''
Write-Host '  Kebab Chefs! mods - Player Tweaks, Load Tweaks' -ForegroundColor Cyan
Write-Host '  -----------------------------------------------'

# --------------------------------------------------------------- locate game

if ($GameDir) {
    if (-not (Test-Path (Join-Path $GameDir $GameExe))) {
        Write-Err "No '$GameExe' in: $GameDir"
        exit 1
    }
    Write-Ok "Game (from -GameDir): $GameDir"
} else {
    Write-Step 'Looking for the game via Steam...'
    $GameDir = Find-GameDir
    if (-not $GameDir) {
        Write-Err 'Could not find the game automatically.'
        Write-Step 'Re-run with the folder that contains the game exe, for example:'
        Write-Step '  Install.cmd -GameDir "D:\Games\Kebab Chefs!"'
        exit 1
    }
    Write-Ok "Game: $GameDir"
}

$bepinex = Join-Path $GameDir 'BepInEx'

# ------------------------------------------------------------------ uninstall

if ($Uninstall) {
    Assert-GameClosed

    $keptConfig = $false
    foreach ($plugin in $Plugins) {
        $pluginDir = Join-Path $bepinex "plugins\$($plugin.Name)"
        if (Test-Path $pluginDir) {
            Remove-Item $pluginDir -Recurse -Force
            Write-Ok "Removed: $pluginDir"
        } else {
            Write-Warn "$($plugin.Name) was not installed; nothing to remove."
        }

        $cfg = Join-Path $bepinex "config\$($plugin.Config)"
        if ($RemoveConfig) {
            if (Test-Path $cfg) { Remove-Item $cfg -Force; Write-Ok "Removed saved settings: $($plugin.Config)" }
        } elseif (Test-Path $cfg) {
            $keptConfig = $true
        }
    }
    if ($keptConfig) { Write-Step 'Kept your settings. Use -RemoveConfig to delete them too.' }

    Write-Host ''
    Write-Host '  Uninstalled.' -ForegroundColor Green
    Write-Host ''
    exit 0
}

# ------------------------------------------------------------ check BepInEx

Write-Step 'Checking BepInEx...'

$il2cppCore = Join-Path $bepinex 'core\BepInEx.Unity.IL2CPP.dll'

if (-not (Test-Path $il2cppCore)) {

    # A BepInEx folder without the IL2CPP core means some other flavour is installed
    # (BepInEx 5, or the Mono build). Unpacking over the top of that produces a mess,
    # so stop and let the user decide rather than silently mixing two installs.
    if ((Test-Path $bepinex) -and -not $ForceBepInEx) {
        Write-Err 'A BepInEx folder exists but it is not the IL2CPP build of BepInEx 6.'
        Write-Step 'Kebab Chefs! is an IL2CPP game; BepInEx 5 and the Mono build cannot load'
        Write-Step 'this mod. Installing over the top would mix two incompatible installs.'
        Write-Host ''
        Write-Step "Delete this folder, then run the installer again: $bepinex"
        Write-Step 'Or re-run with -ForceBepInEx to unpack over it anyway.'
        exit 1
    }

    if ($SkipBepInEx) {
        Write-Err 'BepInEx 6 (IL2CPP) is missing and -SkipBepInEx was given.'
        exit 1
    }

    Write-Warn 'BepInEx 6 (IL2CPP) is not installed. Installing it now.'
    if (-not (Install-BepInEx -TargetDir $GameDir)) { exit 1 }

    if (-not (Test-Path $il2cppCore)) {
        Write-Err 'BepInEx was unpacked but the IL2CPP core is still missing.'
        Write-Step "Expected: $il2cppCore"
        exit 1
    }
    Write-Ok 'BepInEx installed.'
    $script:FreshBepInEx = $true
}

$coreVersion = (Get-Item $il2cppCore).VersionInfo.ProductVersion
Write-Ok "BepInEx present: $coreVersion"

$interop = Join-Path $bepinex 'interop\Assembly-CSharp.dll'
if (-not (Test-Path $interop)) {
    Write-Warn 'Interop assemblies not generated yet.'
    Write-Step 'That is fine - they are created the first time you launch the game with'
    Write-Step 'BepInEx installed. The mod will start working from that launch onward.'
}

# ------------------------------------------------------------------- install

Assert-GameClosed

$installedAny = $false
foreach ($plugin in $Plugins) {
    $dllName = "$($plugin.Name).dll"
    $source  = Find-Payload $dllName
    if (-not $source) {
        Write-Warn "$dllName not found next to this script (or in ..\dist); skipping $($plugin.Name)."
        continue
    }

    $pluginDir = Join-Path $bepinex "plugins\$($plugin.Name)"
    if (-not (Test-Path $pluginDir)) {
        New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
    }
    Copy-Item $source (Join-Path $pluginDir $dllName) -Force

    $installed = Get-Item (Join-Path $pluginDir $dllName)
    Write-Ok "Installed: $($installed.FullName) (v$($installed.VersionInfo.FileVersion))"
    $installedAny = $true
}

if (-not $installedAny) {
    Write-Err 'No mod DLLs found. Keep them in the same folder as this script, or build them first.'
    exit 1
}

Write-Host ''
Write-Host '  Done.' -ForegroundColor Green
Write-Host ''

if ($script:FreshBepInEx) {
    Write-Warn 'First launch will be slow - several minutes at a seemingly frozen screen.'
    Write-Step 'BepInEx is generating the interop assemblies from the game code. Let it'
    Write-Step 'finish; do not kill the game. Later launches are normal speed.'
    Write-Host ''
}

Write-Step 'Player Tweaks: press F1 in-game to open the overlay.'
Write-Step '  Tabs: Player (movement/reach), Teleport (locations + hotkeys), Extras.'
Write-Step 'Load Tweaks: loads heavy saves faster; press F8 after a load for the breakdown.'
Write-Step 'Settings are written to BepInEx\config\ after the first run.'
Write-Step 'If nothing appears, check BepInEx\LogOutput.log.'
Write-Host ''
