<#
    Builds MonitorCoop.jar (the Java agent that gives each local split-screen player a monitor) and
    stages it in ..\dist\MonitorCoop\ for scripts\Release.ps1.

    -Install then runs ..\installer\Install.ps1 on the result.
    -GameDir  the game folder to compile against, if it is not found through Steam.

    The mod is compiled against the game's own projectzomboid.jar, the way the C# mods in this repo
    are compiled against their game's assemblies, and needs nothing else at runtime: the patching
    uses the class file tools in the Java runtime the game already ships with.

    JDK: $env:JAVA_HOME, else a javac on PATH, else <repo>\.tools\jdk, else Temurin is downloaded
    there (about 190 MB, once). Which version is needed comes from the game's own jar - a compiler
    cannot read class files newer than itself - with a floor of Java 24, where the class file API
    the patches are written against arrived.
#>
param(
    [string]$GameDir,
    [switch]$Install,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$JdkUrlFormat = 'https://api.adoptium.net/v3/binary/latest/{0}/ga/windows/x64/jdk/hotspot/normal/eclipse'
$FloorJava    = 24          # the class file API the patches use arrived in Java 24
$AppId        = '108600'
$GameFolder   = 'ProjectZomboid'

$here     = $PSScriptRoot
$gameRoot = Split-Path $here -Parent          # <repo>\ProjectZomboid
$repoRoot = Split-Path $gameRoot -Parent
$dist     = Join-Path $gameRoot 'dist\MonitorCoop'
$out      = Join-Path $here 'build'
$classes  = Join-Path $out 'classes'

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'      # the progress bar makes 5.1's downloads crawl

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# Runs javac or jar. Their warnings come out on stderr, which PowerShell 5.1 would otherwise turn
# into a terminating error; the exit code is what says whether the tool was happy.
function Invoke-Tool($exe, $toolArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @toolArgs
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { throw "$(Split-Path $exe -Leaf) failed (exit $code)" }
}

# Which Java the game's own classes were built for: a compiler cannot read anything newer.
function Get-JarJavaFeature($jarPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($jarPath)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'zombie/core/Core.class' } | Select-Object -First 1
        if (-not $entry) { $entry = $zip.Entries | Where-Object { $_.FullName.EndsWith('.class') } | Select-Object -First 1 }
        if (-not $entry) { return 0 }
        $stream = $entry.Open()
        try {
            $head = New-Object byte[] 8
            $read = $stream.Read($head, 0, 8)
            if ($read -lt 8) { return 0 }
            $major = $head[6] * 256 + $head[7]
            return $major - 44          # class file major 65 is Java 21, 69 is Java 25
        } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
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
        $candidates = @()
        $manifest = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path -LiteralPath $manifest) {
            $m = [regex]::Match((Get-Content -LiteralPath $manifest -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $candidates += Join-Path $lib "steamapps\common\$($m.Groups[1].Value)" }
        }
        $candidates += Join-Path $lib "steamapps\common\$GameFolder"
        foreach ($c in $candidates) {
            if (Test-GameDir $c) { return $c }
        }
    }
    return $null
}

# The feature version of a JDK, or 0 when that javac cannot be asked.
function Get-JavacFeature($javac) {
    if (-not $javac -or -not (Test-Path -LiteralPath $javac)) { return 0 }
    try {
        $text = & $javac -version 2>&1 | Out-String
        if ($text -match 'javac\s+(\d+)') { return [int]$matches[1] }
    } catch {}
    return 0
}

function Get-Jdk($needed) {
    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += (Join-Path $env:JAVA_HOME 'bin\javac.exe') }
    $onPath = Get-Command javac.exe -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $repoRoot '.tools\jdk\bin\javac.exe')

    foreach ($javac in $candidates) {
        if ((Get-JavacFeature $javac) -ge $needed) { return (Split-Path (Split-Path $javac -Parent) -Parent) }
    }

    $jdk = Join-Path $repoRoot '.tools\jdk'
    Step "Downloading a Java $needed JDK to $jdk (about 190 MB, once)"
    $zip = Join-Path $env:TEMP ([IO.Path]::GetRandomFileName() + '.zip')
    $tmp = "$jdk.tmp"
    try {
        Invoke-WebRequest -Uri ($JdkUrlFormat -f $needed) -OutFile $zip -UseBasicParsing
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
        Expand-Archive -LiteralPath $zip -DestinationPath $tmp
        # The zip has one folder in it, jdk-25.0.x+y; its contents become .tools\jdk.
        $inner = @(Get-ChildItem -LiteralPath $tmp)
        $src = if ($inner.Count -eq 1 -and $inner[0].PSIsContainer) { $inner[0].FullName } else { $tmp }
        New-Item -ItemType Directory -Force -Path (Split-Path $jdk) | Out-Null
        if (Test-Path -LiteralPath $jdk) { Remove-Item -LiteralPath $jdk -Recurse -Force }
        Move-Item -LiteralPath $src -Destination $jdk
    } finally {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    }

    $feature = Get-JavacFeature (Join-Path $jdk 'bin\javac.exe')
    if ($feature -lt $needed) { throw "The JDK in $jdk is Java $feature; this build needs $needed or newer." }
    return $jdk
}

# --- The game ----------------------------------------------------------------------------------
if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not (Test-GameDir $GameDir)) {
    throw "Could not find Project Zomboid. Pass -GameDir ""D:\path\to\ProjectZomboid"" (the folder with projectzomboid.jar)."
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$gameJar = Join-Path $GameDir 'projectzomboid.jar'
Write-Host "    Game folder: $GameDir"

# --- The JDK -----------------------------------------------------------------------------------
$gameJava = Get-JarJavaFeature $gameJar
$needed   = [Math]::Max($FloorJava, $gameJava)
$jdk      = Get-Jdk $needed
$javac    = Join-Path $jdk 'bin\javac.exe'
$jar      = Join-Path $jdk 'bin\jar.exe'
Write-Host "    Game jar: Java $gameJava bytecode"
Write-Host "    JDK: $jdk (javac $(Get-JavacFeature $javac))"

# --- Compile -----------------------------------------------------------------------------------
if ($Clean -and (Test-Path -LiteralPath $out)) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $classes | Out-Null

$sources = Get-ChildItem -LiteralPath (Join-Path $here 'src\monitorcoop') -Filter *.java | ForEach-Object FullName
Step "Compiling $($sources.Count) files"
# --release keeps the mod to the Java the game runs, so a newer JDK on the machine cannot slip
# anything into the jar that the game's own runtime would not understand.
Invoke-Tool $javac (@('--release', $needed, '-Xlint:all,-serial,-this-escape', '-classpath', $gameJar, '-d', $classes) + $sources)

# --- Package -----------------------------------------------------------------------------------
# Premain-Class is what -javaagent looks for; Agent-Class lets the same jar attach to a running game.
$version = (Select-String -LiteralPath (Join-Path $here 'src\monitorcoop\Agent.java') -Pattern 'VERSION = "([^"]+)"').Matches[0].Groups[1].Value
$manifest = Join-Path $out 'manifest.mf'
@(
    "Manifest-Version: 1.0"
    "Implementation-Title: MonitorCoop"
    "Implementation-Version: $version"
    "Premain-Class: monitorcoop.Agent"
    "Agent-Class: monitorcoop.Agent"
    "Can-Retransform-Classes: false"
    "Can-Redefine-Classes: false"
    ""
) | Set-Content -LiteralPath $manifest -Encoding ascii

$built = Join-Path $out 'MonitorCoop.jar'
Step "Packaging $built (version $version)"
Invoke-Tool $jar @('--create', '--file', $built, '--manifest', $manifest, '-C', $classes, 'monitorcoop')

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -LiteralPath $built -Destination $dist -Force
# The launcher goes with it: the game's own exe cannot load a Java agent (see Play.ps1).
Copy-Item -LiteralPath (Join-Path $here 'Play.cmd'), (Join-Path $here 'Play.ps1') -Destination $dist -Force
Write-Host "Built -> $dist"

if ($Install) {
    & (Join-Path $gameRoot 'installer\Install.ps1') -GameDir $GameDir
    exit $LASTEXITCODE
}
