<#
    Checks the built QuickKeyRestart against the installed game. The mod reaches the game
    entirely by reflection, so nothing it looks up by name is verified by the compiler:
    every one of those names is asserted here instead, and a game update that renames one
    is a failed check rather than a dead key in game.

    Run it after QuickKeyRestart\build.cmd:
        powershell -NoProfile -File tests\CheckQuickKeyRestart.ps1
#>
param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Overcooked! 2',
    [string]$PluginPath = (Join-Path $PSScriptRoot '..\dist\QuickKeyRestart.dll')
)
$ErrorActionPreference = 'Stop'

Add-Type -Path (Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.RemoveSearchDirectory('.')
$resolver.RemoveSearchDirectory('bin')
$resolver.AddSearchDirectory((Join-Path $GameDir 'Overcooked2_Data\Managed'))
$resolver.AddSearchDirectory((Join-Path $GameDir 'BepInEx\core'))
$reader = New-Object Mono.Cecil.ReaderParameters
$reader.AssemblyResolver = $resolver

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $PluginPath).Path, $reader)
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Overcooked2_Data\Managed\Assembly-CSharp.dll'), $reader)
try {
    $failed = @()
    $checked = 0

    # --- everything the mod compiles against still exists ------------------------------------------
    $references = @($assembly.MainModule.GetTypeReferences()) + @($assembly.MainModule.GetMemberReferences())
    foreach ($reference in $references) {
        if ($reference -isnot [Mono.Cecil.TypeReference] -and
            $reference -isnot [Mono.Cecil.MethodReference] -and
            $reference -isnot [Mono.Cecil.FieldReference]) { continue }
        try {
            if (-not $reference.Resolve()) { $failed += "Missing runtime reference: $($reference.FullName)" }
            else { $checked++ }
        } catch { $failed += "Cannot resolve $($reference.FullName): $($_.Exception.Message)" }
    }

    # The game is looked up by name at runtime, never compiled against, so that a renamed type
    # disables the mod with a log line instead of stopping BepInEx from loading it at all.
    if ($assembly.MainModule.AssemblyReferences | Where-Object { $_.Name -eq 'Assembly-CSharp' }) {
        $failed += 'QuickKeyRestart.dll references Assembly-CSharp; it should only look the game up by name'
    } else { $checked++ }

    # --- what BepInEx needs to load the plugin -----------------------------------------------------
    $plugin = $assembly.MainModule.GetType('QuickKeyRestart.Plugin')
    if (-not $plugin) { $failed += 'Missing QuickKeyRestart.Plugin' }
    else {
        $checked++
        if ($plugin.BaseType.FullName -ne 'BepInEx.BaseUnityPlugin') {
            $failed += "QuickKeyRestart.Plugin derives from $($plugin.BaseType.FullName), expected BepInEx.BaseUnityPlugin"
        } else { $checked++ }

        $attribute = $plugin.CustomAttributes |
            Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' } | Select-Object -First 1
        if (-not $attribute) { $failed += 'QuickKeyRestart.Plugin has no [BepInPlugin]; BepInEx would skip it' }
        else {
            $checked++
            $guid = $attribute.ConstructorArguments[0].Value
            if ($guid -ne 'com.manhl.overcooked2.quickkeyrestart') {
                $failed += "[BepInPlugin] GUID is '$guid', expected com.manhl.overcooked2.quickkeyrestart (the installer migrates the config by that name)"
            } else { $checked++ }
        }

        # BepInEx drives these by name off MonoBehaviour; a rename makes the key stop working.
        foreach ($name in 'Awake', 'Update') {
            if (-not ($plugin.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })) {
                $failed += "QuickKeyRestart.Plugin has no $name(); Unity would never call it"
            } else { $checked++ }
        }
    }

    # --- the game members the mod invokes by name --------------------------------------------------
    # The handler the pause menu's "restart?" dialog calls on yes. Pressing the key calls it
    # directly, which is what skips the menu and the confirmation.
    $pauseMenu = $game.MainModule.GetType('InGamePauseMenu')
    if (-not $pauseMenu) { $failed += 'Missing InGamePauseMenu; the mod has nothing to drive' }
    else {
        $checked++
        $restart = $pauseMenu.Methods |
            Where-Object { $_.Name -eq 'OnRestartConfirmed' -and $_.Parameters.Count -eq 0 } | Select-Object -First 1
        if (-not $restart) { $failed += 'Missing InGamePauseMenu.OnRestartConfirmed(); nothing would restart the level' }
        else {
            $checked++
            if ($restart.IsStatic) {
                $failed += 'InGamePauseMenu.OnRestartConfirmed is static now; the mod invokes it on a found instance'
            } else { $checked++ }
        }

        # The mod finds the live menu through Resources.FindObjectsOfTypeAll and reads
        # gameObject.scene off it, so the type has to remain a Component.
        $base = $pauseMenu
        $isComponent = $false
        while ($base) {
            if ($base.FullName -eq 'UnityEngine.Component') { $isComponent = $true; break }
            if (-not $base.BaseType) { break }
            try { $base = $base.BaseType.Resolve() } catch { break }
        }
        if (-not $isComponent) {
            $failed += 'InGamePauseMenu is no longer a UnityEngine.Component; the mod could not find it in a scene'
        } else { $checked++ }
    }

    # Optional guard: tells "in a live level" from "on a menu or loading". The mod degrades to
    # always-allow when this is missing, so a rename is a warning rather than a failure.
    $flow = $game.MainModule.GetType('ServerFlowControllerBase')
    if (-not $flow) {
        Write-Host 'NOTE: ServerFlowControllerBase is gone; the mod will skip its in-level check.' -ForegroundColor Yellow
    } else {
        $checked++
        $base = $flow
        $isComponent = $false
        while ($base) {
            if ($base.FullName -eq 'UnityEngine.Component') { $isComponent = $true; break }
            if (-not $base.BaseType) { break }
            try { $base = $base.BaseType.Resolve() } catch { break }
        }
        if (-not $isComponent) {
            $failed += 'ServerFlowControllerBase is no longer a UnityEngine.Component; the in-level check would never pass'
        } else { $checked++ }
    }

    if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
    Write-Host "PASS: $checked references and reflection targets resolve against the shipped game runtime."
} finally {
    $assembly.Dispose()
    $game.Dispose()
    $resolver.Dispose()
}
