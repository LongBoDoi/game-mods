param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Overcooked! 2',
    [string]$PluginPath = (Join-Path $PSScriptRoot '..\dist\RandomLevel.dll')
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.RemoveSearchDirectory('.')
$resolver.RemoveSearchDirectory('bin')
foreach ($relative in 'Overcooked2_Data\Managed', 'BepInEx\core', 'BepInEx\plugins\OC2DIYLevel') {
    $resolver.AddSearchDirectory((Join-Path $GameDir $relative))
}
$reader = New-Object Mono.Cecil.ReaderParameters
$reader.AssemblyResolver = $resolver
$assemblies = @()
$script:checked = 0
function Assert($ok, $message) {
    if (-not $ok) { throw $message }
    $script:checked++
}
function Method($type, $name, $count) {
    $method = @($type.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq $count })
    Assert ($method.Count -eq 1) "Expected one $($type.FullName).$name with $count parameters."
    return $method[0]
}
try {
    foreach ($path in (Resolve-Path -LiteralPath $PluginPath).Path,
        (Join-Path $GameDir 'Overcooked2_Data\Managed\Assembly-CSharp.dll'),
        (Join-Path $GameDir 'BepInEx\plugins\OC2DIYLevel\OC2DIYLevel.dll')) {
        $assemblies += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path, $reader)
    }
    $plugin, $game, $diy = $assemblies
    Assert ($plugin.MainModule.RuntimeVersion -eq 'v2.0.50727') 'Plugin must target the Unity CLR 2 / .NET 3.5 profile.'
    foreach ($reference in @($plugin.MainModule.GetTypeReferences()) + @($plugin.MainModule.GetMemberReferences())) {
        if ($reference -is [Mono.Cecil.TypeReference] -or $reference -is [Mono.Cecil.MethodReference] -or
            $reference -is [Mono.Cecil.FieldReference]) {
            Assert ($null -ne $reference.Resolve()) "Missing runtime reference: $($reference.FullName)"
        }
    }
    $entry = $plugin.MainModule.GetType('RandomLevel.Plugin')
    Assert ($entry.BaseType.FullName -eq 'BepInEx.BaseUnityPlugin') 'Wrong plugin base class.'
    $dependency = $entry.CustomAttributes | Where-Object AttributeType -Match 'BepInDependency$'
    Assert ($dependency.ConstructorArguments[0].Value -eq 'dev.gua.overcooked.diylevel') 'Missing hard dependency on OC2DIYLevel.'
    Assert ($dependency.ConstructorArguments[1].Value -eq '0.10.0') 'Unexpected minimum pack version.'

    $ui = $diy.MainModule.GetType('OC2DIYLevel.DIYLevelEntryUI')
    $null = Method $ui 'AddUI' 0
    $select = Method $ui 'OnLevelSelected' 1
    Assert ($select.IsStatic -and $select.Parameters[0].ParameterType.FullName -eq 'System.String') 'The save-slot entry point changed.'
    $menu = $ui.Fields | Where-Object Name -eq 'levelSetSelectionMenu'
    Assert ($menu.IsStatic -and $menu.FieldType.FullName -eq 'FrontendOptionsMenu') 'The DIY menu field changed.'

    $flow = $game.MainModule.GetType('ServerCampaignFlowController')
    $next = Method $flow 'GetNextScene' 3
    $expectedNames = @('o_loadState', 'o_loadEndState', 'o_useLoadingScreen')
    $expectedTypes = @('GameState&', 'GameState&', 'System.Boolean&')
    for ($i = 0; $i -lt 3; $i++) {
        Assert ($next.Parameters[$i].Name -eq $expectedNames[$i] -and $next.Parameters[$i].ParameterType.FullName -eq $expectedTypes[$i]) 'The transition hook signature changed.'
    }
    $restart = $flow.Fields | Where-Object Name -eq 'm_LevelRestartRequested'
    Assert ($restart -and -not $restart.IsStatic -and $restart.FieldType.FullName -eq 'System.Boolean') 'Harmony cannot inject the Restart field.'
    $null = Method ($game.MainModule.GetType('StarRatingUIController')) 'SetButtonActive' 0
    $outro = $game.MainModule.GetType('ScoreScreenOutroFlowroutine')
    $null = Method $outro 'Run' 0
    $scoreController = $outro.Fields | Where-Object Name -eq 'm_starRatingController'
    Assert ($scoreController -and -not $scoreController.IsStatic -and $scoreController.FieldType.FullName -eq 'StarRatingUIController') 'Harmony cannot capture the results controller at outro start.'
    $null = Method ($game.MainModule.GetType('GameUtils')) 'QuitLevel' 0
    $null = Method ($game.MainModule.GetType('InGamePauseMenu')) 'OnRestartConfirmed' 0
    $null = Method ($game.MainModule.GetType('ServerFlowControllerBase')) 'get_InRound' 0
    $null = Method ($game.MainModule.GetType('MultiplayerController')) 'StopSynchronisation' 0
    $null = Method ($diy.MainModule.GetType('OC2DIYLevel.DIYLevelAssetBundleManager')) 'LoadLevelServer' 1
    $null = Method ($diy.MainModule.GetType('OC2DIYLevel.Extension.AnalyticsPublicizer')) 'IsServer' 0

    $skip = Method ($plugin.MainModule.GetType('RandomLevel.SkipLevel')) 'TrySkip' 1
    $skipCalls = @($skip.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
    Assert ($skipCalls.IndexOf('StopSynchronisation') -ge 0 -and $skipCalls.IndexOf('LoadLevelServer') -gt $skipCalls.IndexOf('StopSynchronisation')) 'Skip must tear down synchronisation before loading the next level.'
    Assert (-not ($skipCalls | Where-Object { $_ -in @('SkipToEnd', 'SetScoreData', 'SaveSession') })) 'Skip must not finish or save a partial round as a result.'

    $state = $game.MainModule.GetType('GameState')
    Assert (($state.Fields | Where-Object Name -eq 'LoadKitchen').Constant -eq 5) 'LoadKitchen differs from the pack loading state.'
    Assert (($state.Fields | Where-Object Name -eq 'RunKitchen').Constant -eq 15) 'RunKitchen differs from the pack load-end state.'
    $hook = Method ($plugin.MainModule.GetType('RandomLevel.NextRandomScene')) 'Postfix' 6
    Assert ([bool]($hook.CustomAttributes | Where-Object AttributeType -Match 'HarmonyPostfix$')) 'Next-scene hook must be a postfix, after the pack prefix.'
    Assert ([bool]($hook.CustomAttributes | Where-Object AttributeType -Match 'HarmonyAfter$')) 'Missing explicit ordering after OC2DIYLevel.'
    Write-Host "PASS: $script:checked runtime references and game/DIY hook contracts."
} finally {
    foreach ($assembly in $assemblies) { $assembly.Dispose() }
    $resolver.Dispose()
}
