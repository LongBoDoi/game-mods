<#
    Installs the Stardew Valley mods in this folder: Monitor Co-op (a monitor each for local split-screen
    players) and Auto Fish Co-op (Auto Fishing's settings per player).

    - Finds the game through Steam (or use -GameDir "D:\path\to\Stardew Valley").
    - Installs SMAPI if it is not there yet (a SMAPI installer zip placed next to this script, or
      downloaded from GitHub). SMAPI's own installer does the work, unattended.
    - Copies each mod into its own folder under Mods\, leaving your other mods alone.

    The mod files are taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after build.cmd).

    -Uninstall removes only these mods' files; SMAPI, your other mods and these mods' config.json stay.
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

# One folder under Mods\ each, with the files that belong to the mod itself (config.json is the player's).
$Mods = @(
    @{ Name = 'MonitorCoop';  Files = @('MonitorCoop.dll', 'manifest.json') }
    @{ Name = 'AutoFishCoop'; Files = @('AutoFishCoop.dll', 'manifest.json') }
)

# Auto Fish Co-op has nothing to do without the mod it fixes, which is not ours to install.
$AutoFishingName = 'AutoFishing2'
$AutoFishingUrl  = 'https://www.nexusmods.com/stardewvalley/mods/21339'

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

# Keep each mod inside Mods\ even if a mod name ever gains a path separator.
$modRoot = [IO.Path]::GetFullPath((Join-Path $GameDir 'Mods'))
foreach ($mod in $Mods) {
    $mod.Dir = [IO.Path]::GetFullPath((Join-Path $modRoot $mod.Name))
    if (-not $mod.Dir.StartsWith($modRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Fail "The path for $($mod.Name) would land outside the Mods folder."
    }
}

# --- Uninstall --------------------------------------------------------------
if ($Uninstall) {
    # Only these mods' own files: config.json and anything else in the folder stays.
    $removedAny = $false
    foreach ($mod in $Mods) {
        $removed = @()
        foreach ($file in $mod.Files) {
            $installed = Join-Path $mod.Dir $file
            if (Test-Path -LiteralPath $installed) {
                Remove-Item -LiteralPath $installed -Force
                $removed += $file
            }
        }
        if (-not $removed.Count) {
            Write-Step "$($mod.Name) is not installed; nothing to remove."
            continue
        }
        $removedAny = $true

        # SMAPI warns about a mod folder with no manifest, so take the folder too once it is empty.
        if (Test-Path -LiteralPath $mod.Dir) {
            if (-not (Get-ChildItem -LiteralPath $mod.Dir -Force)) {
                Remove-Item -LiteralPath $mod.Dir -Force
            } else {
                Write-Step "Kept $($mod.Dir) (your config.json is in there)."
            }
        }
        Write-Step "$($mod.Name) removed."
    }

    if ($removedAny) { Write-Step 'Done. (SMAPI and your other mods were left in place.)' }
    exit 0
}

foreach ($mod in $Mods) {
    $mod.Payload = Find-Payload $mod.Name
    if (-not $mod.Payload) {
        Fail "$($mod.Name)\ not found next to this installer (or in ..\dist - run $($mod.Name)\build.cmd first)."
    }
    foreach ($file in $mod.Files) {
        if (-not (Test-Path -LiteralPath (Join-Path $mod.Payload $file))) {
            Fail "$file is missing from '$($mod.Payload)'."
        }
    }
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

# --- The mods ---------------------------------------------------------------
foreach ($mod in $Mods) {
    Write-Step "Installing $($mod.Name)..."
    New-Item -ItemType Directory -Force -Path $mod.Dir | Out-Null
    foreach ($file in $mod.Files) {
        Copy-Item -LiteralPath (Join-Path $mod.Payload $file) -Destination (Join-Path $mod.Dir $file) -Force
    }
}

Write-Host ''
Write-Host 'Done! Start the game through SMAPI (StardewModdingAPI.exe), then start local co-op as usual:' -ForegroundColor Green
Write-Host '    co-op menu, or press Start on a second controller in an existing save.'
Write-Host 'Each player gets a whole monitor, in the order your monitors are arranged, and their own'
Write-Host 'Auto Fishing settings. F7 turns the monitor layout off and on again in game. Settings:'
foreach ($mod in $Mods) {
    Write-Host "    $(Join-Path $mod.Dir 'config.json')"
}
Write-Host '    (each written the first time that mod runs)'

if (-not (Test-Path -LiteralPath (Join-Path $modRoot $AutoFishingName))) {
    Write-Host ''
    Write-Host "AutoFishCoop needs Auto Fishing 2.0, which is not installed and is not mine to ship." -ForegroundColor Yellow
    Write-Host "SMAPI will skip AutoFishCoop until it is there: $AutoFishingUrl"
}

if ($script:FreshSmapi) {
    Write-Host ''
    Write-Host 'SMAPI was just installed. To launch it from Steam, set the game''s launch options to:' -ForegroundColor Yellow
    Write-Host "    `"$smapiExe`" %command%"
}
