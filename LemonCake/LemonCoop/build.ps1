<#
    Builds LemonCoop for release:
      - packs pak\ into LemonCake-WindowsNoEditor_P.pak (repak, UE 4.24 = V8B, mount ../../../),
        which adds the gamepad bindings to LemonCake/Config/DefaultInput.ini;
      - stages the pak and the UE4SS Lua mod (Scripts\, DualMonitor.ps1, README.md) in ..\dist\.

    -Install then runs ..\installer\Install.ps1 on the result.
    repak is taken from $env:REPAK, else <repo>\.tools\repak, else downloaded there.
#>
param([switch]$Install)

$ErrorActionPreference = 'Stop'
$RepakVersion = '0.2.3'
$RepakUrl     = "https://github.com/trumank/repak/releases/download/v$RepakVersion/repak_cli-x86_64-pc-windows-msvc.zip"

# This folder may be reached through the game's Mods\LemonCoop junction; work from the real one.
$here = $PSScriptRoot
$item = Get-Item -LiteralPath $here
if ($item.LinkType -eq 'Junction') { $here = @($item.Target)[0] }
$gameRoot = Split-Path $here -Parent            # <repo>\LemonCake
$repoRoot = Split-Path $gameRoot -Parent
$dist     = Join-Path $gameRoot 'dist'

function Get-Repak {
    if ($env:REPAK -and (Test-Path -LiteralPath $env:REPAK)) { return $env:REPAK }
    $exe = Join-Path $repoRoot '.tools\repak\repak.exe'
    if (Test-Path -LiteralPath $exe) { return $exe }

    Write-Host "Downloading repak $RepakVersion..."
    $zip = Join-Path $env:TEMP 'repak.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $RepakUrl -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath (Split-Path $exe) -Force
    Remove-Item -LiteralPath $zip -Force
    if (-not (Test-Path -LiteralPath $exe)) { throw "repak.exe not found after extracting $RepakUrl" }
    return $exe
}

$repak = Get-Repak

$stage = Join-Path $dist 'LemonCoop'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Scripts') | Out-Null

Write-Host 'Packing LemonCake-WindowsNoEditor_P.pak'
$pak = Join-Path $dist 'LemonCake-WindowsNoEditor_P.pak'
& $repak pack --version V8B --mount-point '../../../' --quiet (Join-Path $here 'pak') $pak
if ($LASTEXITCODE -ne 0) { throw "repak failed ($LASTEXITCODE)" }

Write-Host 'Staging LemonCoop'
Copy-Item -LiteralPath (Join-Path $here 'Scripts\main.lua') -Destination (Join-Path $stage 'Scripts')
Copy-Item -LiteralPath (Join-Path $here 'DualMonitor.ps1'), (Join-Path $here 'README.md') -Destination $stage

Write-Host "Built -> $dist"

if ($Install) {
    & (Join-Path $gameRoot 'installer\Install.ps1')
    exit $LASTEXITCODE
}
