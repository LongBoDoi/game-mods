<#
    Checks the built ChefRoles against the installed game.

    The compiler checks everything ChefRoles calls directly. What it cannot check is what Harmony
    and AccessTools look up by name at runtime: patch target methods, the parameter names patch
    methods bind to (a wrong one makes Harmony throw and the whole plugin unpatch itself), and
    private fields read by name. Each of those is asserted here.

    Run it after ChefRoles\build.cmd:
        powershell -NoProfile -File tests\CheckChefRoles.ps1
#>
param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Overcooked! 2',
    [string]$PluginPath = (Join-Path $PSScriptRoot '..\dist\ChefRoles.dll')
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
        $failed += "ChefRoles references mscorlib $($corlib.Version); the game runs the 2.0 profile (use build.cmd)"
    } else { $checked++ }

    # --- what BepInEx needs to load the plugin ------------------------------------------------
    $plugin = $assembly.MainModule.GetType('ChefRoles.Plugin')
    if (-not $plugin) { $failed += 'Missing ChefRoles.Plugin' }
    else {
        $checked++
        $attribute = $plugin.CustomAttributes |
            Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' } | Select-Object -First 1
        if (-not $attribute) { $failed += 'ChefRoles.Plugin has no [BepInPlugin]' }
        elseif ($attribute.ConstructorArguments[0].Value -ne 'com.manhl.overcooked2.chefroles') {
            $failed += "[BepInPlugin] GUID is '$($attribute.ConstructorArguments[0].Value)', the installer expects com.manhl.overcooked2.chefroles"
        } else { $checked++ }
        if ($plugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' }) {
            $failed += 'ChefRoles.Plugin has a [BepInDependency]; it needs no other plugin and would not load without it'
        } else { $checked++ }
        foreach ($name in 'Awake', 'Update') {
            if (-not ($plugin.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })) {
                $failed += "ChefRoles.Plugin has no $name(); Unity would never call it"
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
    if ($patchClasses -lt 6) { $failed += "Expected 6 patch classes (intro, chop, cook, placement, serve, run), found $patchClasses" }

    # --- private members reached by name -----------------------------------------------------
    $byName = @{
        'PlateReturnController'            = @('m_plateReturnControllerDesc')
        'ClientPlayerControlsImpl_Default' = @('m_controls')
        'T17InGameFlow'                    = @('m_pauseMenu')
    }
    foreach ($typeName in $byName.Keys) {
        $type = $game.MainModule.GetType($typeName)
        foreach ($name in $byName[$typeName]) {
            if (-not ($type.Fields | Where-Object Name -eq $name)) { $failed += "Missing field read by name: $typeName.$name" }
            else { $checked++ }
        }
    }

    if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
    Write-Host "PASS: $checked checks ($patchClasses Harmony patches) against the shipped game runtime."
} finally {
    $game.Dispose()
    $assembly.Dispose()
    $resolver.Dispose()
}
