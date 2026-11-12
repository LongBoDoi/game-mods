<#
    Installs MonitorCoop, which gives each local split-screen player a monitor of their own, into
    Project Zomboid.

    - Finds the game through Steam (or use -GameDir "D:\path\to\ProjectZomboid").
    - Copies MonitorCoop.jar and its launcher into <game>\MonitorCoop\.
    - Changes nothing else. No game file is modified: the mod is loaded by starting the game
      through <game>\MonitorCoop\Play.cmd, which reads the game's own launcher settings. A game
      update therefore cannot quietly leave the mod out, and the game still starts without the mod
      when you launch it the usual way.

    The mod files are taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after build.cmd).

    -Shortcut  also puts a "Project Zomboid (MonitorCoop)" shortcut on the desktop.
    -Uninstall removes the folder and the shortcut. Your settings in
               <user>\Zomboid\MonitorCoop.ini are left alone.
#>
param(
    [string]$GameDir,
    [switch]$Shortcut,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$AppId         = '108600'
$GameFolder    = 'ProjectZomboid'
$ModFolder     = 'MonitorCoop'
$ModFiles      = @('MonitorCoop.jar', 'Play.cmd', 'Play.ps1')
$ShortcutName  = 'Project Zomboid (MonitorCoop).lnk'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

function Test-GameDir($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'projectzomboid.jar')) -and
        (Test-Path -LiteralPath (Join-Path $dir 'ProjectZomboid64.json'))
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
        $candidates += Join-Path $lib "steamapps\common\$GameFolder"
        foreach ($c in $candidates) {
            if (Test-GameDir $c) { return $c }
        }
    }
    return $null
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Release zip: the mod folder sits next to this script. Repo checkout: build.cmd stages it in ..\dist.
function Find-Payload($name) {
    foreach ($dir in $here, (Join-Path $here '..\dist')) {
        $p = Join-Path $dir $name
        if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).Path }
    }
    return $null
}

# --- Locate the game --------------------------------------------------------
if (-not $GameDir) {
    Write-Step 'Looking for Project Zomboid in your Steam libraries...'
    $GameDir = Find-GameDir
}
if (-not (Test-GameDir $GameDir)) {
    Write-Host 'Could not find Project Zomboid automatically.' -ForegroundColor Yellow
    $GameDir = (Read-Host 'Paste the game folder (the one containing projectzomboid.jar)').Trim('"', ' ')
    if (-not (Test-GameDir $GameDir)) { Fail "projectzomboid.jar not found in '$GameDir'." }
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
Write-Host "    Game folder: $GameDir"

if (Get-Process -Name 'ProjectZomboid64', 'javaw', 'java' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($GameDir, [StringComparison]::OrdinalIgnoreCase) }) {
    Fail 'Project Zomboid is running. Close the game and run the installer again.'
}

$modDir   = [IO.Path]::GetFullPath((Join-Path $GameDir $ModFolder))
$launcher = Join-Path $modDir 'Play.cmd'
$desktop  = Join-Path ([Environment]::GetFolderPath('Desktop')) $ShortcutName

if (-not $modDir.StartsWith($GameDir, [StringComparison]::OrdinalIgnoreCase)) {
    Fail 'The mod path would land outside the game folder.'
}

# --- Uninstall --------------------------------------------------------------
if ($Uninstall) {
    $removedAny = $false

    if (Test-Path -LiteralPath $desktop) {
        Remove-Item -LiteralPath $desktop -Force
        Write-Step 'Removed the desktop shortcut.'
        $removedAny = $true
    }

    if (Test-Path -LiteralPath $modDir) {
        foreach ($file in $ModFiles) {
            $installed = Join-Path $modDir $file
            if (Test-Path -LiteralPath $installed) {
                Remove-Item -LiteralPath $installed -Force
                $removedAny = $true
            }
        }
        if (-not (Get-ChildItem -LiteralPath $modDir -Force)) {
            Remove-Item -LiteralPath $modDir -Force
        } else {
            Write-Step "Kept $modDir (something else is in there)."
        }
    }

    if ($removedAny) {
        Write-Step 'MonitorCoop removed. Local split-screen is the game''s own again.'
        Write-Host  "    Your settings are still in $env:USERPROFILE\Zomboid\MonitorCoop.ini"
        Write-Host  '    If you pointed Steam''s launch options at Play.cmd, clear them as well.'
    } else {
        Write-Step 'MonitorCoop is not installed; nothing to remove.'
    }
    exit 0
}

# --- Install ----------------------------------------------------------------
$payload = Find-Payload $ModFolder
if (-not $payload) { Fail "$ModFolder\ not found next to this installer (or in ..\dist - run $ModFolder\build.cmd first)." }
foreach ($file in $ModFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $file))) { Fail "$file is missing from '$payload'." }
}

Write-Step "Installing into $modDir..."
New-Item -ItemType Directory -Force -Path $modDir | Out-Null
foreach ($file in $ModFiles) {
    Copy-Item -LiteralPath (Join-Path $payload $file) -Destination (Join-Path $modDir $file) -Force
}

if ($Shortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($desktop)
    $link.TargetPath = $launcher
    $link.WorkingDirectory = $modDir
    $link.Description = 'Project Zomboid with MonitorCoop: a monitor each for local split-screen'
    $link.IconLocation = (Join-Path $GameDir 'ProjectZomboid64.exe') + ',0'
    $link.WindowStyle = 7            # the launcher's window waits for the game; keep it minimised
    $link.Save()
    Write-Step "Put a shortcut on the desktop: $ShortcutName"
}

Write-Host ''
Write-Host 'Done! Start the game with the mod like this:' -ForegroundColor Green
Write-Host "    $launcher"
if (-not $Shortcut) {
    Write-Host '    (run the installer with -Shortcut to put that on your desktop)'
}
Write-Host ''
Write-Host 'To keep using Steam''s Play button, set the game''s launch options to:' -ForegroundColor Green
Write-Host "    `"$launcher`" %command%"
Write-Host '    (Steam: right-click Project Zomboid, Properties, General, Launch Options)'
Write-Host ''
Write-Host 'Then begin local split-screen the way you normally do: press a button on a second'
Write-Host 'controller in game. The window spreads over your monitors as the second player joins,'
Write-Host 'each player gets a whole monitor, and it all goes back when they leave. F7 turns the'
Write-Host 'monitor layout off and on.'
Write-Host ''
Write-Host "Settings:  $env:USERPROFILE\Zomboid\MonitorCoop.ini  (written the first time the mod runs)"
Write-Host "Log:       $env:USERPROFILE\Zomboid\console.txt       (search it for MonitorCoop)"
Write-Host ''
Write-Host 'Nothing in the game folder was modified, so starting the game the usual way still gives'
Write-Host 'you the game without the mod, and a game update cannot leave the mod half-installed.'
