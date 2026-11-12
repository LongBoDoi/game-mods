<#
    The offline checks for the Project Zomboid mods: the layout maths with no game involved, and -
    against the installed projectzomboid.jar - that every patch still lands where the mod expects
    it to, so a game update that moved something shows up here rather than in a session.

    Uses the same JDK as the build (MonitorCoop\build.ps1 finds or fetches it) and the game found
    through Steam, unless -GameDir says otherwise. -NoGame runs the layout maths only.
#>
param(
    [string]$GameDir,
    [switch]$NoGame
)

$ErrorActionPreference = 'Stop'

$here     = $PSScriptRoot
$gameRoot = Split-Path $here -Parent
$repoRoot = Split-Path $gameRoot -Parent
$modSrc   = Join-Path $gameRoot 'MonitorCoop\src'
$out      = Join-Path $here 'build'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Invoke-Tool($exe, $toolArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @toolArgs | Out-Host
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { throw "$(Split-Path $exe -Leaf) failed (exit $code)" }
}

function Get-JavacFeature($javac) {
    if (-not $javac -or -not (Test-Path -LiteralPath $javac)) { return 0 }
    try {
        $text = & $javac -version 2>&1 | Out-String
        if ($text -match 'javac\s+(\d+)') { return [int]$matches[1] }
    } catch {}
    return 0
}

function Test-GameDir($dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'projectzomboid.jar'))
}

function Find-GameDir {
    $steamRoots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($item.SteamPath)   { $steamRoots += $item.SteamPath }
            if ($item.InstallPath) { $steamRoots += $item.InstallPath }
        } catch {}
    }
    $libraries = @()
    foreach ($root in ($steamRoots | Select-Object -Unique)) {
        $root = $root -replace '/', '\'
        $libraries += $root
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        foreach ($c in @((Join-Path $lib 'steamapps\common\ProjectZomboid'))) {
            if (Test-GameDir $c) { return $c }
        }
    }
    return $null
}

# --- The game and the JDK ----------------------------------------------------------------------
if (-not $NoGame) {
    if (-not $GameDir) { $GameDir = Find-GameDir }
    if (-not (Test-GameDir $GameDir)) {
        Write-Host 'Could not find Project Zomboid; running the layout checks only.' -ForegroundColor Yellow
        $GameDir = $null
    } else {
        $GameDir = (Resolve-Path -LiteralPath $GameDir).Path
    }
} else {
    $GameDir = $null
}

$candidates = @()
if ($env:JAVA_HOME) { $candidates += (Join-Path $env:JAVA_HOME 'bin\javac.exe') }
$onPath = Get-Command javac.exe -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }
$candidates += (Join-Path $repoRoot '.tools\jdk\bin\javac.exe')

$javac = $null
foreach ($candidate in $candidates) {
    if ((Get-JavacFeature $candidate) -ge 24) { $javac = $candidate; break }
}
if (-not $javac) { throw 'No Java 24 or newer JDK found. Run MonitorCoop\build.cmd once; it fetches one.' }
$java = Join-Path (Split-Path $javac -Parent) 'java.exe'

# --- Compile the mod and the checks ------------------------------------------------------------
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$sources = @()
$sources += Get-ChildItem -LiteralPath (Join-Path $modSrc 'monitorcoop') -Filter *.java | ForEach-Object FullName
$sources += Get-ChildItem -LiteralPath (Join-Path $here 'src\monitorcoop') -Filter *.java | ForEach-Object FullName

$classpath = if ($GameDir) { Join-Path $GameDir 'projectzomboid.jar' } else { '.' }
Step "Compiling $($sources.Count) files"
Invoke-Tool $javac (@('-nowarn', '-classpath', $classpath, '-d', $out) + $sources)

Step 'Running the checks'
Invoke-Tool $java @('-classpath', "$out;$classpath", 'monitorcoop.Tests', [string]$GameDir)
