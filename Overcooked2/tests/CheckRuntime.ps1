<#
    Checks the built OC2Tweaks against the installed game.

    Most of what the mod touches is compiled against the game's assemblies, so the compiler
    catches a rename. What it cannot catch is anything reached by name at runtime - Harmony
    patch targets, AccessTools fields and methods, the plugin GUID the installer migrates
    configs to - so each of those is asserted here instead.

    Run it after OC2Tweaks\build.cmd:
        powershell -NoProfile -File tests\CheckRuntime.ps1
#>
param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Overcooked! 2',
    [string]$PluginPath = (Join-Path $PSScriptRoot '..\dist\OC2Tweaks.dll')
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
try {
    $failed = @()
    $checked = 0

    # --- everything the mod compiles against still exists ------------------------------------
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

    # --- what BepInEx needs to load the plugin ------------------------------------------------
    $plugin = $assembly.MainModule.GetType('OC2Tweaks.Plugin')
    if (-not $plugin) { $failed += 'Missing OC2Tweaks.Plugin' }
    else {
        $checked++
        if ($plugin.BaseType.FullName -ne 'BepInEx.BaseUnityPlugin') {
            $failed += "OC2Tweaks.Plugin derives from $($plugin.BaseType.FullName), expected BepInEx.BaseUnityPlugin"
        } else { $checked++ }

        $attribute = $plugin.CustomAttributes |
            Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' } | Select-Object -First 1
        if (-not $attribute) { $failed += 'OC2Tweaks.Plugin has no [BepInPlugin]; BepInEx would skip it' }
        else {
            $checked++
            # The installer migrates the old LobbyTools and QuickKeyRestart configs onto this
            # GUID, and BepInEx names the config file after it.
            $guid = $attribute.ConstructorArguments[0].Value
            if ($guid -ne 'com.manhl.overcooked2.tweaks') {
                $failed += "[BepInPlugin] GUID is '$guid', expected com.manhl.overcooked2.tweaks (the installer migrates configs to that name)"
            } else { $checked++ }
        }

        # Unity drives this by name off MonoBehaviour; a rename loads the plugin and does nothing.
        if (-not ($plugin.Methods | Where-Object { $_.Name -eq 'Awake' -and $_.Parameters.Count -eq 0 })) {
            $failed += 'OC2Tweaks.Plugin has no Awake(); Unity would never call it'
        } else { $checked++ }
    }

    $ui = $assembly.MainModule.GetType('OC2Tweaks.ToolsUI')
    if (-not $ui) { $failed += 'Missing OC2Tweaks.ToolsUI; the panel and the restart key both run off it' }
    else {
        $checked++
        foreach ($name in 'Update', 'OnGUI') {
            if (-not ($ui.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })) {
                $failed += "OC2Tweaks.ToolsUI has no $name(); Unity would never call it"
            } else { $checked++ }
        }
    }

    # --- the game members reached by name, which no compiler checks ---------------------------
    $game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Overcooked2_Data\Managed\Assembly-CSharp.dll'))
    try {
        $flow = $game.MainModule.GetType('ClientLobbyFlowController')
        foreach ($name in 'TryJoinGame', 'HostGame') {
            if (-not ($flow.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })) {
                $failed += "Missing force-host method: ClientLobbyFlowController.$name()"
            } else { $checked++ }
        }

        # The handler the pause menu's "restart?" dialog calls on yes. Quick restart invokes it
        # directly, which is what skips the menu and the confirmation.
        $pauseMenu = $game.MainModule.GetType('InGamePauseMenu')
        if (-not $pauseMenu) { $failed += 'Missing InGamePauseMenu; quick restart has nothing to drive' }
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
        }

        $telemetry = @{
            'ConnectionModeSwitcher' = @('m_LocalServer')
            'Team17.Online.Multiplayer.Server' = @('m_RemoteClientConnections')
            'Team17.Online.Multiplayer.Connection.RemoteConnection' = @('m_ReliableLatencyMeasure', 'm_UnreliableLatencyMeasure')
            'Team17.Online.Multiplayer.Connection.LatencyMeasure' = @('m_OneWayLatencyHistory', 'm_fTimeTimeReceived')
        }
        foreach ($typeName in $telemetry.Keys) {
            $type = $game.MainModule.GetType($typeName)
            foreach ($name in $telemetry[$typeName]) {
                if (-not ($type.Fields | Where-Object Name -eq $name)) {
                    $failed += "Missing connection telemetry field: $typeName.$name"
                } else { $checked++ }
            }
        }
        $baseConnection = $game.MainModule.GetType('Team17.Online.Multiplayer.Connection.BaseConnection')
        if (-not ($baseConnection.Methods | Where-Object { $_.Name -eq 'GetLatencyTestPaused' -and $_.Parameters.Count -eq 0 })) {
            $failed += 'Missing continuous-ping patch target: BaseConnection.GetLatencyTestPaused()'
        } else { $checked++ }
    } finally { $game.Dispose() }
    if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
    Write-Host "PASS: $checked type/member references resolve against the shipped game runtime."
} finally {
    $assembly.Dispose()
    $resolver.Dispose()
}
