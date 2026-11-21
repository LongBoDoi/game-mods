<#
    Checks that a LobbyTools + QuickKeyRestart setup becomes an OC2Tweaks setup without losing
    anything, by running the real installer against a throwaway game folder.

    OC2Tweaks 2.0.0 merged those two plugins into one. The migration is the part of that change
    nobody notices until it is wrong: a user upgrades, their ban list and their rebound keys are
    silently back to defaults, and the superseded plugins are still loading alongside the new one.

    Needs OC2Tweaks\build.cmd to have run, and a real BepInEx.dll to copy in so the installer
    sees a loader and never reaches the network.

        powershell -NoProfile -File tests\CheckMerge.ps1
#>
param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Overcooked! 2',
    [string]$Installer = (Join-Path $PSScriptRoot '..\installer\Install.ps1')
)
$ErrorActionPreference = 'Stop'

$bepInEx = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
if (-not (Test-Path -LiteralPath $bepInEx)) {
    throw "Need a real BepInEx.dll to copy into the sandbox; not found at $bepInEx. Pass -GameDir."
}
$Installer = (Resolve-Path -LiteralPath $Installer).Path

$root = Join-Path ([IO.Path]::GetTempPath()) ('oc2merge-' + [Guid]::NewGuid().ToString('N'))
$failed = @()
$checked = 0
function Check($ok, $what) {
    if ($ok) { $script:checked++ } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:failed += $what }
}

try {
    # --- a game that already has the two old mods, their settings and someone else's mod ----
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'Overcooked2_Data\Managed') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'BepInEx\core') | Out-Null
    foreach ($stub in 'Overcooked2.exe', 'winhttp.dll', 'Overcooked2_Data\Managed\Assembly-CSharp.dll') {
        Set-Content -LiteralPath (Join-Path $root $stub) -Value 'stub' -Encoding Ascii
    }
    Copy-Item -LiteralPath $bepInEx -Destination (Join-Path $root 'BepInEx\core\BepInEx.dll')

    $plugins = Join-Path $root 'BepInEx\plugins'
    $config  = Join-Path $root 'BepInEx\config'
    New-Item -ItemType Directory -Force -Path $config | Out-Null
    foreach ($mod in 'LobbyTools', 'QuickKeyRestart', 'SomeoneElsesMod') {
        New-Item -ItemType Directory -Force -Path (Join-Path $plugins $mod) | Out-Null
        Set-Content -LiteralPath (Join-Path $plugins "$mod\$mod.dll") -Value 'old' -Encoding Ascii
    }

    # Settings the user had chosen. The [Connection filter] section is from a pre-release build
    # and is expected to be dropped; everything else has to survive.
    $lobbyCfg = @"
[General]

## Mo/dong bang quan ly phong.
# Setting type: KeyCode
# Default value: F6
ToggleKey = F8

[Matchmaking]
ForceHost = true

[Connection filter]
MaxPing = 250
"@
    [IO.File]::WriteAllText((Join-Path $config 'com.manhl.overcooked2.lobbytools.cfg'),
        ($lobbyCfg -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $config 'com.manhl.overcooked2.quickkeyrestart.cfg'),
        "[General]`r`nRestartKey = R + LeftControl`r`n", (New-Object Text.UTF8Encoding($false)))
    $bansPath = Join-Path $config 'com.manhl.overcooked2.lobbytools.bans.tsv'
    [IO.File]::WriteAllText($bansPath,
        "OC2LobbyTools bans v1`n76561197960265728`tQWJ1c2Vy`t2025-01-02T03:04:05.0000000Z`n",
        (New-Object Text.UTF8Encoding($false)))
    $bansBefore = [IO.File]::ReadAllBytes($bansPath)

    # --- install the merged mod -------------------------------------------------------------
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Installer -GameDir $root -Only OC2Tweaks -Yes |
        Out-Null
    Check ($LASTEXITCODE -eq 0) "installer exited 0 (was $LASTEXITCODE)"

    Check (Test-Path -LiteralPath (Join-Path $plugins 'OC2Tweaks\OC2Tweaks.dll')) 'OC2Tweaks.dll installed'
    # Left behind, these keep loading beside OC2Tweaks: two F6 panels, two handlers on the
    # restart key, and two mods writing one ban file.
    Check (-not (Test-Path -LiteralPath (Join-Path $plugins 'LobbyTools'))) 'superseded LobbyTools plugin removed'
    Check (-not (Test-Path -LiteralPath (Join-Path $plugins 'QuickKeyRestart'))) 'superseded QuickKeyRestart plugin removed'
    Check (Test-Path -LiteralPath (Join-Path $plugins 'SomeoneElsesMod\SomeoneElsesMod.dll')) 'unrelated plugin left alone'

    $newCfg = Join-Path $config 'com.manhl.overcooked2.tweaks.cfg'
    Check (Test-Path -LiteralPath $newCfg) 'config carried over to the new GUID'
    if (Test-Path -LiteralPath $newCfg) {
        $body = [IO.File]::ReadAllText($newCfg)
        Check ($body -match '(?m)^ToggleKey = F8\s*$')                'rebound panel key kept'
        Check ($body -match '(?m)^ForceHost = true\s*$')              'force-host choice kept'
        Check ($body -match '(?m)^\[QuickRestart\]\s*$')              '[QuickRestart] section written'
        Check ($body -match '(?m)^RestartKey = R \+ LeftControl\s*$') 'rebound restart key carried over'
        Check ($body -notmatch '\[Connection filter\]')               'dead [Connection filter] section dropped'
        Check ($body -notmatch 'MaxPing')                             'dead setting inside it dropped'
        Check ($body -notmatch "[^`r]`n")                             'line endings stayed CRLF, not mixed'
    }
    $newBans = Join-Path $config 'com.manhl.overcooked2.tweaks.bans.tsv'
    Check (Test-Path -LiteralPath $newBans) 'ban list carried over to the new GUID'
    if (Test-Path -LiteralPath $newBans) {
        Check ([Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($newBans), $bansBefore)) 'ban list byte-identical'
    }
    Check (-not (Test-Path -LiteralPath $bansPath)) 'ban list moved rather than copied'

    # --- installing again must not migrate twice ---------------------------------------------
    $cfgBefore = [IO.File]::ReadAllText($newCfg)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Installer -GameDir $root -Only OC2Tweaks -Yes |
        Out-Null
    Check ($LASTEXITCODE -eq 0) 'reinstall exited 0'
    Check ([IO.File]::ReadAllText($newCfg) -eq $cfgBefore) 'reinstall left the config alone (no second [QuickRestart])'

    # --- uninstalling keeps the settings, as every other mod here does ------------------------
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Installer -GameDir $root -Uninstall -Only OC2Tweaks -Yes |
        Out-Null
    Check ($LASTEXITCODE -eq 0) 'uninstall exited 0'
    Check (-not (Test-Path -LiteralPath (Join-Path $plugins 'OC2Tweaks\OC2Tweaks.dll'))) 'uninstall removed the DLL'
    Check (Test-Path -LiteralPath $newCfg) 'uninstall kept the config'
    Check (Test-Path -LiteralPath $newBans) 'uninstall kept the ban list'
    Check (Test-Path -LiteralPath (Join-Path $plugins 'SomeoneElsesMod\SomeoneElsesMod.dll')) 'uninstall left the unrelated plugin alone'

    if ($failed.Count) { throw "$($failed.Count) check(s) failed." }
    Write-Host "PASS: $checked checks (LobbyTools + QuickKeyRestart migrate to OC2Tweaks with settings intact)."
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
