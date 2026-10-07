# LemonCoop save safety net. Launched by the mod at load; can also be run by hand.
# Copies the current .sav files into a timestamped folder and keeps the newest $Keep.
param([int]$Keep = 15)
$ErrorActionPreference = "SilentlyContinue"
$saves = Join-Path $env:LOCALAPPDATA "LemonCake\Saved\SaveGames"
$root = Join-Path $env:LOCALAPPDATA "LemonCake\Saved\SaveGames_LemonCoopBackups"
if (-not (Test-Path $saves)) { exit 0 }
$files = Get-ChildItem -Path $saves -Filter *.sav -File
if (-not $files) { exit 0 }

# Skip if the newest backup already matches the current files (nothing played since).
$sig = ($files | Sort-Object Name | ForEach-Object { "$($_.Name):$($_.Length):$($_.LastWriteTimeUtc.Ticks)" }) -join "|"
$last = Get-ChildItem -Path $root -Directory | Sort-Object Name -Descending | Select-Object -First 1
if ($last) {
  $lastSigFile = Join-Path $last.FullName "signature.txt"
  if ((Test-Path $lastSigFile) -and ((Get-Content $lastSigFile -Raw).Trim() -eq $sig)) { exit 0 }
}

$dest = Join-Path $root (Get-Date -Format "yyyyMMdd_HHmmss")
New-Item -ItemType Directory -Path $dest -Force | Out-Null
$files | Copy-Item -Destination $dest -Force
Set-Content -Path (Join-Path $dest "signature.txt") -Value $sig -Encoding ASCII

# Prune oldest.
Get-ChildItem -Path $root -Directory | Sort-Object Name -Descending | Select-Object -Skip $Keep |
  Remove-Item -Recurse -Force
exit 0
