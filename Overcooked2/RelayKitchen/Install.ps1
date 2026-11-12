param([string]$GameDir)
$ErrorActionPreference = 'Stop'
$levelId = 'manhl_relay_kitchen'
$payload = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'info_manhl_relay')) {
    $PSScriptRoot
} else { Join-Path $PSScriptRoot "build\$levelId" }

function Test-Game($dir) {
    try { return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'Overcooked2.exe')) } catch { return $false }
}
if (-not $GameDir) {
    $roots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        try {
            $entry = Get-ItemProperty -Path $key
            if ($entry.SteamPath) { $roots += $entry.SteamPath }
            if ($entry.InstallPath) { $roots += $entry.InstallPath }
        } catch {}
    }
    foreach ($root in @($roots | Select-Object -Unique)) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $roots += $m.Groups[1].Value -replace '\\\\', '\'
            }
        }
    }
    foreach ($root in $roots | Select-Object -Unique) {
        $candidate = Join-Path $root 'steamapps\common\Overcooked! 2'
        if (Test-Game $candidate) { $GameDir = $candidate; break }
    }
}
if (-not (Test-Game $GameDir)) { throw 'Cannot find Overcooked! 2. Run Install.cmd -GameDir "<game folder>".' }
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$running = @(Get-Process -Name Overcooked2 -ErrorAction SilentlyContinue | Where-Object {
    try { (-not $_.Path) -or ($_.Path -eq (Join-Path $GameDir 'Overcooked2.exe')) } catch { $true }
})
if ($running.Count) { throw 'Close Overcooked! 2 before installing this level. Nothing was changed.' }
$diy = Join-Path $GameDir 'BepInEx\plugins\OC2DIYLevel'
foreach ($name in 'OC2DIYLevel.dll', 'common01', 'common02', 'common03') {
    if (-not (Test-Path -LiteralPath (Join-Path $diy $name))) { throw "Install the OC2DIYLevel pack first (missing $name)." }
}
$names = @('info_manhl_relay', 's_manhl_relay_kitchen_1', 'layout.png', 'build-report.json')
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name))) { throw 'Build this level first; see README.md.' }
}
$report = Get-Content -LiteralPath (Join-Path $payload 'build-report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($name in $names | Where-Object { $_ -ne 'build-report.json' }) {
    $expected = $report.outputs.$name
    if (-not $expected -or (Get-FileHash -LiteralPath (Join-Path $payload $name) -Algorithm SHA256).Hash -ne $expected) {
        throw "Payload checksum mismatch: $name"
    }
}
foreach ($name in 'common01', 'common02', 'common03') {
    if ((Get-FileHash -LiteralPath (Join-Path $diy $name) -Algorithm SHA256).Hash -ne $report.inputs.$name) {
        throw "This level was built for different OC2DIYLevel assets ($name). Build it again with --source pointing at the installed OC2DIYLevel folder."
    }
}
$levels = [IO.Path]::GetFullPath((Join-Path $diy 'levels'))
$destination = [IO.Path]::GetFullPath((Join-Path $levels $levelId))
if (-not $destination.StartsWith($levels + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid level destination.' }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($name in $names) {
    Copy-Item -LiteralPath (Join-Path $payload $name) -Destination (Join-Path $destination $name) -Force
}
Write-Host "Installed Relay Kitchen into $destination" -ForegroundColor Green
Write-Host 'Open More Levels > GameMods Originals > Night Market Relay.'
Write-Host 'Every player needs this map and the same OC2DIYLevel pack.'
