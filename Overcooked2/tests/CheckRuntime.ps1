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

    # Unity calls these by name off MonoBehaviour: the always-on runtime (bans, ping, keys,
    # overlay) and the settings screen's refresh and scrolling.
    $unityMessages = @{
        'OC2Tweaks.Runtime' = @('Update', 'OnGUI')
        'OC2Tweaks.TweaksMenu' = @('Update', 'LateUpdate', 'OnDisable')
        'OC2Tweaks.TabEntry' = @('OnEnable', 'LateUpdate')
        'OC2Tweaks.DialogFonts' = @('OnDisable')
    }
    foreach ($typeName in $unityMessages.Keys) {
        $type = $assembly.MainModule.GetType($typeName)
        if (-not $type) { $failed += "Missing $typeName"; continue }
        $checked++
        foreach ($name in $unityMessages[$typeName]) {
            if (-not ($type.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })) {
                $failed += "$typeName has no $name(); Unity would never call it"
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
            'ConnectionModeSwitcher' = @('m_LocalServer', 'm_LocalClient')
            'Team17.Online.Multiplayer.Server' = @('m_RemoteClientConnections')
            'Team17.Online.Multiplayer.Client' = @('m_ServerConnection')
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

        # The host relays everyone's ping inside the game's latency probe; clients read it in a
        # prefix on the probe handler. Harmony binds prefix arguments by name, so check those too.
        $measure = $game.MainModule.GetType('Team17.Online.Multiplayer.Connection.LatencyMeasure')
        $receive = $measure.Methods | Where-Object { $_.Name -eq 'OnLatencyReceived' } | Select-Object -First 1
        if (-not $receive) { $failed += 'Missing ping-relay patch target: LatencyMeasure.OnLatencyReceived' }
        else {
            $checked++
            $names = @($receive.Parameters | ForEach-Object { $_.Name }) -join ','
            if ($names -ne 'from,message') {
                $failed += "LatencyMeasure.OnLatencyReceived parameters are ($names), the relay prefix expects (from,message)"
            } else { $checked++ }
        }
        if (-not ($measure.Fields | Where-Object { $_.Name -eq 'm_bReliable' -and $_.FieldType.FullName -eq 'System.Boolean' })) {
            $failed += 'Missing ping-relay field: LatencyMeasure.m_bReliable (bool)'
        } else { $checked++ }
        # Real probes carry Time.realtimeSinceStartup here; the relay marks itself with a negative one.
        $latencyMessage = $game.MainModule.GetType('LatencyMessage')
        if (-not ($latencyMessage.Fields | Where-Object { $_.Name -eq 'm_fTime' -and $_.FieldType.FullName -eq 'System.Single' })) {
            $failed += 'Missing ping-relay carrier: LatencyMessage.m_fTime (float)'
        } else { $checked++ }

        # --- the Settings screen: patch targets and private members reached by name --------------
        function Assert-Method($typeName, $name, $parameters, $why) {
            $type = $game.MainModule.GetType($typeName)
            $method = if ($type) { $type.Methods | Where-Object { $_.Name -eq $name } | Select-Object -First 1 }
            if (-not $method) { $script:failed += "Missing $typeName.$name ($why)"; return }
            $names = @($method.Parameters | ForEach-Object { $_.Name }) -join ','
            if ($null -ne $parameters -and $names -ne $parameters) {
                $script:failed += "$typeName.$name parameters are ($names), expected ($parameters) ($why)"
            } else { $script:checked++ }
        }
        function Assert-Field($typeName, $name, $why) {
            $type = $game.MainModule.GetType($typeName)
            if (-not ($type -and ($type.Fields | Where-Object Name -eq $name))) { $script:failed += "Missing $typeName.$name ($why)" }
            else { $script:checked++ }
        }
        Assert-Method 'FrontendRootMenu' 'Awake' '' 'adds the OC2 TWEAKS entry to Settings'
        Assert-Method 'FrontendRootMenu' 'OnMenuShow' 'menu' 'tells the main menu the screen is open, so Esc closes it'
        Assert-Method 'FrontendRootMenu' 'OnMenuHide' 'menu' 'and that it closed'
        Assert-Field 'FrontendOptionsMenu' 'm_VersionString' 'the version line the screen reuses'
        Assert-Field 'FrontendOptionsMenu' 'm_ScrollView' 'the list the screen is built around'
        Assert-Field 'BaseUIOption`1' 'm_OptionType' 'cleared so a copied row leaves the game option alone'
        Assert-Field 'BaseUIOption`1' 'm_Option' 'where a copied row is pointed at the mod setting'
        Assert-Field 'T17NavigableGrid' 'm_ContentIsDynamic' 'the list must not rebuild its rows on its own'
        Assert-Field 'T17ScrollView' 'm_bScrollToSelectedElement' 'the screen does its own scrolling'
        if (-not $game.MainModule.GetType('FrontendGameSettingsTab')) { $failed += 'Missing FrontendGameSettingsTab: the Settings tab the entry goes into' } else { $checked++ }
        # The game's player cards: the hidden Kick button the mod switches on for the host.
        Assert-Field 'UIPlayerMenuBehaviour' 'm_menuOptions' 'the card entries, Kick among them'
        Assert-Field 'UIPlayerMenuBehaviour/MenuOption' 'm_platformsActiveOn' 'Kick is hidden on every platform here'
        Assert-Method 'UIPlayerMenuBehaviour' 'OnClick' '_option' 'Kick opens the kick/ban prompt'
        Assert-Method 'UIPlayerMenuBehaviour' 'Awake' '' 'Kick is un-hidden as each card is made'
        Assert-Method 'UIPlayerRootMenu' 'ReCreateUIPlayers' '' 'lets the host kick from the cards'
        Assert-Field 'UIPlayerRootMenu' 'm_canKickUsers' 'lets the host kick from the cards'
    } finally { $game.Dispose() }
    if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
    Write-Host "PASS: $checked type/member references resolve against the shipped game runtime."
} finally {
    $assembly.Dispose()
    $resolver.Dispose()
}
