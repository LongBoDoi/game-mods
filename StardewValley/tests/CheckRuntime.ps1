<#
    Checks the built mod against the game and SMAPI that are actually installed: every type, method and
    field it uses has to exist, and Game1.SetWindowSize has to still have the shape the Harmony transpiler
    relies on (one split-screen slot lookup, with the zoom and the render targets around it).

    Run it after MonitorCoop\build.cmd:
        powershell -NoProfile -File tests\CheckRuntime.ps1
#>
param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Stardew Valley',
    [string]$ModPath = (Join-Path $PSScriptRoot '..\dist\MonitorCoop\MonitorCoop.dll')
)
$ErrorActionPreference = 'Stop'

$smapiDir = Join-Path $GameDir 'smapi-internal'
Add-Type -Path (Join-Path $smapiDir 'Mono.Cecil.dll')

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.RemoveSearchDirectory('.')
$resolver.RemoveSearchDirectory('bin')
$resolver.AddSearchDirectory($GameDir)
$resolver.AddSearchDirectory($smapiDir)
$reader = New-Object Mono.Cecil.ReaderParameters
$reader.AssemblyResolver = $resolver

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $ModPath).Path, $reader)
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stardew Valley.dll'), $reader)
$monoGame = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'MonoGame.Framework.dll'), $reader)
try {
    $failed = @()
    $checked = 0

    # --- everything the mod compiles against still exists ---------------------------------------
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

    # --- what SMAPI checks before it loads the mod -------------------------------------------------
    $manifestPath = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $ModPath).Path) 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        $failed += "manifest.json is not next to the DLL ($manifestPath); SMAPI would not see the mod"
    } else {
        $checked++
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        foreach ($field in 'Name', 'Author', 'Version', 'Description', 'UniqueID', 'EntryDll') {
            if (-not $manifest.$field) { $failed += "manifest.json has no $field" } else { $checked++ }
        }
        if ($manifest.EntryDll -ne (Split-Path -Leaf $ModPath)) {
            $failed += "manifest.json points at $($manifest.EntryDll) but the built mod is $(Split-Path -Leaf $ModPath)"
        } else { $checked++ }
        if ($manifest.Version -ne $assembly.Name.Version.ToString(3)) {
            $failed += "manifest.json says version $($manifest.Version) but the DLL is $($assembly.Name.Version.ToString(3))"
        } else { $checked++ }

        $smapi = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'StardewModdingAPI.dll'), $reader)
        try {
            $installed = [version]$smapi.Name.Version.ToString(3)
            if ([version]$manifest.MinimumApiVersion -gt $installed) {
                $failed += "manifest.json needs SMAPI $($manifest.MinimumApiVersion) but $installed is installed"
            } else { $checked++ }
        } finally { $smapi.Dispose() }
    }

    # SMAPI entry point: one public class deriving from Mod, with a public no-argument constructor.
    $entries = @($assembly.MainModule.Types | Where-Object {
        $_.IsPublic -and $_.BaseType -and $_.BaseType.FullName -eq 'StardewModdingAPI.Mod'
    })
    if ($entries.Count -ne 1) {
        $failed += "Expected exactly one public StardewModdingAPI.Mod subclass, found $($entries.Count)"
    } else {
        $checked++
        if (-not ($entries[0].Methods | Where-Object { $_.IsConstructor -and $_.IsPublic -and $_.Parameters.Count -eq 0 })) {
            $failed += "$($entries[0].FullName) has no public constructor without arguments; SMAPI could not create it"
        } else { $checked++ }
        if (-not ($entries[0].Methods | Where-Object { $_.Name -eq 'Entry' -and $_.Parameters.Count -eq 1 })) {
            $failed += "$($entries[0].FullName) does not override Entry(IModHelper)"
        } else { $checked++ }
    }

    # --- the fields the patch writes through -------------------------------------------------------
    $game1 = $game.MainModule.GetType('StardewValley.Game1')
    foreach ($name in 'localMultiplayerWindow', 'instanceIndex', 'zoomModifier', 'game1', 'graphics') {
        $field = $game1.Fields | Where-Object Name -eq $name
        if (-not $field) { $failed += "Missing Game1 field: $name" }
        elseif (-not $field.IsPublic) { $failed += "Game1.$name is no longer public" }
        else { $checked++ }
    }

    # --- the shape Transpile() depends on ---------------------------------------------------------
    $setWindowSize = $game1.Methods |
        Where-Object { $_.Name -eq 'SetWindowSize' -and $_.Parameters.Count -eq 2 }
    if (-not $setWindowSize) {
        $failed += 'Missing patch target: Game1.SetWindowSize(int, int)'
    } else {
        $checked++
        if (-not $setWindowSize.IsVirtual) { $failed += 'Game1.SetWindowSize is no longer virtual' } else { $checked++ }

        # The transpiler pushes ldarg.0/1/2 after the slot lookup, so the method has to still be an
        # instance method taking (int w, int h).
        if ($setWindowSize.IsStatic) {
            $failed += 'Game1.SetWindowSize is now static; the hook passes the wrong arguments'
        } else { $checked++ }
        # The postfix takes w and h by name, so Harmony needs those names to still be in the metadata.
        $expectedNames = @('w', 'h')
        for ($i = 0; $i -lt $setWindowSize.Parameters.Count; $i++) {
            $parameter = $setWindowSize.Parameters[$i]
            if ($parameter.ParameterType.FullName -ne 'System.Int32') {
                $failed += "Game1.SetWindowSize takes $($parameter.ParameterType.FullName), expected int"
            } else { $checked++ }
            if ($parameter.Name -ne $expectedNames[$i]) {
                $failed += "Game1.SetWindowSize's parameter $i is called '$($parameter.Name)', but the postfix asks for '$($expectedNames[$i])'"
            } else { $checked++ }
        }

        $body = @($setWindowSize.Body.Instructions)
        $slotLookups = @()
        $zoomStores = @()
        $renderTargets = @()
        for ($i = 0; $i -lt $body.Count; $i++) {
            $operand = $body[$i].Operand
            if ($operand -is [Mono.Cecil.MethodReference]) {
                if ($operand.Name -eq 'get_Item' -and
                    $operand.DeclaringType.FullName -eq 'System.Collections.Generic.List`1<Microsoft.Xna.Framework.Vector4>') {
                    $slotLookups += $i
                }
                if ($operand.Name -eq '.ctor' -and $operand.DeclaringType.Name -eq 'RenderTarget2D') {
                    $renderTargets += $i
                }
            }
            if ($operand -is [Mono.Cecil.FieldReference] -and $operand.Name -eq 'zoomModifier' -and
                $body[$i].OpCode.Name -eq 'stfld') {
                $zoomStores += $i
            }
        }

        if ($slotLookups.Count -ne 1) {
            $failed += "Game1.SetWindowSize has $($slotLookups.Count) split-screen slot lookups, expected exactly 1 (the transpiler anchor)"
        } else { $checked++ }

        if ($slotLookups.Count -eq 1) {
            # The looked-up slot is stored straight into a local, which is what lets the hook substitute
            # its own value for it.
            $next = $body[$slotLookups[0] + 1]
            if (-not $next -or $next.OpCode.Name -notlike 'stloc*') {
                $failed += "The split-screen slot is no longer stored in a local right after the lookup (next instruction: $($next.OpCode.Name)); the hook's return value could go somewhere unexpected"
            } else { $checked++ }

            if (-not $zoomStores.Count -or ($zoomStores | Measure-Object -Maximum).Maximum -gt $slotLookups[0]) {
                $failed += 'Game1.SetWindowSize sets zoomModifier after the slot lookup; the hook could no longer override the split-screen zoom'
            } else { $checked++ }

            if (-not $renderTargets.Count -or ($renderTargets | Measure-Object -Minimum).Minimum -lt $slotLookups[0]) {
                $failed += 'Game1.SetWindowSize creates its render targets before the slot lookup; the hook could no longer size them'
            } else { $checked++ }
        }
    }

    # --- the cursor patch's targets ----------------------------------------------------------------
    $inputState = $game.MainModule.GetType('StardewValley.InputState')
    $getMouse = $inputState.Methods | Where-Object { $_.Name -eq 'GetMouseState' -and $_.Parameters.Count -eq 0 }
    if (-not $getMouse) {
        $failed += 'Missing InputState.GetMouseState()'
    } else {
        $checked++
        if ($getMouse.ReturnType.Name -ne 'MouseState') {
            $failed += "InputState.GetMouseState returns $($getMouse.ReturnType.Name), expected MouseState"
        } else { $checked++ }
    }

    $setMouse = $inputState.Methods | Where-Object { $_.Name -eq 'SetMousePosition' -and $_.Parameters.Count -eq 2 }
    if (-not $setMouse) {
        $failed += 'Missing InputState.SetMousePosition(int, int)'
    } else {
        $checked++
        # The prefix takes x and y by name and by reference, so both names have to still be there.
        $expectedMouseArgs = @('x', 'y')
        for ($i = 0; $i -lt $setMouse.Parameters.Count; $i++) {
            if ($setMouse.Parameters[$i].Name -ne $expectedMouseArgs[$i]) {
                $failed += "InputState.SetMousePosition's parameter $i is called '$($setMouse.Parameters[$i].Name)', but the prefix asks for '$($expectedMouseArgs[$i])'"
            } else { $checked++ }
        }
        if (-not (@($setMouse.Body.Instructions) | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'SetPosition' -and
                $_.Operand.DeclaringType.Name -eq 'Mouse' })) {
            $failed += 'InputState.SetMousePosition no longer moves the pointer through Mouse.SetPosition'
        } else { $checked++ }
    }

    # SMAPI replaces the game's input handler, and its own cached mouse state comes from a non-virtual
    # call to InputState.GetMouseState. That call is what the postfix rides on, so if SMAPI ever stops
    # making it, the cursor fix would silently do nothing.
    $smapiInput = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'StardewModdingAPI.dll'), $reader)
    try {
        $sInput = $smapiInput.MainModule.GetType('StardewModdingAPI.Framework.Input.SInputState')
        if (-not $sInput) {
            $failed += 'SMAPI no longer has SInputState; check how it reads the mouse before trusting the cursor fix'
        } elseif ($sInput.BaseType.FullName -ne 'StardewValley.InputState') {
            $failed += "SMAPI's SInputState no longer derives from InputState (it derives from $($sInput.BaseType.FullName))"
        } else {
            $checked++
            $callsBase = $false
            foreach ($method in $sInput.Methods) {
                if (-not $method.HasBody) { continue }
                foreach ($instruction in $method.Body.Instructions) {
                    if ($instruction.OpCode.Name -eq 'call' -and $instruction.Operand -is [Mono.Cecil.MethodReference] -and
                        $instruction.Operand.Name -eq 'GetMouseState' -and
                        $instruction.Operand.DeclaringType.FullName -eq 'StardewValley.InputState') {
                        $callsBase = $true
                    }
                }
            }
            if (-not $callsBase) {
                $failed += "SMAPI's SInputState no longer calls base.GetMouseState(); the cursor postfix would never run"
            } else { $checked++ }
        }
    } finally { $smapiInput.Dispose() }

    # The postfix rebuilds the state with this constructor.
    $mouseState = $monoGame.MainModule.GetType('Microsoft.Xna.Framework.Input.MouseState')
    if (-not ($mouseState.Methods | Where-Object { $_.IsConstructor -and $_.Parameters.Count -eq 8 })) {
        $failed += 'MouseState has no eight-argument constructor; the cursor postfix could not rebuild the state'
    } else { $checked++ }

    # --- the window position still decides where a player's screen is drawn ------------------------
    $drawSplit = $game1.Methods | Where-Object { $_.Name -eq 'DrawSplitScreenWindow' -and $_.Parameters.Count -eq 0 }
    if (-not $drawSplit) {
        $failed += 'Missing Game1.DrawSplitScreenWindow()'
    } elseif (-not (@($drawSplit.Body.Instructions) |
            Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'localMultiplayerWindow' })) {
        $failed += 'Game1.DrawSplitScreenWindow no longer reads localMultiplayerWindow'
    } else { $checked++ }

    # --- the window and option calls the mod makes -------------------------------------------------
    $options = $game.MainModule.GetType('StardewValley.Options')
    foreach ($name in 'isCurrentlyWindowed', 'isCurrentlyFullscreen', 'isCurrentlyWindowedBorderless') {
        if (-not ($options.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })) {
            $failed += "Missing Options.$name()"
        } else { $checked++ }
    }
    if (-not ($options.Methods | Where-Object {
            $_.Name -eq 'setWindowedOption' -and $_.Parameters.Count -eq 1 -and
            $_.Parameters[0].ParameterType.FullName -eq 'System.Int32' })) {
        $failed += 'Missing Options.setWindowedOption(int)'
    } else { $checked++ }

    $runner = $game.MainModule.GetType('StardewValley.GameRunner')
    foreach ($name in 'instance', 'gameInstances') {
        if (-not ($runner.Fields | Where-Object Name -eq $name)) { $failed += "Missing GameRunner field: $name" }
        else { $checked++ }
    }
    if (-not ($runner.Methods | Where-Object { $_.Name -eq 'OnWindowSizeChange' -and $_.Parameters.Count -eq 2 })) {
        $failed += 'Missing GameRunner.OnWindowSizeChange(object, EventArgs)'
    } else { $checked++ }

    $window = $monoGame.MainModule.GetType('Microsoft.Xna.Framework.GameWindow')
    if (-not ($window.Methods | Where-Object {
            $_.Name -eq 'GetDisplayBounds' -and $_.Parameters.Count -eq 1 })) {
        $failed += 'Missing GameWindow.GetDisplayBounds(int); the mod has no way to read the monitor layout'
    } else { $checked++ }
    foreach ($name in 'Position', 'IsBorderless', 'ClientBounds') {
        if (-not ($window.Properties | Where-Object Name -eq $name)) { $failed += "Missing GameWindow.$name" }
        else { $checked++ }
    }
    foreach ($name in 'Position', 'IsBorderless') {
        $property = $window.Properties | Where-Object Name -eq $name
        if ($property -and -not $property.SetMethod) { $failed += "GameWindow.$name can no longer be set" }
        elseif ($property) { $checked++ }
    }

    if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
    Write-Host "PASS: $checked references and layout assumptions hold against the installed game and SMAPI."
} finally {
    $assembly.Dispose()
    $game.Dispose()
    $monoGame.Dispose()
    $resolver.Dispose()
}
