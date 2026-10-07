# LemonCoop save guard.
#
# Playing co-op can make the game save with every shop-upgrade flag cleared (money, level, day and
# recipes stay correct — only the four "purchased" arrays are wiped). This watches the save folder
# and restores those arrays right after such a write, so a co-op session cannot cost you upgrades.
#
# Safety: it only acts when ALL flags in a room are 0 AND the save is an advanced game
# (SV_Day >= $MinDay). A genuinely new game is left alone.
#
# Usage: SaveGuard.ps1            # watch until the game exits
#        SaveGuard.ps1 -Once      # repair once and exit
param([switch]$Once, [int]$MinDay = 10)
$ErrorActionPreference = "Stop"

$saves = Join-Path $env:LOCALAPPDATA "LemonCake\Saved\SaveGames"
$logFile = Join-Path $env:LOCALAPPDATA "LemonCake\Saved\SaveGuard.log"
$arrays = @("SV_IsStoreUpgradePurchased", "SV_IsKitchenUpgradePurchased",
            "SV_IsGreenhouseUpgradePurchased", "SV_IsBedroomUpgradePurchased")
$latin1 = [System.Text.Encoding]::GetEncoding(28591)   # 1 byte per char: string index == byte offset

function Write-Log([string]$m) {
  $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $m
  Add-Content -Path $logFile -Value $line -Encoding ASCII
}

# Reads an IntProperty's value by name, or $null.
function Get-IntProp($bytes, $text, [string]$name) {
  $i = $text.IndexOf($name)
  if ($i -lt 0) { return $null }
  $j = $text.IndexOf("IntProperty", $i)
  if ($j -lt 0 -or ($j - $i) -gt 64) { return $null }
  # "IntProperty" + NUL + int64 size + guid byte, then the int32 value
  $p = $j + 11 + 1 + 8 + 1
  if ($p + 4 -gt $bytes.Length) { return $null }
  return [BitConverter]::ToInt32($bytes, $p)
}

function Repair-Save([string]$path) {
  $bytes = [System.IO.File]::ReadAllBytes($path)
  if ($bytes.Length -lt 64) { return 0 }
  if ([System.Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne "GVAS") { return 0 }
  $text = $latin1.GetString($bytes)

  $day = Get-IntProp $bytes $text "SV_Day"
  if ($null -eq $day) { return 0 }
  if ($day -lt $MinDay) { return 0 }        # new game: never touch

  $fixed = @()
  foreach ($name in $arrays) {
    $i = $text.IndexOf($name)
    if ($i -lt 0) { continue }
    $j = $text.IndexOf("BoolProperty", $i)
    if ($j -lt 0 -or ($j - $i) -gt 96) { continue }
    # "BoolProperty" + NUL + guid byte, then int32 count, then one byte per element
    $p = $j + 12 + 1 + 1
    if ($p + 4 -gt $bytes.Length) { continue }
    $count = [BitConverter]::ToInt32($bytes, $p)
    $p += 4
    if ($count -lt 1 -or $count -gt 64 -or ($p + $count) -gt $bytes.Length) { continue }
    $allZero = $true
    for ($k = 0; $k -lt $count; $k++) { if ($bytes[$p + $k] -ne 0) { $allZero = $false; break } }
    if (-not $allZero) { continue }
    for ($k = 0; $k -lt $count; $k++) { $bytes[$p + $k] = 1 }
    $fixed += "$name x$count"
  }
  if ($fixed.Count -eq 0) { return 0 }

  # keep a copy of what the game wrote, then write the repair
  $side = Join-Path $env:LOCALAPPDATA ("LemonCake\Saved\SaveGuard_wiped\" + (Get-Date -Format "yyyyMMdd_HHmmss"))
  New-Item -ItemType Directory -Path $side -Force | Out-Null
  Copy-Item -Path $path -Destination $side -Force
  [System.IO.File]::WriteAllBytes($path, $bytes)
  Write-Log ("repaired {0} (day {1}): {2}" -f (Split-Path $path -Leaf), $day, ($fixed -join ", "))
  return $fixed.Count
}

function Scan {
  $n = 0
  Get-ChildItem -Path $saves -Filter "SaveSlot*.sav" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "*Global*" } |
    ForEach-Object {
      try { $n += Repair-Save $_.FullName } catch { Write-Log ("error on {0}: {1}" -f $_.Name, $_.Exception.Message) }
    }
  return $n
}

if ($Once) { $c = Scan; Write-Log "one-shot scan: $c array(s) repaired"; exit 0 }

Write-Log "guard started (MinDay=$MinDay)"
$stamps = @{}
while ($true) {
  $proc = Get-Process -Name "LemonCake-Win64-Shipping" -ErrorAction SilentlyContinue
  if (-not $proc) { Write-Log "game closed; guard exiting"; break }
  Get-ChildItem -Path $saves -Filter "SaveSlot*.sav" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "*Global*" } |
    ForEach-Object {
      $key = $_.FullName
      $sig = "$($_.Length):$($_.LastWriteTimeUtc.Ticks)"
      if ($stamps[$key] -ne $sig) {
        Start-Sleep -Milliseconds 400          # let the game finish writing
        try { Repair-Save $key | Out-Null } catch { Write-Log ("error: " + $_.Exception.Message) }
        $f = Get-Item $key -ErrorAction SilentlyContinue
        if ($f) { $stamps[$key] = "$($f.Length):$($f.LastWriteTimeUtc.Ticks)" }
      }
    }
  Start-Sleep -Seconds 2
}
exit 0
