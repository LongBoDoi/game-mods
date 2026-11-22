<#
    Checks the built ChefSkills against the installed game.

    The compiler checks everything ChefSkills calls directly. What it cannot check is what Harmony
    and AccessTools look up by name at runtime: patch target methods, the parameter names patch
    methods bind to (a wrong one makes Harmony throw and the whole plugin unpatch itself), and
    private fields read by name. Each of those is asserted here.

    Run it after ChefSkills\build.cmd:
        powershell -NoProfile -File tests\CheckChefSkills.ps1
#>
param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Overcooked! 2',
    [string]$PluginPath = (Join-Path $PSScriptRoot '..\dist\ChefSkills.dll')
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
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    (Join-Path $GameDir 'Overcooked2_Data\Managed\Assembly-CSharp.dll'), $reader)
try {
    $failed = @()
    $checked = 0

    # --- everything the mod compiles against exists in the shipped runtime --------------------
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

    # Compiled against the game's mscorlib, not .NET 4's.
    $corlib = $assembly.MainModule.AssemblyReferences | Where-Object Name -eq 'mscorlib' | Select-Object -First 1
    if ($corlib -and $corlib.Version.Major -ge 4) {
        $failed += "ChefSkills references mscorlib $($corlib.Version); the game runs the 2.0 profile (use build.cmd)"
    } else { $checked++ }

    # --- what BepInEx needs to load the plugin ------------------------------------------------
    $plugin = $assembly.MainModule.GetType('ChefSkills.Plugin')
    if (-not $plugin) { $failed += 'Missing ChefSkills.Plugin' }
    else {
        $checked++
        $attribute = $plugin.CustomAttributes |
            Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' } | Select-Object -First 1
        if (-not $attribute) { $failed += 'ChefSkills.Plugin has no [BepInPlugin]' }
        elseif ($attribute.ConstructorArguments[0].Value -ne 'com.manhl.overcooked2.chefskills') {
            $failed += "[BepInPlugin] GUID is '$($attribute.ConstructorArguments[0].Value)', the installer expects com.manhl.overcooked2.chefskills"
        } else { $checked++ }
        # Soft dependencies only order loading (TiengViet first); a hard one would stop ChefSkills
        # loading without that plugin. BepInDependency(guid) is hard; flags value 2 is soft.
        $hard = $plugin.CustomAttributes | Where-Object {
            $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and
            -not ($_.ConstructorArguments.Count -ge 2 -and ([int]$_.ConstructorArguments[1].Value -band 2))
        }
        if ($hard) {
            $failed += 'ChefSkills.Plugin has a hard [BepInDependency]; it needs no other plugin and would not load without it'
        } else { $checked++ }
        foreach ($name in 'Awake', 'Update') {
            if (-not ($plugin.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })) {
                $failed += "ChefSkills.Plugin has no $name(); Unity would never call it"
            } else { $checked++ }
        }
    }

    # --- every Harmony patch binds to a real method with matching parameter names --------------
    $patchKinds = @{ 'Prefix' = 'HarmonyLib.HarmonyPrefix'; 'Postfix' = 'HarmonyLib.HarmonyPostfix'
                     'Finalizer' = 'HarmonyLib.HarmonyFinalizer'; 'Transpiler' = 'HarmonyLib.HarmonyTranspiler' }
    $patchClasses = 0
    foreach ($type in $assembly.MainModule.Types) {
        $patch = $type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' } |
            Select-Object -First 1
        if (-not $patch -or $patch.ConstructorArguments.Count -lt 2) { continue }
        $patchClasses++
        $targetType = $patch.ConstructorArguments[0].Value.Resolve()
        $methodName = $patch.ConstructorArguments[1].Value
        $argTypes = $null
        if ($patch.ConstructorArguments.Count -ge 3) {
            $argTypes = @($patch.ConstructorArguments[2].Value | ForEach-Object { $_.Value.FullName })
        }
        $candidates = @($targetType.Methods | Where-Object { $_.Name -eq $methodName })
        if ($argTypes) {
            $candidates = @($candidates | Where-Object {
                $names = @($_.Parameters | ForEach-Object { $_.ParameterType.FullName })
                ($names -join ',') -eq ($argTypes -join ',')
            })
        }
        if ($candidates.Count -ne 1) {
            $failed += "$($type.Name): patch target $($targetType.FullName).$methodName matches $($candidates.Count) methods (need exactly 1)"
            continue
        }
        $target = $candidates[0]
        $checked++
        $targetParams = @($target.Parameters | ForEach-Object Name)

        $found = 0
        foreach ($method in $type.Methods) {
            $isPatch = $patchKinds.ContainsKey($method.Name) -or
                ($method.CustomAttributes | Where-Object { $patchKinds.Values -contains $_.AttributeType.FullName })
            if (-not $isPatch) { continue }
            $found++
            foreach ($parameter in $method.Parameters) {
                $name = $parameter.Name
                if ($name -eq '__result' -and $target.ReturnType.FullName -eq 'System.Void') {
                    $failed += "$($type.Name).$($method.Name): __result on a void method"
                } elseif ($name.StartsWith('__')) {
                    if (@('__instance', '__state', '__result', '__exception') -notcontains $name) {
                        $failed += "$($type.Name).$($method.Name): unknown injected parameter $name"
                    } elseif ($name -eq '__instance' -and $target.IsStatic) {
                        $failed += "$($type.Name).$($method.Name): __instance on static $methodName"
                    } else { $checked++ }
                } elseif ($targetParams -notcontains $name) {
                    $failed += "$($type.Name).$($method.Name): parameter '$name' is not on $($targetType.Name).$methodName($($targetParams -join ', '))"
                } else { $checked++ }
            }
        }
        if ($found -eq 0) { $failed += "$($type.Name) has [HarmonyPatch] but no patch methods" }
    }
    if ($patchClasses -lt 16) {
        $failed += "Expected 16 patch classes (intro, chop, client chop, cook, mix, placement, serve, wash, client wash, throw, firefight, movement, dash report, dash latch, dash latch read, dash bump), found $patchClasses"
    }

    # --- private members reached by name -----------------------------------------------------
    $byName = @{
        'PlateReturnController'            = @('m_plateReturnControllerDesc')
        'ClientPlayerControlsImpl_Default' = @('m_controls', 'm_dashTimer', 'm_impactTimer')
        'ClientInputTransmitter'           = @('m_Pad')
        'ClientInputTransmitter/Pad'       = @('m_Dash')
        'ServerInputReceiver'              = @('m_DashNetworkButton')
        'NetworkLogicalButton'             = @('m_IsDown')
        'LogicalButtonBase'                = @('m_pressClaimed')
        'ServerWashingStation'             = @('m_cleaningTimer', 'm_interactor', 'm_interactable')
        'ClientWashingStation'             = @('m_cleaningTimer', 'm_interactable', 'm_progressUI', 'm_washingStation')
        'ServerInteractable'               = @('m_interacters')
        'ClientInteractable'               = @('m_interacters')
        'ServerAttachmentThrower'          = @('m_thrower')
        'ServerFireExtinguishSpray'        = @('m_FireExtinguishSpray')
        'Team17.Online.SteamOnlineMultiplayerSessionCoordinator' = @('m_lobbyId')
    }
    foreach ($typeName in $byName.Keys) {
        $type = $game.MainModule.GetType($typeName)
        foreach ($name in $byName[$typeName]) {
            if (-not ($type.Fields | Where-Object Name -eq $name)) { $failed += "Missing field read by name: $typeName.$name" }
            else { $checked++ }
        }
    }

    # --- every AccessTools.Field/Property/Method(typeof(T), "name") in the IL ---------------------
    # The pattern the compiler emits: ldtoken T; call Type.GetTypeFromHandle; ldstr "name"; call AccessTools.X
    $lookups = 0
    foreach ($type in $assembly.MainModule.GetTypes()) {
        foreach ($method in $type.Methods) {
            if (-not $method.HasBody) { continue }
            $code = @($method.Body.Instructions)
            for ($i = 0; $i -lt $code.Count; $i++) {
                $call = $code[$i]
                if ($call.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Call) { continue }
                $callee = $call.Operand
                if ($callee.DeclaringType.FullName -ne 'HarmonyLib.AccessTools') { continue }
                $kind = $callee.Name
                if (@('Field', 'Property', 'Method', 'DeclaredField', 'DeclaredProperty', 'DeclaredMethod') -notcontains $kind) { continue }
                # Find the ldstr and ldtoken just before the call.
                $name = $null; $token = $null
                for ($j = $i - 1; $j -ge [Math]::Max(0, $i - 12); $j--) {
                    $op = $code[$j].OpCode
                    if (-not $name -and $op.Code -eq [Mono.Cecil.Cil.Code]::Ldstr) { $name = $code[$j].Operand }
                    if ($op.Code -eq [Mono.Cecil.Cil.Code]::Ldtoken) { $token = $code[$j].Operand; break }
                    # Anything that ends the previous statement or computes the type another way:
                    # the lookup isn't of the typeof(T), "name" form, so it can't be checked here.
                    $isTypeOf = $op.Code -eq [Mono.Cecil.Cil.Code]::Call -and $code[$j].Operand.Name -eq 'GetTypeFromHandle'
                    if (-not $isTypeOf -and ($op.FlowControl -eq [Mono.Cecil.Cil.FlowControl]::Call -or
                        $op.Code.ToString().StartsWith('St') -or $op.Code.ToString().StartsWith('Ldfld') -or
                        $op.Code.ToString().StartsWith('Ldsfld') -or $op.Code.ToString().StartsWith('Ldloc') -or
                        $op.Code.ToString().StartsWith('Ldarg'))) { break }
                }
                if (-not $name -or -not $token) { continue }
                $target = $token.Resolve()
                if (-not $target) { $failed += "$($type.Name).$($method.Name): cannot resolve $($token.FullName) for $kind '$name'"; continue }
                $found = $false
                for ($t = $target; $t -and -not $found; $t = $(if ($t.BaseType) { $t.BaseType.Resolve() } else { $null })) {
                    switch -Wildcard ($kind) {
                        '*Field'    { if ($t.Fields | Where-Object Name -eq $name) { $found = $true } }
                        '*Property' { if ($t.Properties | Where-Object Name -eq $name) { $found = $true } }
                        '*Method'   { if ($t.Methods | Where-Object Name -eq $name) { $found = $true } }
                    }
                    if ($kind.StartsWith('Declared')) { break }
                }
                if ($found) { $checked++; $lookups++ }
                else { $failed += "$($type.Name).$($method.Name): AccessTools.$kind($($target.FullName), '$name') finds nothing" }
            }
        }
    }

    if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
    Write-Host "PASS: $checked checks ($patchClasses Harmony patches, $lookups name lookups) against the shipped game runtime."
} finally {
    $game.Dispose()
    $assembly.Dispose()
    $resolver.Dispose()
}
