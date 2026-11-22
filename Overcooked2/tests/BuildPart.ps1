<#
    Compiles ChefSkills' core and framework plus only the given special-skill module files into a
    private folder, then runs CheckChefSkills.ps1 on the result. Lets several modules be written
    and checked at the same time without touching bin\ or dist\ (or each other's half-done files).

        powershell -NoProfile -ExecutionPolicy Bypass -File tests\BuildPart.ps1 -Modules Movement.cs
        powershell -NoProfile -ExecutionPolicy Bypass -File tests\BuildPart.ps1 -Modules *      (every module)

    Module files live in ChefSkills\Specials\Modules\.
#>
param(
    [Parameter(Mandatory = $true)][string[]]$Modules,
    [string]$GameDir = 'E:\Steam\steamapps\common\Overcooked! 2'
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..\ChefSkills'
$managed = Join-Path $GameDir 'Overcooked2_Data\Managed'
$core = Join-Path $GameDir 'BepInEx\core'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'

$sources = @(Get-ChildItem (Join-Path $root '*.cs')) + @(Get-ChildItem (Join-Path $root 'Specials\*.cs'))
$moduleDir = Join-Path $root 'Specials\Modules'
if ($Modules.Count -eq 1 -and $Modules[0] -eq '*') {
    $sources += @(Get-ChildItem (Join-Path $moduleDir '*.cs'))
} else {
    foreach ($module in $Modules) {
        $path = Join-Path $moduleDir ([IO.Path]::GetFileName($module))
        if (-not (Test-Path $path)) { throw "No module file $path" }
        $sources += Get-Item $path
    }
}

$name = ($Modules -join '_') -replace '[^A-Za-z0-9_]', ''
if (-not $name) { $name = 'all' }
$out = Join-Path ([IO.Path]::GetTempPath()) ("ChefSkillsPart_" + $name)
New-Item -ItemType Directory -Force $out | Out-Null
$dll = Join-Path $out 'ChefSkills.dll'

$references = 'mscorlib', 'System', 'System.Core', 'Assembly-CSharp', 'Assembly-CSharp-firstpass', 'UnityEngine',
    'UnityEngine.CoreModule', 'UnityEngine.UI', 'UnityEngine.UIModule', 'UnityEngine.TextRenderingModule',
    'UnityEngine.InputModule', 'UnityEngine.IMGUIModule', 'UnityEngine.AnimationModule', 'UnityEngine.AudioModule',
    'UnityEngine.PhysicsModule', 'UnityEngine.ImageConversionModule' | ForEach-Object { '/r:' + (Join-Path $managed ($_ + '.dll')) }
$references += '/r:' + (Join-Path $core 'BepInEx.dll')
$references += '/r:' + (Join-Path $core '0Harmony.dll')

$arguments = @('/nologo', '/noconfig', '/nostdlib+', '/target:library', '/optimize+', '/langversion:5',
    '/nowarn:0649', "/out:$dll") + $references + @($sources | ForEach-Object { $_.FullName })
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build FAILED.' }
Write-Host "Built $dll"
& (Join-Path $PSScriptRoot 'CheckChefSkills.ps1') -GameDir $GameDir -PluginPath $dll
