<#
    Installs or removes the Overcooked! 2 mods, and the BepInEx loader they run on.

    Run with no arguments and it stays open: it asks what you want, does it, then asks again with
    the list refreshed, until you choose Q. Pass -All/-Only/-Yes and it does that one thing and
    exits instead, so scripted use never loops.

      OC2Tweaks        F6 panel: ping, force host, kick, saved bans, and an F5 instant
                       restart you can switch off or rebind in its Phim tat tab.
      RandomLevel      random DIY levels; Continue goes straight to the next random level.
      RelayKitchen     custom level Bếp Tiếp Sức / Night Market Relay (needs OC2DIYLevel).
      BepInEx          the mod loader itself (BepInEx 5 x86), installed or upgraded in place.

    - Finds the game through Steam (or use -GameDir "D:\path\to\Overcooked! 2").
    - BepInEx defaults to the newest stable 5.x release for win_x86. This game is a 32-bit Unity
      2017 Mono build: the x64 loader and the BepInEx 6 prereleases do not work on it, so "latest"
      deliberately means latest 5.x and never simply the newest release on GitHub.
    - Mods are copied into BepInEx\plugins\<Mod>\, leaving other plugins alone.

    The mod files are taken from this script's folder (release zip) or from ..\dist (repo checkout,
    after build.cmd). A BepInEx zip placed next to this script is used instead of downloading.

    Run with no arguments and it asks Install or Uninstall first, then asks what you want,
    does it, and asks again until you choose Q. Pass -All/-Only/-Yes and it does that one thing
    and exits, so scripted use never loops.

    Examples:
      Install.ps1                                  ask mode, then keep asking until you quit
      Install.ps1 -All                             install everything, once, no questions
      Install.ps1 -Only OC2Tweaks                  install just that mod, once
      Install.ps1 -Only RelayKitchen               install just the custom level (OC2DIYLevel must be installed)
      Install.ps1 -Only BepInEx                    install or upgrade just the loader
      Install.ps1 -Only BepInEx -BepInExVersion 5.4.23.2    pin a version
      Install.ps1 -Uninstall                       ask what to remove, until you quit
      Install.ps1 -Uninstall -All                  remove all mods and the loader, once
#>
param(
    [string]$GameDir,
    [switch]$Uninstall,
    [Alias('Mods')]
    [string[]]$Only,
    [switch]$All,
    [switch]$Yes,
    [string]$BepInExVersion,
    [switch]$PurgeBepInEx,
    [string]$DiyPackDir
)

$ErrorActionPreference = 'Stop'

# Expand-Archive arrived in PowerShell 5.0. Windows 10 and 11 ship 5.1; say so plainly rather
# than failing later with "the term Expand-Archive is not recognized".
if ($PSVersionTable.PSVersion.Major -lt 5) {
    Write-Host "ERROR: Needs Windows PowerShell 5.0 or newer; this is $($PSVersionTable.PSVersion)." -ForegroundColor Red
    Write-Host '       Windows 10 and 11 already have it. On older Windows, install Windows Management Framework 5.1.'
    exit 1
}

$AppId          = '728880'
$GameFolderName = 'Overcooked! 2'

# Used when GitHub cannot be reached and no zip was bundled.
$BepInExFallback = '5.4.23.2'

# LegacyId: a config this mod used to write under a different plugin GUID. It is renamed on install
# so settings made before the mod moved into this repo are not silently abandoned.
$Targets = @(
    [pscustomobject]@{ Key = 'OC2Tweaks'; Kind = 'Mod'
        Blurb = 'F6 panel: ping, force host, kick, bans, F5 instant restart'
        Id = 'com.manhl.overcooked2.tweaks'; LegacyId = 'com.manhl.overcooked2.lobbytools' }
    [pscustomobject]@{ Key = 'RandomLevel'; Kind = 'Mod'
        Blurb = 'random DIY levels; F8 skips, Continue plays the next level'
        Id = 'com.manhl.overcooked2.randomlevel'; LegacyId = $null }
    [pscustomobject]@{ Key = 'OC2DIYLevel'; Kind = 'Pack'
        Blurb = 'custom level pack + helper plugins (not bundled: ~400 MB)'
        Id = $null; LegacyId = $null }
    [pscustomobject]@{ Key = 'RelayKitchen'; Kind = 'Level'
        Blurb = 'custom level: Bếp Tiếp Sức / Night Market Relay (needs OC2DIYLevel)'
        Id = 'manhl_relay_kitchen'
        Files = @('info_manhl_relay', 's_manhl_relay_kitchen_1', 'layout.png', 'build-report.json')
        LegacyId = $null }
    [pscustomobject]@{ Key = 'BepInEx'; Kind = 'Loader'
        Blurb = 'the mod loader itself (BepInEx 5 x86)'
        Id = $null; LegacyId = $null }
)

<#
    The OC2DIYLevel pack is third-party work by someone else (see its own READ ME) and is hundreds
    of megabytes of Unity asset bundles, so it is deliberately not committed to this repo and not
    put in the release zip. It is copied from a folder you point at with -DiyPackDir, or one found
    next to this installer, or one you paste in when asked.

    Its README tells you to delete the whole BepInEx\plugins folder before copying it in. That
    would take OC2Tweaks and RandomLevel with it, so instead only the pack's own entries are
    cleared before copying - which is what actually prevents the stale-level conflicts it warns
    about. What got copied is recorded in a manifest so removing the pack takes exactly those
    entries back out and nothing else.
#>
# Plugin folders that OC2Tweaks replaces, and the configs it takes settings over from.
$MergedInto      = 'OC2Tweaks'
$MergedFolders   = @('LobbyTools', 'QuickKeyRestart')
$MergedRestartId = @('com.manhl.overcooked2.quickkeyrestart', 'com.yourname.quickkeyrestart')
$MergedBansId    = 'com.manhl.overcooked2.lobbytools'

$PackName = 'OC2DIYLevel'
$PackManifestName = 'com.manhl.overcooked2.diypack.manifest'
# Fallback for a pack that was copied in by hand, before any manifest existed.
$PackKnownEntries = @(
    'OC2DIYLevel', 'OC2DIYLevelRuntimeWLoader', 'DIYLevelFastInit.dll', 'JonnyLevelLoaderPlugin.dll',
    'JonnyLevelScripts.dll', 'OC2ExtinguisherWash.dll', 'OC2ManyRecipes.dll', 'OC2RandomSize.dll',
    'OC2StageThumbnailPreview.dll', 'OC2ThrowAny.dll', 'Overcooked2RecipeViewer.dll'
)

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Note($msg) { Write-Host "    $msg" }
function Write-Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

<#
    Join-Path throws DriveNotFoundException for a path on a drive that does not exist, and with
    ErrorActionPreference = Stop that would end the script instead of just saying "not the game
    folder". A mistyped drive letter and an unplugged external drive both land here.
#>
function Test-GameDir($dir) {
    if (-not $dir) { return $false }
    try {
        return (Test-Path -LiteralPath (Join-Path $dir 'Overcooked2.exe')) -and
            (Test-Path -LiteralPath (Join-Path $dir 'Overcooked2_Data\Managed\Assembly-CSharp.dll'))
    } catch { return $false }
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
        # A library on a drive that is not plugged in must not end the search for the others.
        try {
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
        } catch {
            Write-Note "Skipped an unreachable Steam library: $lib"
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

# A BepInEx zip bundled next to the installer, whatever 5.x version it happens to be.
function Find-BepInExZip {
    foreach ($dir in $here, (Join-Path $here '..\dist')) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $found = Get-ChildItem -LiteralPath $dir -Filter 'BepInEx_win_x86_*.zip' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

# Release zip: RelayKitchen\ next to the installer. Repo checkout: ..\RelayKitchen\ in-place
# or ..\RelayKitchen\build\<id>\ after build_level.py.
function Find-RelayKitchenPayload {
    $levelId = 'manhl_relay_kitchen'
    foreach ($candidate in @(
        (Join-Path $here 'RelayKitchen'),
        (Join-Path $here '..\RelayKitchen'),
        (Join-Path $here "..\RelayKitchen\build\$levelId"),
        (Join-Path $here '..\dist\RelayKitchen')
    )) {
        try {
            if (-not (Test-Path -LiteralPath $candidate)) { continue }
            if (Test-Path -LiteralPath (Join-Path $candidate 'info_manhl_relay')) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        } catch {}
    }
    return $null
}

<#
    The newest stable 5.x win_x86 release. BepInEx 6 is a different loader for IL2CPP and .NET
    Core builds and its prereleases top the release list, so filtering to non-prerelease v5.* is
    what keeps "latest" meaning "latest thing that runs this game".
#>
function Get-LatestBepInEx {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/BepInEx/BepInEx/releases' `
            -UseBasicParsing -Headers @{ 'User-Agent' = 'GameMods-installer' }
        foreach ($release in $releases) {
            if ($release.prerelease -or $release.tag_name -notmatch '^v5\.') { continue }
            $asset = $release.assets | Where-Object { $_.name -match '^BepInEx_win_x86_.*\.zip$' } | Select-Object -First 1
            if ($asset) {
                return [pscustomobject]@{ Version = $release.tag_name.TrimStart('v'); Url = $asset.browser_download_url }
            }
        }
    } catch {
        Write-Warn "Could not ask GitHub for the newest BepInEx ($($_.Exception.Message))."
    }
    return $null
}

<#
    Strip the "came from the internet" tag (the Zone.Identifier stream) off what we just put in the
    game. Copy-Item carries that stream along, so files from a zip the browser marked stay marked.
    Unity's Mono almost certainly ignores it where .NET Framework would not, but it costs nothing to
    remove and it also stops Windows nagging about the files later.
#>
function Unblock-Copied($path) {
    try {
        if (Test-Path -LiteralPath $path -PathType Container) {
            Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue |
                Unblock-File -ErrorAction SilentlyContinue
        } else {
            Unblock-File -LiteralPath $path -ErrorAction SilentlyContinue
        }
    } catch {}
}

function Get-BepInExVersionAt($dir) {
    $core = Join-Path $dir 'BepInEx\core\BepInEx.dll'
    if (-not (Test-Path -LiteralPath $core)) { return $null }
    try { return (Get-Item -LiteralPath $core).VersionInfo.FileVersion } catch { return 'unknown' }
}

# --- Locate the game --------------------------------------------------------
if (-not $GameDir) {
    Write-Step 'Looking for Overcooked! 2 in your Steam libraries...'
    $GameDir = Find-GameDir
}
if (-not (Test-GameDir $GameDir)) {
    Write-Host 'Could not find the Steam (Mono) build of Overcooked! 2 automatically.' -ForegroundColor Yellow
    # Read-Host returns $null when there is no console to read from (a scheduler, piped input, CI):
    # fall back to an empty string so this fails with the message below instead of
    # "You cannot call a method on a null-valued expression".
    $typed = Read-Host 'Paste the game folder (the one containing Overcooked2.exe)'
    $GameDir = "$typed".Trim().Trim('"', ' ')
    if (-not (Test-GameDir $GameDir)) { Fail "Overcooked2.exe not found in '$GameDir'." }
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
Write-Note "Game folder: $GameDir"

$pluginRoot = [IO.Path]::GetFullPath((Join-Path $GameDir 'BepInEx\plugins'))
$configDir  = Join-Path $GameDir 'BepInEx\config'
$gameExe    = (Resolve-Path -LiteralPath (Join-Path $GameDir 'Overcooked2.exe')).Path

<#
    A loaded DLL cannot be replaced, so every round re-checks this rather than only once at
    startup: the menu stays open long enough for the game to be launched behind it.
#>
function Test-GameClosed {
    $running = @(Get-Process -Name 'Overcooked2' -ErrorAction SilentlyContinue | Where-Object {
        # Reading .Path throws for a process owned by another user or running elevated; treat an
        # unreadable path as "might be ours" rather than letting it end the script.
        $path = $null
        try { $path = $_.Path } catch {}
        (-not $path) -or ($path -eq $gameExe)
    })
    if ($running.Count) {
        Write-Host ''
        Write-Warn 'Overcooked! 2 is running. Close the game first; nothing was changed.'
        return $false
    }
    return $true
}

<#
    Steam is often under C:\Program Files (x86), which a normal user cannot write to. Check before
    touching anything, so the answer is "run it as administrator" rather than a PowerShell
    access-denied stack trace halfway through a copy.
#>
function Test-CanModifyGame {
    $probeDir = if (Test-Path -LiteralPath $pluginRoot) { $pluginRoot } else { $GameDir }
    $probe = Join-Path $probeDir ('.write-test-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType File -Path $probe -Force -ErrorAction Stop | Out-Null
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        return $true
    } catch {}

    $elevated = $false
    try {
        $elevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).
            IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {}

    Write-Host ''
    Write-Warn "Cannot write to $probeDir - nothing was changed."
    if ($elevated) {
        Write-Note 'This is already running as administrator, so something else is in the way: check the'
        Write-Note 'folder is not read-only, and that antivirus is not locking it.'
    } else {
        Write-Note 'The game is in a protected folder. Close this window, right-click Install.cmd (or'
        Write-Note 'Uninstall.cmd) and choose "Run as administrator", then try again.'
    }
    return $false
}

# Keep each mod inside plugins\ even if a plugin name ever gains a path separator.
function Get-PluginDir($name) {
    $dir = [IO.Path]::GetFullPath((Join-Path $pluginRoot $name))
    if (-not $dir.StartsWith($pluginRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Fail "The mod path for '$name' would land outside BepInEx\plugins."
    }
    return $dir
}

function Get-InstalledDll($target) {
    $path = Join-Path (Get-PluginDir $target.Key) "$($target.Key).dll"
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

function Get-LevelDir($target) {
    $levelsRoot = [IO.Path]::GetFullPath((Join-Path $pluginRoot "OC2DIYLevel\levels"))
    $dir = [IO.Path]::GetFullPath((Join-Path $levelsRoot $target.Id))
    if (-not $dir.StartsWith($levelsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Fail "The level path for '$($target.Key)' would land outside OC2DIYLevel\levels."
    }
    return $dir
}

# --- What each target looks like right now ----------------------------------
# Re-read at the top of every round so the menu reflects what just happened.
function Update-LoaderState {
    $script:loaderInstalled = (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\core\BepInEx.dll')) -and
                              (Test-Path -LiteralPath (Join-Path $GameDir 'winhttp.dll'))
    $script:loaderVersion   = Get-BepInExVersionAt $GameDir
}

# Only asked for when it is actually needed, so the common paths stay offline and instant.
$script:latestBepInEx = $null
$script:latestChecked = $false
function Get-LatestOnce {
    if (-not $script:latestChecked) {
        $script:latestChecked = $true
        $script:latestBepInEx = Get-LatestBepInEx
    }
    return $script:latestBepInEx
}

# --- The level pack ---------------------------------------------------------
function Get-PackManifestPath { return (Join-Path $configDir $PackManifestName) }

# What the pack put in plugins\: the manifest if we installed it, otherwise the known names.
function Get-PackEntries {
    $manifest = Get-PackManifestPath
    if (Test-Path -LiteralPath $manifest) {
        $listed = @(Get-Content -LiteralPath $manifest | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($listed.Count) { return $listed }
    }
    return $PackKnownEntries
}

function Get-PackInstalledEntries {
    if (-not (Test-Path -LiteralPath $pluginRoot)) { return @() }
    return @(Get-PackEntries | Where-Object { Test-Path -LiteralPath (Join-Path $pluginRoot $_) })
}

# A pack folder is one holding OC2DIYLevel\ - accept either the "plugins" folder or its parent.
function Test-PackSource($dir) {
    if (-not $dir) { return $false }
    try { return (Test-Path -LiteralPath (Join-Path $dir 'OC2DIYLevel')) } catch { return $false }
}
# Same drive-letter hazard as Test-GameDir: a pasted path can name a drive that is not there.
function Resolve-PackSource($dir) {
    if (-not $dir) { return $null }
    try {
        $dir = "$dir".Trim().Trim('"', ' ')
        if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { return $null }
        if (Test-PackSource $dir) { return (Resolve-Path -LiteralPath $dir).Path }
        $inner = Join-Path $dir 'plugins'
        if (Test-PackSource $inner) { return (Resolve-Path -LiteralPath $inner).Path }
    } catch {}
    return $null
}

function Find-PackSource {
    if ($DiyPackDir) {
        $resolved = Resolve-PackSource $DiyPackDir
        if (-not $resolved) { Fail "No OC2DIYLevel pack in '$DiyPackDir' (expected an OC2DIYLevel folder inside it)." }
        return $resolved
    }
    foreach ($candidate in (Join-Path $here 'plugins'), (Join-Path $here '..\dist\plugins'), $here) {
        $resolved = Resolve-PackSource $candidate
        if ($resolved) { return $resolved }
    }
    # Commonly still sitting in Downloads as the extracted pack folder.
    $downloads = Join-Path $env:USERPROFILE 'Downloads'
    if (Test-Path -LiteralPath $downloads) {
        foreach ($folder in Get-ChildItem -LiteralPath $downloads -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match 'OC2|Overcooked' } | Sort-Object LastWriteTime -Descending) {
            $resolved = Resolve-PackSource $folder.FullName
            if ($resolved) { return $resolved }
        }
    }
    return $null
}

function Install-DiyPack {
    $source = Find-PackSource
    if (-not $source) {
        # Taking "all of them" on a fresh machine should not stop to ask about a 400 MB download
        # nobody requested. Only prompt when the pack was picked out deliberately.
        if ($script:choseAll) {
            Write-Note 'Skipped OC2DIYLevel: it is a separate third-party download and none was found.'
            Write-Note 'Pick it on its own later, or pass -DiyPackDir "<folder>", once you have it.'
            return $false
        }
        Write-Warn 'Could not find the OC2DIYLevel pack automatically.'
        Write-Note 'It is not bundled with this installer: it is a large third-party pack you download yourself.'
        $typed = Read-Host 'Paste the pack folder (the one containing OC2DIYLevel), or Enter to skip'
        $source = Resolve-PackSource $typed
        if (-not $source) { Write-Warn 'Skipped the level pack.'; return $false }
    }
    Write-Note "Pack folder: $source"

    $entries = @(Get-ChildItem -LiteralPath $source -Force | ForEach-Object Name)
    if (-not $entries.Count) { Write-Warn "Nothing in '$source'; skipped the level pack."; return $false }

    $size = [math]::Round((Get-ChildItem -LiteralPath $source -Recurse -File -Force |
        Measure-Object Length -Sum).Sum / 1MB)
    Write-Note "Copying $($entries.Count) entries, about $size MB. This takes a while."

    New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null

    # Clear the pack's previous entries first (stale level files are what its README warns about),
    # then copy fresh. Entries outside the pack - our mods, anything you added - are not touched.
    # Parentheses matter: `Get-PackEntries + $entries` would pass "+" as an argument instead.
    foreach ($name in (@(Get-PackEntries) + $entries | Select-Object -Unique)) {
        $existing = Join-Path $pluginRoot $name
        if (Test-Path -LiteralPath $existing) { Remove-Item -LiteralPath $existing -Recurse -Force }
    }

    $i = 0
    foreach ($name in $entries) {
        $i++
        Write-Host ("    [{0}/{1}] {2}" -f $i, $entries.Count, $name)
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $pluginRoot -Recurse -Force
        Unblock-Copied (Join-Path $pluginRoot $name)
    }

    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    Set-Content -LiteralPath (Get-PackManifestPath) -Value $entries -Encoding UTF8
    Write-Note "Recorded $($entries.Count) pack entries, so removing it later takes back exactly these."
    return $true
}

function Remove-DiyPack {
    $entries = Get-PackInstalledEntries
    if (-not $entries.Count) { Write-Note "$PackName is not installed."; return }

    # Hundreds of megabytes that only come back from a download or a long copy, so confirm it the
    # way BepInEx does rather than deleting it on the way past during an "all of them".
    $size = 0
    try {
        $size = [math]::Round((Get-ChildItem -LiteralPath $pluginRoot -Recurse -File -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(OC2Tweaks|RandomLevel|LobbyTools|QuickKeyRestart)\\' } |
            Measure-Object Length -Sum).Sum / 1MB)
    } catch {}
    $go = $Yes
    if (-not $go) {
        Write-Host ''
        Write-Warn "About to delete the $PackName pack: $($entries.Count) entries$(if ($size) { ", about $size MB" })."
        Write-Note 'Putting it back means copying the whole pack in again from wherever you keep it.'
        $answer = Read-Host "Remove $PackName? [y/N]"
        $go = ("$answer".Trim() -in @('y', 'Y', 'yes'))
    }
    if (-not $go) { Write-Note "Left $PackName in place."; return }

    foreach ($name in $entries) {
        Remove-Item -LiteralPath (Join-Path $pluginRoot $name) -Recurse -Force
        Write-Host "    removed $name"
    }
    $manifest = Get-PackManifestPath
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
    Write-Note "$PackName removed ($($entries.Count) entries). Its own mod settings were left in place."
}

function Get-Status($target) {
    if ($target.Kind -eq 'Loader') {
        if (-not $script:loaderInstalled) { return 'not installed' }
        return "installed $script:loaderVersion"
    }
    if ($target.Kind -eq 'Pack') {
        $entries = Get-PackInstalledEntries
        if (-not $entries.Count) { return 'not installed' }
        $levels = @(Get-ChildItem -LiteralPath (Join-Path $pluginRoot 'OC2DIYLevel\levels') -Directory `
            -ErrorAction SilentlyContinue).Count
        if ($levels) { return "installed, $levels level sets" }
        return "installed, $($entries.Count) entries"
    }
    if ($target.Kind -eq 'Level') {
        $levelDir = Get-LevelDir $target
        if ($target.Files -and (Test-Path -LiteralPath (Join-Path $levelDir $target.Files[0]))) { return 'installed' }
        return 'not installed'
    }
    $dll = Get-InstalledDll $target
    if (-not $dll) { return 'not installed' }
    $version = (Get-Item -LiteralPath $dll).VersionInfo.FileVersion
    if ($version) { return "installed $version" }
    return 'installed'
}

# --- Choosing what to act on -------------------------------------------------
<#
    Deduplicate by Key, keeping the order chosen. Not Select-Object -Unique: these are
    [pscustomobject]s whose ToString() is empty, so -Unique sees them all as the same object and
    silently keeps only the first - which quietly turned "1,2" into just "1".
#>
function Select-UniqueTargets($targets) {
    $seen = @{}
    $result = @()
    foreach ($target in $targets) {
        if (-not $target) { continue }
        if ($seen.ContainsKey($target.Key)) { continue }
        $seen[$target.Key] = $true
        $result += $target
    }
    return @($result)
}

function Resolve-Named($names, $candidates) {
    $chosen = @()
    foreach ($name in $names) {
        foreach ($part in ($name -split '[,;\s]+' | Where-Object { $_ })) {
            if ($part -in @('all', 'a', '*')) { return @($candidates) }
            $match = $candidates | Where-Object { $_.Key -eq $part } | Select-Object -First 1
            if (-not $match) {
                $known = ($candidates | ForEach-Object Key) -join ', '
                Fail "Don't know '$part'. Choose from: $known (or -All)."
            }
            $chosen += $match
        }
    }
    return Select-UniqueTargets $chosen
}

function Select-Interactive($candidates, $verb) {
    Write-Host ''
    Write-Host "Which would you like to ${verb}?" -ForegroundColor Cyan
    Write-Host ''
    $width = ($candidates | ForEach-Object { $_.Blurb.Length } | Measure-Object -Maximum).Maximum
    for ($i = 0; $i -lt $candidates.Count; $i++) {
        $target = $candidates[$i]
        Write-Host ('  {0}) {1,-16} {2}  ' -f ($i + 1), $target.Key, $target.Blurb.PadRight($width)) -NoNewline
        Write-Host "[$(Get-Status $target)]" -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host '  A) all of them        Q) quit'
    Write-Host ''

    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $answer = Read-Host "Choose, e.g. 1,3 [A]"
        # No console to read from (a scheduler, piped input, CI). Take everything, and record it:
        # looping would re-prompt a prompt that can never be answered, forever.
        if ($null -eq $answer) {
            $script:noConsole = $true
            $script:choseAll = $true
            return @($candidates)
        }
        $answer = $answer.Trim()
        if (-not $answer -or $answer -in @('a', 'A', 'all')) {
            $script:choseAll = $true
            return @($candidates)
        }
        if ($answer -in @('q', 'Q', 'quit', 'exit')) { return $null }
        $script:choseAll = $false

        $chosen = @()
        $bad = $false
        foreach ($part in ($answer -split '[,;\s]+' | Where-Object { $_ })) {
            $match = $null
            if ($part -match '^\d+$') {
                $index = [int]$part - 1
                if ($index -ge 0 -and $index -lt $candidates.Count) { $match = $candidates[$index] }
            } else {
                $match = $candidates | Where-Object { $_.Key -eq $part } | Select-Object -First 1
            }
            if (-not $match) { Write-Warn "Not one of the options: $part"; $bad = $true; break }
            $chosen += $match
        }
        if (-not $bad -and $chosen.Count) { return Select-UniqueTargets $chosen }
    }
    Fail 'No valid choice made.'
}

# --- Doing it ---------------------------------------------------------------
function Remove-Level($target) {
    $levelDir = Get-LevelDir $target
    if (-not (Test-Path -LiteralPath $levelDir)) { Write-Note "$($target.Key) is not installed."; return }
    Remove-Item -LiteralPath $levelDir -Recurse -Force
    Write-Note "$($target.Key) removed."
}

function Invoke-Remove($selected) {
    foreach ($target in @($selected | Where-Object { $_.Kind -eq 'Mod' })) {
        $pluginDir = Get-PluginDir $target.Key
        # Only this mod's DLL: anything else the user put in the folder stays.
        $installed = Join-Path $pluginDir "$($target.Key).dll"
        if (Test-Path -LiteralPath $installed) {
            Remove-Item -LiteralPath $installed -Force
            Write-Note "$($target.Key) removed."
            # Tidy up the folder the installer made, but never one with anything else in it.
            if ((Test-Path -LiteralPath $pluginDir) -and -not (Get-ChildItem -LiteralPath $pluginDir -Force)) {
                Remove-Item -LiteralPath $pluginDir -Force
            }
        } else {
            Write-Note "$($target.Key) is not installed."
        }
    }

    if ($selected | Where-Object { $_.Kind -eq 'Pack' }) { Remove-DiyPack }

    foreach ($target in @($selected | Where-Object { $_.Kind -eq 'Level' })) { Remove-Level $target }

    if (-not ($selected | Where-Object { $_.Kind -eq 'Loader' })) { return }

    # Removing the loader stops every BepInEx mod in this game.
    $others = @()
    $packEntries = @(Get-PackInstalledEntries)
    if (Test-Path -LiteralPath $pluginRoot) {
        $keys = @($Targets | ForEach-Object Key) + $packEntries
        $others = @(Get-ChildItem -LiteralPath $pluginRoot -Force |
            Where-Object { $_.Name -notin $keys } | ForEach-Object Name)
    }
    Write-Host ''
    Write-Warn 'Removing BepInEx disables every mod in this game, including ones installed by hand.'
    if ($packEntries.Count) { Write-Warn "The $PackName pack ($($packEntries.Count) entries) would stop loading." }
    if ($others.Count) { Write-Warn "These would stop loading: $($others -join ', ')" }
    if ($PurgeBepInEx) {
        Write-Warn 'With -PurgeBepInEx, their files and all saved settings will be DELETED as well.'
    } else {
        Write-Note 'Their files and your settings are kept, so reinstalling BepInEx brings them back.'
    }

    $go = $Yes
    if (-not $go) {
        $answer = Read-Host 'Remove BepInEx? [y/N]'
        $go = ("$answer".Trim() -in @('y', 'Y', 'yes'))
    }
    if (-not $go) { Write-Note 'Left BepInEx in place.'; return }

    foreach ($relative in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') {
        $path = Join-Path $GameDir $relative
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    # BepInEx ships a changelog.txt into the game folder; only remove that one.
    $changelog = Join-Path $GameDir 'changelog.txt'
    if (Test-Path -LiteralPath $changelog) {
        $head = (Get-Content -LiteralPath $changelog -TotalCount 5 -ErrorAction SilentlyContinue) -join "`n"
        if ($head -match 'commits since v|BepInEx') { Remove-Item -LiteralPath $changelog -Force }
        else { Write-Note "Left changelog.txt alone; it does not look like BepInEx's." }
    }
    # core and cache are BepInEx's own and are rebuilt on install; the rest is user data.
    foreach ($relative in 'BepInEx\core', 'BepInEx\cache') {
        $path = Join-Path $GameDir $relative
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    if ($PurgeBepInEx) {
        foreach ($relative in 'BepInEx\plugins', 'BepInEx\config', 'BepInEx\patchers') {
            $path = Join-Path $GameDir $relative
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
    }
    $bepDir = Join-Path $GameDir 'BepInEx'
    if ((Test-Path -LiteralPath $bepDir) -and -not (Get-ChildItem -LiteralPath $bepDir -Force)) {
        Remove-Item -LiteralPath $bepDir -Force
    }
    Write-Note 'BepInEx removed.'
    if (-not $PurgeBepInEx -and (Test-Path -LiteralPath $bepDir)) {
        Write-Note "Kept your plugins and settings in $bepDir (use -PurgeBepInEx to delete them too)."
    }
}

function Install-BepInEx {
    $zip = Find-BepInExZip
    $downloaded = $false
    $wanted = $null

    if ($BepInExVersion) {
        # An explicit version wins over a bundled zip of a different one.
        $expected = "BepInEx_win_x86_$BepInExVersion.zip"
        if (-not $zip -or (Split-Path -Leaf $zip) -ne $expected) {
            $zip = $null
            $wanted = [pscustomobject]@{ Version = $BepInExVersion
                Url = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/$expected" }
        }
    } elseif ($zip) {
        Write-Note "Using the BepInEx zip next to the installer: $(Split-Path -Leaf $zip)"
    } else {
        $wanted = Get-LatestOnce
        if (-not $wanted) {
            $wanted = [pscustomobject]@{ Version = $BepInExFallback
                Url = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExFallback/BepInEx_win_x86_$BepInExFallback.zip" }
            Write-Warn "Falling back to the pinned BepInEx $BepInExFallback."
        }
    }

    $upToDate = $false
    if ($script:loaderInstalled -and $wanted -and $script:loaderVersion -and $script:loaderVersion -ne 'unknown') {
        try { $upToDate = ([version]$script:loaderVersion -ge [version]$wanted.Version) } catch {}
    }

    if ($script:loaderInstalled -and $upToDate -and -not $BepInExVersion) {
        Write-Step "BepInEx $script:loaderVersion is already current."
        return
    }

    if ($script:loaderInstalled) {
        Write-Step "Updating BepInEx $script:loaderVersion -> $(if ($wanted) { $wanted.Version } else { 'bundled zip' }) (x86)..."
    } else {
        Write-Step "Installing BepInEx $(if ($wanted) { $wanted.Version } else { 'from the bundled zip' }) (x86)..."
    }

    if (-not $zip) {
        $zip = Join-Path $env:TEMP (Split-Path -Leaf ([Uri]$wanted.Url).AbsolutePath)
        Write-Note "Downloading $($wanted.Url)"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $ProgressPreference = 'SilentlyContinue'   # the progress bar makes 5.1's download crawl
        try {
            Invoke-WebRequest -Uri $wanted.Url -OutFile $zip -UseBasicParsing
        } catch {
            Fail "Download failed: $($_.Exception.Message)"
        }
        $downloaded = $true
    }

    # The 5.x zip holds only the loader and BepInEx\core, so this never touches plugins or configs.
    Expand-Archive -LiteralPath $zip -DestinationPath $GameDir -Force
    if ($downloaded) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
    Unblock-Copied (Join-Path $GameDir 'BepInEx\core')
    foreach ($loaderFile in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') {
        $path = Join-Path $GameDir $loaderFile
        if (Test-Path -LiteralPath $path) { Unblock-Copied $path }
    }

    Update-LoaderState
    Write-Note "BepInEx is now $script:loaderVersion."
}

<#
    Carries a LobbyTools + QuickKeyRestart setup over to OC2Tweaks, which replaced both.

    Three things have to move, or installing the merged mod looks like a clean install and
    quietly loses what the user had set:
      - the ban list, which is named after the plugin GUID like the config is;
      - the restart key, which lived in a config of its own and now belongs to a section of
        OC2Tweaks' config (the key alone is written, and BepInEx fills in the rest on load);
      - the old plugin folders, which otherwise keep loading beside OC2Tweaks: two F6 panels,
        two handlers on the restart key, and two mods writing one ban file.

    Everything it reads is left on disk apart from the plugin DLLs, so nothing here loses a
    setting if it guesses wrong.
#>
function Convert-MergedMod {
    $newId = 'com.manhl.overcooked2.tweaks'

    # The ban list: same name as the config, different extension.
    $oldBans = Join-Path $configDir "$MergedBansId.bans.tsv"
    $newBans = Join-Path $configDir "$newId.bans.tsv"
    if ((Test-Path -LiteralPath $oldBans) -and -not (Test-Path -LiteralPath $newBans)) {
        Move-Item -LiteralPath $oldBans -Destination $newBans
        Write-Note 'Kept your ban list from LobbyTools.'
    }

    # The restart key, out of whichever of QuickKeyRestart's configs is there.
    $newConfig = Join-Path $configDir "$newId.cfg"
    $already = $false
    if (Test-Path -LiteralPath $newConfig) {
        $already = [IO.File]::ReadAllText($newConfig) -match '(?m)^\s*RestartKey\s*='
    }
    if (-not $already) {
        foreach ($oldId in $MergedRestartId) {
            $oldConfig = Join-Path $configDir "$oldId.cfg"
            if (-not (Test-Path -LiteralPath $oldConfig)) { continue }
            $match = [regex]::Match([IO.File]::ReadAllText($oldConfig), '(?m)^\s*RestartKey\s*=\s*(.+?)\s*$')
            if (-not $match.Success) { continue }
            # Follow the endings already in the file rather than forcing CRLF, so an edited or
            # hand-written config does not come back with mixed endings. BepInEx writes CRLF.
            $existing = if (Test-Path -LiteralPath $newConfig) { [IO.File]::ReadAllText($newConfig) } else { '' }
            $eol = if ($existing -match '[^\r]\n') { "`n" } else { "`r`n" }

            # A bare "Section/Key = value" is all BepInEx needs to read a setting back; it
            # rewrites the file with the descriptions and defaults the next time the game runs.
            $section = "[QuickRestart]$eol" + "RestartKey = $($match.Groups[1].Value)"
            $body = if ($existing) { $existing.TrimEnd() + $eol + $eol + $section } else { $section }
            [IO.File]::WriteAllText($newConfig, $body + $eol, (New-Object Text.UTF8Encoding($false)))
            Write-Note "Kept your restart key ($($match.Groups[1].Value)) from QuickKeyRestart."
            break
        }
    }

    # The superseded plugins themselves.
    foreach ($folder in $MergedFolders) {
        $dir = Get-PluginDir $folder
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        try {
            Remove-Item -LiteralPath $dir -Recurse -Force
            Write-Note "Removed the old $folder plugin, now part of $MergedInto."
        } catch {
            Write-Warn "Could not remove the old $folder plugin at $dir : $($_.Exception.Message)"
            Write-Warn "Delete that folder by hand, or it keeps loading alongside $MergedInto."
        }
    }
}
function Install-Level($target) {
    $source = Find-RelayKitchenPayload
    if (-not $source) {
        Write-Warn "$($target.Key): level files not found next to this installer or in ..\RelayKitchen\build after build_level.py."
        return $false
    }
    Write-Note "Level payload: $source"

    $diy = Join-Path $pluginRoot 'OC2DIYLevel'
    foreach ($name in 'OC2DIYLevel.dll', 'common01', 'common02', 'common03') {
        if (-not (Test-Path -LiteralPath (Join-Path $diy $name))) {
            Write-Warn "Skipped $($target.Key): install OC2DIYLevel first (missing $name)."
            return $false
        }
    }

    foreach ($name in $target.Files) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $name))) {
            Write-Warn "$($target.Key): $name not found in $source; run build_level.py first."
            return $false
        }
    }

    $reportPath = Join-Path $source 'build-report.json'
    $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($name in ($target.Files | Where-Object { $_ -ne 'build-report.json' })) {
        $expected = $report.outputs.$name
        if (-not $expected -or (Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash -ne $expected) {
            Write-Warn "$($target.Key): checksum mismatch on $name; rebuild the level first."
            return $false
        }
    }
    foreach ($name in 'common01', 'common02', 'common03') {
        if ((Get-FileHash -LiteralPath (Join-Path $diy $name) -Algorithm SHA256).Hash -ne $report.inputs.$name) {
            Write-Warn "$($target.Key): built for a different OC2DIYLevel version ($name checksum mismatch); rebuild the level."
            return $false
        }
    }

    $levelDir = Get-LevelDir $target
    New-Item -ItemType Directory -Force -Path $levelDir | Out-Null
    foreach ($name in $target.Files) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $levelDir $name) -Force
        Unblock-Copied (Join-Path $levelDir $name)
    }
    Write-Note "Installed $($target.Key) into $levelDir"
    return $true
}

function Invoke-Install($selected) {
    $payloads = @()
    foreach ($target in @($selected | Where-Object { $_.Kind -eq 'Mod' })) {
        $dll = Find-Payload "$($target.Key).dll"
        if ($dll) {
            $payloads += [pscustomobject]@{ Target = $target; Dll = $dll }
        } else {
            Write-Warn "$($target.Key).dll not found next to this installer (or in ..\dist - run $($target.Key)\build.cmd first); skipping it."
        }
    }
    $loaderSelected = [bool]($selected | Where-Object { $_.Kind -eq 'Loader' })
    $packSelected   = [bool]($selected | Where-Object { $_.Kind -eq 'Pack' })
    $levelTargets   = @($selected | Where-Object { $_.Kind -eq 'Level' })

    if (-not $payloads.Count -and -not $loaderSelected -and -not $packSelected -and -not $levelTargets.Count) {
        Write-Warn "No mod DLLs found. Keep them next to this installer, or run each mod's build.cmd first."
        return $false
    }

    # Nothing loads without the loader, so bring BepInEx along rather than appearing to succeed.
    if (($payloads.Count -or $packSelected) -and -not $script:loaderInstalled -and -not $loaderSelected) {
        Write-Note 'BepInEx is not installed yet; adding it, because nothing loads without it.'
        $loaderSelected = $true
    }

    if ($loaderSelected) { Install-BepInEx }

    $packInstalled = $false
    if ($packSelected) {
        Write-Step "Installing $PackName..."
        $packInstalled = Install-DiyPack
    }

    # Level targets need OC2DIYLevel; re-check after the pack install in case both were selected.
    $levelTargets = @($levelTargets | Where-Object {
        if (-not (Test-Path -LiteralPath (Join-Path $pluginRoot 'OC2DIYLevel\OC2DIYLevel.dll'))) {
            Write-Warn "Skipped $($_.Key): install OC2DIYLevel first (choose it from the menu, or use -Only OC2DIYLevel -DiyPackDir <folder>)."
            $false
        } else { $true }
    })

    # This add-on cannot load without the third-party plugin. Re-check after installing the pack,
    # since both may have been selected in the same round.
    $payloads = @($payloads | Where-Object {
        if ($_.Target.Key -eq 'RandomLevel' -and -not (Test-Path -LiteralPath (Join-Path $pluginRoot 'OC2DIYLevel\OC2DIYLevel.dll'))) {
            Write-Warn 'Skipped RandomLevel: install OC2DIYLevel first (choose it from the menu, or use -Only OC2DIYLevel -DiyPackDir <folder>).'
            $false
        } else { $true }
    })

    foreach ($payload in $payloads) {
        $target    = $payload.Target
        $pluginDir = Get-PluginDir $target.Key
        Write-Step "Installing $($target.Key)..."
        New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
        $destination = Join-Path $pluginDir "$($target.Key).dll"
        Copy-Item -LiteralPath $payload.Dll -Destination $destination -Force
        Unblock-Copied $destination

        # The mod's GUID changed when it moved into this repo. BepInEx names the config after the
        # GUID, so carry the old file over rather than leaving the user's key binding behind.
        if ($target.LegacyId) {
            $legacy  = Join-Path $configDir "$($target.LegacyId).cfg"
            $current = Join-Path $configDir "$($target.Id).cfg"
            if ((Test-Path -LiteralPath $legacy) -and -not (Test-Path -LiteralPath $current)) {
                Move-Item -LiteralPath $legacy -Destination $current
                Write-Note "Kept your existing settings (was $($target.LegacyId).cfg)."
            }
        }
    }

    if ($payloads | Where-Object { $_.Target.Key -eq $MergedInto }) {
        Convert-MergedMod
        # Pre-release builds wrote a [Connection filter] section the mod no longer reads; drop it
        # without touching the hotkeys, the force-host choice or the ban list.
        $tweaksConfig = Join-Path $configDir 'com.manhl.overcooked2.tweaks.cfg'
        if (Test-Path -LiteralPath $tweaksConfig) {
            $originalConfig = [IO.File]::ReadAllText($tweaksConfig)
            $updatedConfig = [regex]::Replace($originalConfig, '(?ms)^\[Connection filter\][^\r\n]*\r?\n.*?(?=^\[|\z)', '')
            if ($updatedConfig -ne $originalConfig) {
                [IO.File]::WriteAllText($tweaksConfig, $updatedConfig, (New-Object Text.UTF8Encoding($false)))
            }
        }
    }

    $levelsInstalled = @()
    foreach ($target in $levelTargets) {
        Write-Step "Installing $($target.Key)..."
        if (Install-Level $target) { $levelsInstalled += $target.Key }
    }

    $installedNames = @($payloads | ForEach-Object { $_.Target.Key })
    if ($packInstalled) { $installedNames += $PackName }
    $installedNames += $levelsInstalled

    Write-Host ''
    if ($installedNames.Count) {
        Write-Host "Installed: $($installedNames -join ', ')." -ForegroundColor Green
    } else {
        Write-Host "BepInEx $script:loaderVersion is in place. Nothing else was installed." -ForegroundColor Green
    }
    if ($installedNames -contains $MergedInto) {
        Write-Note 'OC2Tweaks: press F6 for the panel (in Vietnamese), F5 while playing to restart the level.'
        Write-Note '           Its Phim tat tab switches the restart key off or rebinds either key. Host only.'
    }
    if ($installedNames -contains 'RandomLevel') {
        Write-Note 'RandomLevel: More Levels -> Choi ngau nhien. F8 = skip round, Continue = next random level, Restart = same level, F7 = main menu at results. Host only.'
    }
    if ($installedNames -contains $PackName) {
        Write-Note "$PackName : custom levels. Everyone in the match needs the same pack."
    }
    if ($installedNames -contains 'RelayKitchen') {
        Write-Note 'RelayKitchen: More Levels → GameMods Originals → Bếp Tiếp Sức — Chợ Đêm / Night Market Relay.'
        Write-Note '              Everyone in the match needs the same map and OC2DIYLevel pack.'
    }
    return $true
}

# --- The menu, round after round --------------------------------------------
# Flags mean "do this one thing": looping on them would never end.
$oneShot = [bool]($All -or $Only -or $Yes)
$didWork = $false
$script:noConsole = $false
$script:choseAll  = $false

# Interactive: when no mode flag was passed, ask install or uninstall first.
if (-not $oneShot -and -not $Uninstall) {
    Write-Host ''
    Write-Host 'What would you like to do?' -ForegroundColor Cyan
    Write-Host '  I) Install or upgrade mods'
    Write-Host '  U) Uninstall mods'
    Write-Host ''
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $modeAnswer = Read-Host 'Choose [I]'
        if ($null -eq $modeAnswer) { $script:noConsole = $true; break }
        $modeAnswer = "$modeAnswer".Trim()
        if (-not $modeAnswer -or $modeAnswer -in @('i', 'I')) { break }
        if ($modeAnswer -in @('u', 'U')) { $Uninstall = $true; break }
        Write-Warn "Enter I to install or U to uninstall."
    }
}

$verb = if ($Uninstall) { 'remove' } else { 'install' }

while ($true) {
    Update-LoaderState

    if ($Uninstall) {
        # Nothing to offer for something that is not there.
        # $target, not $_: switch rebinds $_ to its own input (the Kind string), so passing $_
        # into Get-InstalledDll here would hand it "Mod" instead of the target object.
        $candidates = @($Targets | Where-Object {
            $target = $_
            switch ($target.Kind) {
                'Loader' { $script:loaderInstalled }
                'Pack'   { @(Get-PackInstalledEntries).Count -gt 0 }
                'Level'  { $target.Files -and (Test-Path -LiteralPath (Join-Path (Get-LevelDir $target) $target.Files[0])) }
                default  { $null -ne (Get-InstalledDll $target) }
            }
        })
        if (-not $candidates.Count) {
            Write-Host ''
            Write-Step $(if ($didWork) { 'Everything is removed; nothing left to do.' }
                         else { 'Nothing is installed; nothing to remove.' })
            break
        }
    } else {
        $candidates = @($Targets)
    }

    # choseAll distinguishes "everything" from a deliberate pick, so optional extras that need a
    # separate download can be skipped quietly instead of interrogating someone who pressed Enter.
    if ($All)      { $selected = @($candidates); $script:choseAll = $true }
    elseif ($Only) { $selected = Resolve-Named $Only $candidates; $script:choseAll = $false }
    elseif ($Yes)  { $selected = @($candidates); $script:choseAll = $true }
    else           { $selected = Select-Interactive $candidates $verb }

    # Q at the menu.
    if ($null -eq $selected) {
        Write-Host ''
        Write-Host $(if ($didWork) { 'Done. Closing.' } else { 'Nothing to do. Closing.' })
        break
    }

    Write-Host ''
    Write-Step "$(if ($Uninstall) { 'Removing' } else { 'Installing' }): $(($selected | ForEach-Object Key) -join ', ')"

    # Both re-checked every round: the game may have been started, or a folder locked, since the
    # last one. Interactively these loop back to the menu; with nobody to re-prompt they fail.
    if (-not (Test-GameClosed)) {
        if ($oneShot -or $script:noConsole) { exit 1 }
    } elseif (-not (Test-CanModifyGame)) {
        if ($oneShot -or $script:noConsole) { exit 1 }
    } else {
        if ($Uninstall) { Invoke-Remove $selected; $didWork = $true }
        elseif (Invoke-Install $selected) { $didWork = $true }
    }

    if ($oneShot -or $script:noConsole) { break }

    Write-Host ''
    Write-Host ('  ' + ('-' * 66)) -ForegroundColor DarkGray
}
