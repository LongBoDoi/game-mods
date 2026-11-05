<#
    Checks the built AutoFishCoop against what is actually installed: the game, SMAPI, and Tibbles' Auto
    Fishing 2.0 itself. Everything this mod looks up by name in another mod is asserted here, so a version of
    Auto Fishing it no longer fits is a failed check rather than a surprise in game.

    Run it after AutoFishCoop\build.cmd:
        powershell -NoProfile -File tests\CheckAutoFish.ps1
#>
param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Stardew Valley',
    [string]$ModPath = (Join-Path $PSScriptRoot '..\dist\AutoFishCoop\AutoFishCoop.dll'),
    [string]$AutoFishDir
)
$ErrorActionPreference = 'Stop'

if (-not $AutoFishDir) { $AutoFishDir = Join-Path $GameDir 'Mods\AutoFishing2' }

$smapiDir = Join-Path $GameDir 'smapi-internal'
Add-Type -Path (Join-Path $smapiDir 'Mono.Cecil.dll')

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.RemoveSearchDirectory('.')
$resolver.RemoveSearchDirectory('bin')
$resolver.AddSearchDirectory($GameDir)
$resolver.AddSearchDirectory($smapiDir)
$reader = New-Object Mono.Cecil.ReaderParameters
$reader.AssemblyResolver = $resolver

function Get-Member($type, $kind, $name) {
    if (-not $type) { return $null }
    return $type.$kind | Where-Object Name -eq $name | Select-Object -First 1
}

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $ModPath).Path, $reader)
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stardew Valley.dll'), $reader)
$smapi = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'StardewModdingAPI.dll'), $reader)
$autoFish = $null
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

    # Auto Fishing is reached by name at runtime, never compiled against: SMAPI has to be able to load
    # this mod even when Auto Fishing has changed or gone.
    if ($assembly.MainModule.AssemblyReferences | Where-Object { $_.Name -eq 'AutoFishing2' }) {
        $failed += 'AutoFishCoop.dll references the AutoFishing2 assembly; it should only look it up by name'
    } else { $checked++ }

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

        $installed = [version]$smapi.Name.Version.ToString(3)
        if ([version]$manifest.MinimumApiVersion -gt $installed) {
            $failed += "manifest.json needs SMAPI $($manifest.MinimumApiVersion) but $installed is installed"
        } else { $checked++ }

        # The dependency is what makes SMAPI load Auto Fishing first, which the patches need.
        $dependency = $manifest.Dependencies | Where-Object { $_.UniqueID -eq 'Tibbles.AutoFishing2' }
        if (-not $dependency) {
            $failed += 'manifest.json does not depend on Tibbles.AutoFishing2, so SMAPI may load this mod first'
        } else {
            $checked++
            if (-not $dependency.IsRequired) {
                $failed += 'The Tibbles.AutoFishing2 dependency is not required; there is nothing to do without it'
            } else { $checked++ }
        }
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

    # --- the game statics the fix leans on ---------------------------------------------------------
    # Game1's statics are per split-screen player unless marked otherwise, which is the whole reason
    # Game1.input can be read to find the mouse state of the player whose code is running.
    $game1 = $game.MainModule.GetType('StardewValley.Game1')
    if (-not ($game1.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'StardewValley.InstanceStatics' })) {
        $failed += 'Game1 is no longer marked [InstanceStatics]; its statics may be shared between players now'
    } else { $checked++ }

    $input = Get-Member $game1 'Fields' 'input'
    if (-not $input) { $failed += 'Missing Game1.input' }
    else {
        $checked++
        if (-not $input.IsPublic) { $failed += 'Game1.input is no longer public' } else { $checked++ }
        if ($input.FieldType.FullName -ne 'StardewValley.InputState') {
            $failed += "Game1.input is a $($input.FieldType.FullName), expected StardewValley.InputState"
        } else { $checked++ }
        if ($input.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'StardewValley.NonInstancedStatic' }) {
            $failed += 'Game1.input is now shared between players, so it no longer identifies the running screen'
        } else { $checked++ }
    }

    $inputState = $game.MainModule.GetType('StardewValley.InputState')
    $mouseState = Get-Member $inputState 'Fields' '_currentMouseState'
    if (-not $mouseState) {
        $failed += 'Missing InputState._currentMouseState; ScreenMouseState has nothing to read or write'
    } else {
        $checked++
        if ($mouseState.FieldType.FullName -ne 'Microsoft.Xna.Framework.Input.MouseState') {
            $failed += "InputState._currentMouseState is a $($mouseState.FieldType.FullName), expected a MouseState"
        } else { $checked++ }
    }

    # --- the menu ----------------------------------------------------------------------------------
    # FishingMenu is a game menu, so the game has to still call into the parts of it that are overridden.
    # A method that stopped being virtual would leave the override sitting there unused.
    $menu = $game.MainModule.GetType('StardewValley.Menus.IClickableMenu')
    if (-not $menu) { $failed += 'Missing StardewValley.Menus.IClickableMenu; the menu has no base class' }
    else {
        $checked++
        if ($menu.IsSealed) { $failed += 'IClickableMenu is sealed now; FishingMenu cannot derive from it' }
        else { $checked++ }
        if (-not ($menu.Methods | Where-Object { $_.IsConstructor -and $_.IsPublic -and $_.Parameters.Count -eq 0 })) {
            $failed += 'IClickableMenu has no public constructor without arguments'
        } else { $checked++ }

        $overridden = @('applyMovementKey', 'receiveGamePadButton', 'receiveLeftClick', 'performHoverAction',
            'gameWindowSizeChanged', 'snapToDefaultClickableComponent', 'draw')
        foreach ($name in $overridden) {
            $methods = @($menu.Methods | Where-Object { $_.Name -eq $name -and $_.IsVirtual })
            if (-not $methods.Count) {
                $failed += "IClickableMenu.$name is no longer virtual, so FishingMenu's version would never run"
            } else { $checked++ }
        }

        # The menu leans on the base class for the rest: the D-pad moves between rows through these.
        foreach ($name in 'populateClickableComponentList', 'snapCursorToCurrentSnappedComponent', 'exitThisMenu') {
            if (-not (Get-Member $menu 'Methods' $name)) { $failed += "Missing IClickableMenu.$name" }
            else { $checked++ }
        }
        foreach ($name in 'allClickableComponents', 'currentlySnappedComponent') {
            if (-not (Get-Member $menu 'Fields' $name)) { $failed += "Missing IClickableMenu.$name" }
            else { $checked++ }
        }
    }

    # A player is their farmer's multiplayer ID and their number is the game's own count of the screens,
    # which it renumbers as players leave. ModEntry goes round the screens to report on each of them.
    if (-not (Get-Member $game.MainModule.GetType('StardewValley.Farmer') 'Properties' 'UniqueMultiplayerID')) {
        $failed += 'Missing Farmer.UniqueMultiplayerID; settings have no stable way to tell players apart'
    } else { $checked++ }
    $instanceIndex = Get-Member $game1 'Fields' 'instanceIndex'
    if (-not $instanceIndex -or -not $instanceIndex.IsPublic) {
        $failed += 'Game1.instanceIndex is gone or not public; player numbers have nowhere to come from'
    } else { $checked++ }
    $runner = $game.MainModule.GetType('StardewValley.GameRunner')
    if (-not (Get-Member $runner 'Fields' 'instance')) { $failed += 'Missing GameRunner field: instance' }
    else { $checked++ }
    if (-not (Get-Member $runner 'Methods' 'ExecuteForInstances')) { $failed += 'Missing GameRunner.ExecuteForInstances' }
    else { $checked++ }

    # ModEntry puts its messages on one player's screen.
    if (-not ($game1.Methods | Where-Object { $_.Name -eq 'addHUDMessage' -and $_.Parameters.Count -eq 1 })) {
        $failed += 'Missing Game1.addHUDMessage(HUDMessage)'
    } else { $checked++ }

    # What a toggle says has to land on the screen of the player who pressed the button, which it does
    # because the list of messages is one of the per-player statics.
    $hudMessages = Get-Member $game1 'Fields' 'hudMessages'
    if (-not $hudMessages) { $failed += 'Missing Game1.hudMessages; a toggle cannot replace its own message' }
    else {
        $checked++
        if ($hudMessages.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'StardewValley.NonInstancedStatic' }) {
            $failed += 'Game1.hudMessages is shared between players now, so a toggle would speak on every screen'
        } else { $checked++ }
    }

    $hudMessage = $game.MainModule.GetType('StardewValley.HUDMessage')
    if (-not ($hudMessage.Methods | Where-Object {
            $_.IsConstructor -and
            (@($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -like 'System.String,System.Single*' })) {
        $failed += 'Missing HUDMessage(string, float); the toggles have no way to say what they did'
    } else { $checked++ }
    if (-not (Get-Member $hudMessage 'Fields' 'noIcon')) { $failed += 'Missing HUDMessage.noIcon' }
    else { $checked++ }

    # --- the buttons on each player's own controller ------------------------------------------------
    # Taking the D-pad off walking duty rests on the game reading the pad through Game1.input, which
    # SMAPI replaces with a state it can hide buttons from.
    $updateControl = $game1.Methods | Where-Object { $_.Name -eq 'UpdateControlInput' } | Select-Object -First 1
    if (-not $updateControl -or -not $updateControl.HasBody) {
        $failed += 'Missing Game1.UpdateControlInput; the walking input is read somewhere else now'
    } else {
        $checked++
        if (-not ($updateControl.Body.Instructions | Where-Object {
                $_.OpCode.Name -like 'call*' -and $_.Operand -and $_.Operand.Name -eq 'GetGamePadState' -and
                $_.Operand.DeclaringType -and $_.Operand.DeclaringType.FullName -eq 'StardewValley.InputState' })) {
            $failed += 'Game1.UpdateControlInput no longer reads the pad through Game1.input, so suppressing the D-pad would not stop the character walking'
        } else { $checked++ }
        # The walking itself is in a closure the mod hooks hand back, so it is one of UpdateControlInput's
        # own compiler-generated methods rather than the method above.
        $walking = @(@($game1.NestedTypes | ForEach-Object { $_.Methods }) + @($updateControl) |
            Where-Object { $_.HasBody -and $_.Name -like '*UpdateControlInput*' } |
            Sort-Object { $_.Body.Instructions.Count } -Descending)[0]

        # Buttons.DPadUp, DPadDown, DPadLeft and DPadRight are 1, 2, 4 and 8, each pushed just before the
        # pad is asked about it. All four have to be read off a pad state for suppressing them to work.
        $dpad = @{}
        $instructions = @($walking.Body.Instructions)
        for ($i = 1; $i -lt $instructions.Count; $i++) {
            if (-not $instructions[$i].Operand -or $instructions[$i].Operand.Name -ne 'IsButtonDown') { continue }
            if ($instructions[$i].Operand.DeclaringType.FullName -ne 'Microsoft.Xna.Framework.Input.GamePadState') { continue }
            switch ($instructions[$i - 1].OpCode.Name) {
                'ldc.i4.1' { $dpad[1] = $true }
                'ldc.i4.2' { $dpad[2] = $true }
                'ldc.i4.4' { $dpad[4] = $true }
                'ldc.i4.8' { $dpad[8] = $true }
            }
        }
        foreach ($button in 1, 2, 4, 8) {
            if (-not $dpad[$button]) {
                $failed += "$($walking.Name) does not read D-pad button $button off a pad state any more, so suppressing it may no longer stop the character walking"
            } else { $checked++ }
        }
    }

    $inputHelper = $smapi.MainModule.GetType('StardewModdingAPI.IInputHelper')
    if (-not $inputHelper) { $failed += 'Missing StardewModdingAPI.IInputHelper' }
    else {
        $checked++
        foreach ($name in 'IsDown', 'Suppress', 'SuppressActiveKeybinds') {
            if (-not (Get-Member $inputHelper 'Methods' $name)) { $failed += "Missing IInputHelper.$name" }
            else { $checked++ }
        }
    }

    $sButton = $smapi.MainModule.GetType('StardewModdingAPI.SButton')
    foreach ($name in 'DPadUp', 'DPadDown', 'DPadLeft', 'DPadRight', 'LeftStick') {
        if (-not (Get-Member $sButton 'Fields' $name)) { $failed += "SButton no longer has $name" }
        else { $checked++ }
    }

    $context = $smapi.MainModule.GetType('StardewModdingAPI.Context')
    foreach ($name in 'get_IsPlayerFree', 'get_IsWorldReady') {
        if (-not (Get-Member $context 'Methods' $name)) {
            $failed += "Missing StardewModdingAPI.Context.$($name -replace '^get_', '')"
        } else { $checked++ }
    }

    # Suppressing a button has to outlast the tick it was asked for: SMAPI keeps it hidden until the
    # button is really let go of, which is what quietens the D-pad for a whole press from one call.
    $sInput = $smapi.MainModule.GetType('StardewModdingAPI.Framework.Input.SInputState')
    if (-not $sInput) { $failed += "Missing SMAPI's SInputState; suppression works some other way now" }
    else {
        $checked++
        foreach ($name in 'CustomPressedKeys', 'CustomReleasedKeys') {
            if (-not (Get-Member $sInput 'Fields' $name)) { $failed += "SInputState no longer has $name" }
            else { $checked++ }
        }

        $trueUpdate = $sInput.Methods | Where-Object { $_.Name -eq 'TrueUpdate' } | Select-Object -First 1
        if (-not $trueUpdate -or -not $trueUpdate.HasBody) {
            $failed += 'Missing SInputState.TrueUpdate; where overrides live from tick to tick is unknown'
        } else {
            $checked++

            # Which of the two override sets it empties each tick: the pressed one, and not the
            # released one, is what makes a suppressed button stay suppressed.
            $cleared = @()
            $instructions = @($trueUpdate.Body.Instructions)
            for ($i = 0; $i -lt $instructions.Count; $i++) {
                if ($instructions[$i].OpCode.Name -notlike 'call*') { continue }
                if (-not $instructions[$i].Operand -or $instructions[$i].Operand.Name -ne 'Clear') { continue }
                for ($back = $i - 1; $back -ge 0 -and $back -ge $i - 4; $back--) {
                    if ($instructions[$back].OpCode.Name -eq 'ldfld' -and
                        $instructions[$back].Operand.Name -like 'Custom*Keys') {
                        $cleared += $instructions[$back].Operand.Name
                    }
                }
            }
            if ($cleared -contains 'CustomReleasedKeys') {
                $failed += 'SMAPI empties CustomReleasedKeys every tick now, so a suppressed D-pad button would come back while it is still held'
            } else { $checked++ }
            # Without this the check above would pass simply by finding nothing at all.
            if ($cleared -notcontains 'CustomPressedKeys') {
                $failed += 'SInputState.TrueUpdate no longer empties CustomPressedKeys, so the check for how long suppression lasts is reading the wrong code'
            } else { $checked++ }
        }
    }

    # The handle handed to Auto Fishing has to be the interface it declares the field as.
    $reflectedField = $smapi.MainModule.GetType('StardewModdingAPI.IReflectedField`1')
    if (-not $reflectedField) { $failed += 'Missing StardewModdingAPI.IReflectedField<T>' }
    else {
        $checked++
        foreach ($name in 'GetValue', 'SetValue', 'get_FieldInfo') {
            if (-not (Get-Member $reflectedField 'Methods' $name)) {
                $failed += "IReflectedField<T> no longer has $name; ScreenMouseState does not implement it any more"
            } else { $checked++ }
        }
    }

    # --- Auto Fishing itself -----------------------------------------------------------------------
    $autoFishPath = Join-Path $AutoFishDir 'AutoFishing2.dll'
    if (-not (Test-Path -LiteralPath $autoFishPath)) {
        Write-Host "SKIPPED the Auto Fishing checks: $autoFishPath is not installed." -ForegroundColor Yellow
    } else {
        $autoFish = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $autoFishPath).Path, $reader)

        # The version the manifest says is the least this mod understands.
        $theirManifestPath = Join-Path $AutoFishDir 'manifest.json'
        if (-not (Test-Path -LiteralPath $theirManifestPath)) {
            $failed += "Auto Fishing has no manifest.json in $AutoFishDir"
        } else {
            $checked++
            $theirs = Get-Content -LiteralPath $theirManifestPath -Raw | ConvertFrom-Json
            if ($theirs.UniqueID -ne 'Tibbles.AutoFishing2') {
                $failed += "The mod in $AutoFishDir is $($theirs.UniqueID), not Tibbles.AutoFishing2"
            } else { $checked++ }
            if ($manifest -and $manifest.Dependencies) {
                $wanted = ($manifest.Dependencies | Where-Object { $_.UniqueID -eq 'Tibbles.AutoFishing2' }).MinimumVersion
                if ($wanted -and ([version]$theirs.Version -lt [version]$wanted)) {
                    $failed += "Auto Fishing $($theirs.Version) is installed but the manifest asks for $wanted or later"
                } else { $checked++ }
            }
        }

        $types = @{}
        foreach ($name in 'AutoFish', 'FishingManager', 'ModEntry', 'ModConfig') {
            $type = $autoFish.MainModule.GetType("AutoFishing2.$name")
            if (-not $type) { $failed += "Auto Fishing no longer has its $name class" }
            else {
                $checked++
                $types[$name] = $type
            }
        }

        # The three methods the patches sit in front of, each still an instance method taking what the
        # prefixes expect.
        $targets = @(
            @{ Type = 'AutoFish'; Name = 'OnUpdateTicked';
               Parameters = @('System.Object', 'StardewModdingAPI.Events.UpdateTickedEventArgs') }
            @{ Type = 'ModEntry'; Name = 'OnButtonPressed';
               Parameters = @('System.Object', 'StardewModdingAPI.Events.ButtonPressedEventArgs') }
            @{ Type = 'FishingManager'; Name = 'CloseCaughtFishDialogue';
               Parameters = @('StardewValley.Tools.FishingRod') }
        )
        foreach ($target in $targets) {
            $type = $types[$target.Type]
            $method = $null
            if ($type) {
                $method = $type.Methods | Where-Object {
                    $_.Name -eq $target.Name -and
                    (@($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq ($target.Parameters -join ',')
                } | Select-Object -First 1
            }
            if (-not $method) {
                $failed += "Missing patch target: $($target.Type).$($target.Name)($($target.Parameters -join ', '))"
                continue
            }
            $checked++
            if ($method.IsStatic) {
                $failed += "$($target.Type).$($target.Name) is static now; the prefixes ask for __instance"
            } else { $checked++ }
        }

        # The settings object the players take turns with, reached from both of the methods that use it.
        foreach ($pair in @(@{ Type = 'AutoFish'; Field = 'config' }, @{ Type = 'ModEntry'; Field = '_config' })) {
            $field = Get-Member $types[$pair.Type] 'Fields' $pair.Field
            if (-not $field) { $failed += "Missing $($pair.Type).$($pair.Field)" }
            elseif ($field.FieldType.FullName -ne 'AutoFishing2.ModConfig') {
                $failed += "$($pair.Type).$($pair.Field) is a $($field.FieldType.FullName), expected AutoFishing2.ModConfig"
            } else { $checked++ }
        }

        # The cached mouse handle this mod replaces with a screen-aware one.
        $cached = Get-Member $types['FishingManager'] 'Fields' '_currentMouseState'
        if (-not $cached) { $failed += 'Missing FishingManager._currentMouseState' }
        elseif ($cached.FieldType.FullName -ne 'StardewModdingAPI.IReflectedField`1<Microsoft.Xna.Framework.Input.MouseState>') {
            $failed += "FishingManager._currentMouseState is a $($cached.FieldType.FullName), which ScreenMouseState cannot stand in for"
        } else { $checked++ }

        # Every setting the swap covers has to be there, and writable.
        $settings = @{ AutoCast = 'System.Boolean'; AutoFish = 'System.Boolean'; AutoHit = 'System.Boolean';
            CaughtDoubleFish = 'System.Boolean'; CastingPower = 'System.Single'; StaminaThreshold = 'System.Single' }
        foreach ($name in $settings.Keys) {
            $type = $settings[$name]
            $field = Get-Member $types['ModConfig'] 'Fields' $name
            $property = Get-Member $types['ModConfig'] 'Properties' $name
            if ($field) {
                if ($field.FieldType.FullName -ne $type) {
                    $failed += "Auto Fishing's $name is a $($field.FieldType.FullName), expected $type"
                } elseif ($field.IsInitOnly) {
                    $failed += "Auto Fishing's $name is readonly now, so it cannot be swapped"
                } else { $checked++ }
            } elseif ($property) {
                if ($property.PropertyType.FullName -ne $type) {
                    $failed += "Auto Fishing's $name is a $($property.PropertyType.FullName), expected $type"
                } elseif (-not $property.GetMethod -or -not $property.SetMethod) {
                    $failed += "Auto Fishing's $name can no longer be both read and written"
                } else { $checked++ }
            } else {
                $failed += "Auto Fishing no longer has a $name setting"
            }
        }

        # The swap is only worth anything if Auto Fishing really does read that object as it works, and
        # the mouse handle is only worth replacing if it really does use the cached one.
        $onUpdate = $types['AutoFish'].Methods | Where-Object { $_.Name -eq 'OnUpdateTicked' } | Select-Object -First 1
        if ($onUpdate -and $onUpdate.HasBody) {
            if (-not ($onUpdate.Body.Instructions | Where-Object {
                    $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'config' })) {
                $failed += 'AutoFish.OnUpdateTicked no longer reads its config field; the per-player swap would do nothing'
            } else { $checked++ }
        }
        $close = $types['FishingManager'].Methods |
            Where-Object { $_.Name -eq 'CloseCaughtFishDialogue' } | Select-Object -First 1
        if ($close -and $close.HasBody) {
            if (-not ($close.Body.Instructions | Where-Object {
                    $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq '_currentMouseState' })) {
                $failed += 'FishingManager.CloseCaughtFishDialogue no longer uses the cached mouse handle'
            } else { $checked++ }
        }
    }

    if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
    Write-Host "PASS: $checked references and assumptions hold against the installed game, SMAPI and Auto Fishing."
} finally {
    $assembly.Dispose()
    $game.Dispose()
    $smapi.Dispose()
    if ($autoFish) { $autoFish.Dispose() }
    $resolver.Dispose()
}
