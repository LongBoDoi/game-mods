<#
    Checks the built TiengViet against the installed game.

    The compiler catches renames in what the mod calls directly. What it cannot catch is what is
    reached by name at runtime - Harmony patch targets and the private fields read by reflection -
    so each of those is asserted here, along with the plugin GUID (the config file is named after
    it) and the translation table built into the DLL.

    Run it after TiengViet\build.cmd:
        powershell -NoProfile -File tests\CheckTiengViet.ps1
#>
param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Overcooked! 2',
    [string]$PluginPath = (Join-Path $PSScriptRoot '..\dist\TiengViet.dll')
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')
$managed = Join-Path $GameDir 'Overcooked2_Data\Managed'
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.RemoveSearchDirectory('.')
$resolver.RemoveSearchDirectory('bin')
$resolver.AddSearchDirectory($managed)
$resolver.AddSearchDirectory((Join-Path $GameDir 'BepInEx\core'))
$reader = New-Object Mono.Cecil.ReaderParameters
$reader.AssemblyResolver = $resolver
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $PluginPath).Path, $reader)
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'Assembly-CSharp.dll'), $reader)
$ui = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'UnityEngine.UI.dll'), $reader)
$failed = @()
$checked = 0

# --- everything the mod compiles against still exists -------------------------------------
foreach ($reference in @($assembly.MainModule.GetTypeReferences()) + @($assembly.MainModule.GetMemberReferences())) {
    if ($reference -isnot [Mono.Cecil.TypeReference] -and $reference -isnot [Mono.Cecil.MethodReference] -and
        $reference -isnot [Mono.Cecil.FieldReference]) { continue }
    try {
        if (-not $reference.Resolve()) { $failed += "Missing runtime reference: $($reference.FullName)" } else { $checked++ }
    } catch { $failed += "Cannot resolve $($reference.FullName): $($_.Exception.Message)" }
}

# --- what is reached by name ------------------------------------------------------------------
function Find-Type($module, $name) {
    $type = $module.MainModule.GetType($name)
    if (-not $type) { $script:failed += "Missing type $name" }
    return $type
}
function Assert-Field($module, $typeName, $field) {
    $type = Find-Type $module $typeName
    if ($type -and -not ($type.Fields | Where-Object { $_.Name -eq $field })) { $script:failed += "Missing field $typeName.$field" }
    else { $script:checked++ }
}
function Assert-Method($module, $typeName, $method, $parameters) {
    $type = Find-Type $module $typeName
    if (-not $type) { return }
    $found = $type.Methods | Where-Object {
        $_.Name -eq $method -and ($parameters -eq $null -or
            (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq $parameters)
    }
    if (-not $found) { $script:failed += "Missing method $typeName.$method($parameters)" } else { $script:checked++ }
}

Assert-Method $game 'Localization' 'Get' 'System.String,System.String&,LocToken[]'   # LocalizationGet
Assert-Method $game 'Localization' 'GetLanguage' ''                                  # LanguageGet
Assert-Method $game 'Localization' 'CheckForMarkup' $null                            # button markers in vi.tsv
Assert-Field  $game 'T17Text' 'm_TextProcessors'                                      # Refresh: has it started?
Assert-Field  $game 'LocalisedText' 'm_tag'                                           # Refresh
Assert-Field  $game 'BaseUIOption`1' 'm_OptionType'                                   # LanguageRow
Assert-Field  $game 'BaseUIOption`1' 'm_Option'
Assert-Field  $game 'SelectorOption' 'm_LocalizeOption'
Assert-Method $game 'T17ScrollView' 'SingleTimeInitialize' ''                         # LanguageRow patch
Assert-Field  $game 'FrontendOptionsMenu' 'm_ScrollView'
Assert-Method $ui 'UnityEngine.UI.Text' 'set_text' 'System.String'                    # HeaderFont patches
Assert-Method $ui 'UnityEngine.UI.Text' 'OnEnable' ''
Assert-Method $ui 'UnityEngine.UI.Text' 'OnPopulateMesh' 'UnityEngine.UI.VertexHelper'

# --- plugin identity and the table ------------------------------------------------------------
$plugin = $assembly.MainModule.GetType('TiengViet.Plugin')
$attribute = if ($plugin) { $plugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' } | Select-Object -First 1 }
if (-not $attribute) { $failed += 'TiengViet.Plugin has no [BepInPlugin]' }
elseif ($attribute.ConstructorArguments[0].Value -ne 'com.manhl.overcooked2.tiengviet') {
    $failed += "GUID is '$($attribute.ConstructorArguments[0].Value)', expected com.manhl.overcooked2.tiengviet"
} else { $checked++ }

$resource = $assembly.MainModule.Resources | Where-Object { $_.Name -eq 'TiengViet.vi.tsv' }
if (-not $resource) { $failed += 'vi.tsv is not built into the DLL' }
else {
    $text = [Text.Encoding]::UTF8.GetString($resource.GetResourceData())
    $rows = @($text -split "`n" | Where-Object { $_ -and -not $_.StartsWith('#') })
    $bad = @($rows | Where-Object { ($_ -split "`t").Count -ne 3 })
    if ($rows.Count -lt 1300) { $failed += "vi.tsv has only $($rows.Count) rows" } else { $checked++ }
    if ($bad.Count) { $failed += "vi.tsv has $($bad.Count) rows without exactly 3 cells, e.g. $($bad[0])" } else { $checked++ }
}

$resource = $assembly.MainModule.Resources | Where-Object { $_.Name -eq 'TiengViet.mods.tsv' }
if (-not $resource) { $failed += 'mods.tsv is not built into the DLL' }
else {
    $text = [Text.Encoding]::UTF8.GetString($resource.GetResourceData())
    $rows = @($text -split "`n" | Where-Object { $_ -and -not $_.StartsWith('#') })
    $bad = @($rows | Where-Object { $cells = $_ -split "`t"; $cells.Count -ne 4 -or -not $cells[3] -or
        @('ModSettings', 'HostTools', 'Setting') -notcontains $cells[0] })
    if ($rows.Count -lt 600) { $failed += "mods.tsv has only $($rows.Count) rows" } else { $checked++ }
    if ($bad.Count) { $failed += "mods.tsv has $($bad.Count) bad rows, e.g. $($bad[0])" } else { $checked++ }
}

# The template for the game's title font with Vietnamese letters (GameTitleFont.cs): LZ4 with
# its size first, unpacking to a bundle with room for the font.
$resource = $assembly.MainModule.Resources | Where-Object { $_.Name -eq 'TiengViet.title.bundle.lz4' }
if (-not $resource) { $failed += 'title.bundle.lz4 is not built into the DLL' }
else {
    $data = $resource.GetResourceData()
    $size = [BitConverter]::ToInt32($data, 0)
    if ($size -lt 400000 -or $size -gt 2000000) { $failed += "title.bundle.lz4 unpacks to $size bytes" } else { $checked++ }
}

# The title font is made on the player's PC, so make it here from the installed game: find
# Naughty Squirrel in the game's font bundle and add the Vietnamese letters.
$bundle22 = Join-Path $GameDir 'Overcooked2_Data\StreamingAssets\Windows\bundle22'
if (Test-Path -LiteralPath $bundle22) {
    try {
        $searchDirs = @($managed, (Join-Path $GameDir 'BepInEx\core'))
        [AppDomain]::CurrentDomain.add_AssemblyResolve({
            param($sender, $e)
            $file = (New-Object Reflection.AssemblyName $e.Name).Name + '.dll'
            foreach ($dir in $searchDirs) {
                $path = Join-Path $dir $file
                if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::LoadFrom($path) }
            }
            return $null
        }.GetNewClosure())
        $mod = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $PluginPath).Path))
        $any = [Reflection.BindingFlags]'Static, NonPublic, Public'
        $font = $mod.GetType('TiengViet.GameTitleFont+Bundles').GetMethod('FindFont', $any).Invoke($null, [object[]]@([string]$bundle22, 'Naughty Squirrel'))
        if (-not $font) { $failed += 'Naughty Squirrel is not in the game''s bundle22 any more' }
        else {
            $call = [object[]]@($font, 0)
            $built = $mod.GetType('TiengViet.VietGlyphs').GetMethod('Build', $any).Invoke($null, $call)
            if ($call[1] -ne 94) { $failed += "The title font got $($call[1]) Vietnamese letters, expected 94" } else { $checked++ }
            if ($built.Length -gt 384 * 1024) { $failed += "The title font is $($built.Length) bytes, more than its 384 KB room" } else { $checked++ }
        }
    } catch {
        $inner = $_.Exception
        while ($inner.InnerException) { $inner = $inner.InnerException }
        $failed += "Could not make the title font: $($inner.Message)"
    }
}

# --- other mods' text tables (only checked when that mod is installed) -----------------------
$plugins = Join-Path $GameDir 'BepInEx\plugins'
$modSettings = Get-ChildItem $plugins -Recurse -Filter 'OC2ModSettings.dll' | Select-Object -First 1
if ($modSettings) {
    $ms = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($modSettings.FullName, $reader)
    Assert-Method $ms 'OC2ModSettings.Texts' 'Resolve' $null                  # ModTexts.ModSettingsText
    Assert-Method $ms 'OC2ModSettings.EntryMetadata' 'Read' $null             # ModTexts.SettingNames
    Assert-Method $ms 'OC2ModSettings.RegisteredAction' 'Metadata' ''
    foreach ($f in 'Name', 'Category', 'Description') { Assert-Field $ms 'OC2ModSettings.EntryMetadata' $f }
    Assert-Field  $ms 'OC2ModSettings.NativePage' 'language'                   # ModTexts.Relabel
    Assert-Field  $ms 'OC2ModSettings.NativeEntry' 'language'
    Assert-Field  $ms 'OC2ModSettings.ModSettingsPlugin' 'Instance'
    Assert-Field  $ms 'OC2ModSettings.ModSettingsPlugin' 'Catalog'
    Assert-Field  $ms 'OC2ModSettings.ModSettingsPlugin' 'entry'
    Assert-Method $ms 'OC2ModSettings.ConfigCatalog' 'Invalidate' ''
    Assert-Method $ms 'OC2ModSettings.NativeFontLease' 'Keep' 'UnityEngine.Font' # HeaderFont.GameFontFirst
}
$hostTools = Get-ChildItem $plugins -Recurse -Filter 'HostUtilities.dll' | Sort-Object FullName -Descending | Select-Object -First 1
if ($hostTools) {
    $hu = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($hostTools.FullName, $reader)
    Assert-Method $hu 'HostUtilities.Language' 'GetText' 'System.String,System.String'   # ModTexts.HostToolsText
    foreach ($f in 'languageDict', 'configTranslationKeys', 'configTranslationKeyByText', 'publishedGameLanguageCode') {
        Assert-Field $hu 'HostUtilities.Language' $f                                       # Teach, Relabel
    }
}

if ($failed.Count) {
    $failed | ForEach-Object { Write-Host "FAIL $_" -ForegroundColor Red }
    exit 1
}
Write-Host "TiengViet: $checked checks passed." -ForegroundColor Green
