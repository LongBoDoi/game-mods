<#
    Checks what Install.ps1 -Update (what Mod Updater runs when the game closes) installs, by
    running the real installer against a throwaway game folder.

    An update upgrades the mods that are there and adds the release's mods the player doesn't
    have yet, so a mod that arrived in a later release (TiengViet, DIYChefFix) reaches people who
    update through Mod Updater. It must not bring back a mod the player removed, and must not
    add a companion whose third-party plugin is missing. Mods that left the collection
    (OC2Tweaks, the Relay Kitchen level) are taken back out.

    Needs every mod's build.cmd to have run (..\dist), and a real BepInEx.dll to copy in so the
    installer sees a loader and never reaches the network.

        powershell -NoProfile -File tests\CheckUpdate.ps1
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

$root = Join-Path ([IO.Path]::GetTempPath()) ('oc2update-' + [Guid]::NewGuid().ToString('N'))
$failed = @()
$checked = 0
function Check($ok, $what) {
    if ($ok) { $script:checked++ } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:failed += $what }
}

$plugins = Join-Path $root 'BepInEx\plugins'
$config  = Join-Path $root 'BepInEx\config'
function Installed($mod) { Test-Path -LiteralPath (Join-Path $plugins "$mod\$mod.dll") }
function Invoke-Installer([string[]]$arguments) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Installer -GameDir $root @arguments | Out-Null
    return $LASTEXITCODE
}

try {
    # --- a player on an older release: ChefSkills and Mod Updater, plus the retired mods -------
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'Overcooked2_Data\Managed') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'BepInEx\core') | Out-Null
    New-Item -ItemType Directory -Force -Path $config | Out-Null
    foreach ($stub in 'Overcooked2.exe', 'winhttp.dll', 'Overcooked2_Data\Managed\Assembly-CSharp.dll') {
        Set-Content -LiteralPath (Join-Path $root $stub) -Value 'stub' -Encoding Ascii
    }
    Copy-Item -LiteralPath $bepInEx -Destination (Join-Path $root 'BepInEx\core\BepInEx.dll')
    foreach ($stub in 'ChefSkills\ChefSkills.dll', 'ModUpdater\ModUpdater.dll', 'OC2Tweaks\OC2Tweaks.dll',
                      'RandomLevel\RandomLevel.dll', 'RandomLevel\keep.txt',
                      'OC2DIYLevel\OC2DIYLevel.dll', 'OC2DIYLevel\levels\manhl_relay_kitchen\info_manhl_relay') {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent (Join-Path $plugins $stub)) | Out-Null
        Set-Content -LiteralPath (Join-Path $plugins $stub) -Value 'old' -Encoding Ascii
    }
    # They took EmoteSync out at some point.
    Set-Content -LiteralPath (Join-Path $config 'com.manhl.overcooked2.removed.txt') -Value 'EmoteSync' -Encoding Ascii

    Check ((Invoke-Installer '-Update') -eq 0) 'update exited 0'
    Check ((Get-Content -LiteralPath (Join-Path $plugins 'ChefSkills\ChefSkills.dll') -Raw) -ne "old`r`n") 'installed mod upgraded'
    Check (Test-Path -LiteralPath (Join-Path $plugins 'ModUpdater\ModUpdater.ps1')) 'Mod Updater upgraded with its script'
    Check (Installed 'TiengViet') 'update added TiengViet'
    Check (-not (Test-Path -LiteralPath (Join-Path $plugins 'OC2Tweaks'))) 'retired OC2Tweaks removed with its folder'
    Check ((Get-Content -LiteralPath (Join-Path $plugins 'RandomLevel\RandomLevel.dll') -Raw) -ne "old`r`n") 'RandomLevel upgraded with OC2DIYLevel there'
    Check (Test-Path -LiteralPath (Join-Path $plugins 'RandomLevel\keep.txt')) 'other files in its folder kept'
    Check (-not (Test-Path -LiteralPath (Join-Path $plugins 'OC2DIYLevel\levels\manhl_relay_kitchen'))) 'retired Relay Kitchen level removed'
    Check (-not (Installed 'EmoteSync')) 'update did not bring back a mod the player removed'
    Check (-not (Installed 'DIYChefFix')) 'update did not add DIYChefFix without DIYChef'

    # --- DIYChef appears: the next update adds its companion ----------------------------------
    New-Item -ItemType Directory -Force -Path (Join-Path $plugins 'OC2DIYChef-FastInit') | Out-Null
    Set-Content -LiteralPath (Join-Path $plugins 'OC2DIYChef-FastInit\DIYChefFastInit.dll') -Value 'third party' -Encoding Ascii
    Check ((Invoke-Installer '-Update') -eq 0) 'second update exited 0'
    Check (Installed 'DIYChefFix') 'update added DIYChefFix once DIYChef is there'

    # --- removing a mod keeps it out of later updates; installing it again lets it back in -----
    Check ((Invoke-Installer '-Uninstall', '-Only', 'TiengViet', '-Yes') -eq 0) 'uninstall exited 0'
    Check ((Invoke-Installer '-Update') -eq 0) 'third update exited 0'
    Check (-not (Installed 'TiengViet')) 'update did not bring back TiengViet after it was removed'
    Check ((Invoke-Installer '-Only', 'EmoteSync', '-Yes') -eq 0) 'install EmoteSync exited 0'
    $removed = @(Get-Content -LiteralPath (Join-Path $config 'com.manhl.overcooked2.removed.txt') -ErrorAction SilentlyContinue)
    Check ($removed -notcontains 'EmoteSync') 'installing EmoteSync again took it off the removed list'
    Check ($removed -contains 'TiengViet') 'TiengViet still on the removed list'

    # --- a companion chosen by hand without its third-party plugin is skipped -----------------
    Remove-Item -LiteralPath (Join-Path $plugins 'OC2DIYChef-FastInit') -Recurse -Force
    Check ((Invoke-Installer '-Uninstall', '-Only', 'DIYChefFix', '-Yes') -eq 0) 'uninstall DIYChefFix exited 0'
    Invoke-Installer '-Only', 'DIYChefFix', '-Yes' | Out-Null
    Check (-not (Installed 'DIYChefFix')) 'DIYChefFix skipped without DIYChef'

    if ($failed.Count) { throw "$($failed.Count) check(s) failed." }
    Write-Host "PASS: $checked checks (-Update upgrades, adds new mods, respects removals and requirements, drops retired mods)."
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
