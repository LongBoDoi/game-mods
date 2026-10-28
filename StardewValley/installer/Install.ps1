<#
    Installs Monitor Co-op (a monitor each for local split-screen players) for Stardew Valley.

    - Finds the game through Steam (or use -GameDir "D:\path\to\Stardew Valley").
    - Installs SMAPI if it is not there yet (a SMAPI installer zip placed next to this script, or
      downloaded from GitHub). SMAPI's own installer does the work, unattended.
    - Copies the mod into Mods\MonitorCoop\, leaving your other mods alone.

    The mod files are taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after build.cmd).

    -Uninstall removes only this mod's files; SMAPI, your other mods and this mod's config.json stay.
#>
param(
    [string]$GameDir,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$SmapiVersion   = '4.5.2'
$SmapiZipName   = "SMAPI-$SmapiVersion-installer.zip"
$SmapiUrl       = "https://github.com/Pathoschild/SMAPI/releases/download/$SmapiVersion/$SmapiZipName"
$AppId          = '413150'
$GameFolderName = 'Stardew Valley'
$ModName        = 'MonitorCoop'
$ModFiles       = @('MonitorCoop.dll', 'manifest.json')

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

function Test-GameDir($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'Stardew Valley.exe')) -and
        (Test-Path -LiteralPath (Join-Path $dir 'Stardew Valley.dll'))
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
    Write-Step 'Looking for Stardew Valley in your Steam libraries...'
    $GameDir = Find-GameDir
}
if (-not (Test-GameDir $GameDir)) {
    Write-Host 'Could not find Stardew Valley automatically.' -ForegroundColor Yellow
    $GameDir = (Read-Host 'Paste the game folder (the one containing Stardew Valley.exe)').Trim('"', ' ')
    if (-not (Test-GameDir $GameDir)) { Fail "Stardew Valley.exe not found in '$GameDir'." }
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
Write-Host "    Game folder: $GameDir"

$running = Get-Process -Name 'Stardew Valley', 'StardewModdingAPI' -ErrorAction SilentlyContinue
if ($running) {
    Fail 'Stardew Valley is running. Close the game and run the installer again.'
}

# Keep the mod inside Mods\ even if the mod name ever gains a path separator.
$modRoot = [IO.Path]::GetFullPath((Join-Path $GameDir 'Mods'))
$modDir  = [IO.Path]::GetFullPath((Join-Path $modRoot $ModName))
if (-not $modDir.StartsWith($modRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    Fail 'The mod path would land outside the Mods folder.'
}

# --- Uninstall --------------------------------------------------------------
if ($Uninstall) {
    # Only this mod's own files: config.json and anything else in the folder stays.
    $removed = @()
    foreach ($file in $ModFiles) {
        $installed = Join-Path $modDir $file
        if (Test-Path -LiteralPath $installed) {
            Remove-Item -LiteralPath $installed -Force
            $removed += $file
        }
    }
    if (-not $removed.Count) {
        Write-Step "$ModName is not installed; nothing to remove."
        exit 0
    }

    # SMAPI warns about a mod folder with no manifest, so take the folder too once it is empty.
    if (Test-Path -LiteralPath $modDir) {
        if (-not (Get-ChildItem -LiteralPath $modDir -Force)) {
            Remove-Item -LiteralPath $modDir -Force
        } else {
            Write-Step "Kept $modDir (your config.json is in there)."
        }
    }
    Write-Step "$ModName removed. (SMAPI and your other mods were left in place.)"
    exit 0
}

$payload = Find-Payload $ModName
if (-not $payload) { Fail "$ModName\ not found next to this installer (or in ..\dist - run $ModName\build.cmd first)." }
foreach ($file in $ModFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $file))) { Fail "$file is missing from '$payload'." }
}

# --- SMAPI ------------------------------------------------------------------
$smapiExe = Join-Path $GameDir 'StardewModdingAPI.exe'
if (Test-Path -LiteralPath $smapiExe) {
    Write-Step 'SMAPI is already installed.'
} else {
    Write-Step "Installing SMAPI $SmapiVersion..."
    $zip = Find-Payload $SmapiZipName
    $downloaded = $false
    if (-not $zip) {
        $zip = Join-Path $env:TEMP $SmapiZipName
        Write-Host "    Downloading $SmapiUrl (about 40 MB)"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $ProgressPreference = 'SilentlyContinue'   # the progress bar makes 5.1's download crawl
        try {
            Invoke-WebRequest -Uri $SmapiUrl -OutFile $zip -UseBasicParsing
        } catch {
            Fail "Download failed: $($_.Exception.Message)"
        }
        $downloaded = $true
    }

    # SMAPI ships its own installer; unpack it somewhere of our own and let it do the work.
    $unpack = Join-Path $env:TEMP ("smapi-" + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Force -Path $unpack | Out-Null
        Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force
        $installer = Get-ChildItem -LiteralPath $unpack -Recurse -Filter 'SMAPI.Installer.exe' |
            Select-Object -First 1
        if (-not $installer) { Fail "SMAPI.Installer.exe was not in $SmapiZipName." }

        $process = Start-Process -FilePath $installer.FullName -Wait -PassThru -ArgumentList @(
            '--install', '--no-prompt', '--game-path', $GameDir
        )
        if ($process.ExitCode -ne 0) { Fail "SMAPI's installer exited with code $($process.ExitCode)." }
        if (-not (Test-Path -LiteralPath $smapiExe)) { Fail "SMAPI's installer ran but StardewModdingAPI.exe is not there." }
    } finally {
        Remove-Item -LiteralPath $unpack -Recurse -Force -ErrorAction SilentlyContinue
        if ($downloaded) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
    }
    $script:FreshSmapi = $true
}

# --- The mod ----------------------------------------------------------------
Write-Step "Installing $ModName..."
New-Item -ItemType Directory -Force -Path $modDir | Out-Null
foreach ($file in $ModFiles) {
    Copy-Item -LiteralPath (Join-Path $payload $file) -Destination (Join-Path $modDir $file) -Force
}

Write-Host ''
Write-Host 'Done! Start the game through SMAPI (StardewModdingAPI.exe), then start local co-op as usual:' -ForegroundColor Green
Write-Host '    co-op menu, or press Start on a second controller in an existing save.'
Write-Host 'Each player gets a whole monitor, in the order your monitors are arranged.'
Write-Host 'F7 turns that off and on again in game. Settings:'
Write-Host "    $(Join-Path $modDir 'config.json')   (written the first time the mod runs)"
if ($script:FreshSmapi) {
    Write-Host ''
    Write-Host 'SMAPI was just installed. To launch it from Steam, set the game''s launch options to:' -ForegroundColor Yellow
    Write-Host "    `"$smapiExe`" %command%"
}
