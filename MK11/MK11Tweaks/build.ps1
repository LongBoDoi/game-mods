<#
    Builds MK11Tweaks.asi with llvm-mingw (no Visual Studio needed) and stages it, together with the
    Ultimate ASI Loader (dinput8.dll), in ..\dist\ for scripts\Release.ps1.

    -Install then runs ..\installer\Install.ps1 on the result.
    -Clean   rebuilds everything instead of only what changed.

    Toolchain: $env:LLVM_MINGW, else <repo>\.tools\llvm-mingw, else downloaded there (~180 MB).
    Dependencies (deps\): downloaded at the pinned versions below when missing.
#>
param([switch]$Install, [switch]$Clean)

$ErrorActionPreference = 'Stop'

$LlvmMingwUrl = 'https://github.com/mstorsjo/llvm-mingw/releases/download/20260922/llvm-mingw-20260922-ucrt-x86_64.zip'
$Deps = @(
    @{ Name = 'imgui';   Url = 'https://github.com/ocornut/imgui/archive/refs/tags/v1.92.9b.zip' },
    @{ Name = 'minhook'; Url = 'https://github.com/TsudaKageyu/minhook/archive/8af6b4acae5a9388fd742b56fa79ece89d96f823.zip' },
    @{ Name = 'asi';     Url = 'https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/download/v9.7.4/Ultimate-ASI-Loader-NoPDB_x64.zip'; Flat = $true }
)

# This folder may be reached through the old _modding\MK11Tweaks junction; work from the real one.
$here = $PSScriptRoot
$item = Get-Item -LiteralPath $here
if ($item.LinkType -eq 'Junction') { $here = @($item.Target)[0] }
$gameRoot = Split-Path $here -Parent            # <repo>\MK11
$repoRoot = Split-Path $gameRoot -Parent
$dist     = Join-Path $gameRoot 'dist'
$depsDir  = Join-Path $here 'deps'
$out      = Join-Path $here 'build'
$objDir   = Join-Path $out 'obj'

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'   # the progress bar makes 5.1's downloads crawl

# Downloads a zip and extracts it to $dest. GitHub source archives have one top-level folder;
# unless -Flat, its contents become $dest.
function Get-Zip($url, $dest, [switch]$Flat) {
    Write-Host "Downloading $url"
    $zip = Join-Path $env:TEMP ([IO.Path]::GetRandomFileName() + '.zip')
    $tmp = "$dest.tmp"
    try {
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
        Expand-Archive -LiteralPath $zip -DestinationPath $tmp
        $inner = @(Get-ChildItem -LiteralPath $tmp)
        $src = if (-not $Flat -and $inner.Count -eq 1 -and $inner[0].PSIsContainer) { $inner[0].FullName } else { $tmp }
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Move-Item -LiteralPath $src -Destination $dest
    } finally {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }
}

# --- Toolchain and dependencies ------------------------------------------------
$tc = $env:LLVM_MINGW
if (-not $tc) { $tc = Join-Path $repoRoot '.tools\llvm-mingw' }
if (-not (Test-Path -LiteralPath (Join-Path $tc 'bin\x86_64-w64-mingw32-clang++.exe'))) {
    Get-Zip $LlvmMingwUrl $tc
}
$cc  = Join-Path $tc 'bin\x86_64-w64-mingw32-clang.exe'
$cxx = Join-Path $tc 'bin\x86_64-w64-mingw32-clang++.exe'

foreach ($d in $Deps) {
    $dir = Join-Path $depsDir $d.Name
    if (-not (Test-Path -LiteralPath $dir)) { Get-Zip $d.Url $dir -Flat:([bool]$d.Flat) }
}

# --- Compile -----------------------------------------------------------------------
if ($Clean -and (Test-Path -LiteralPath $out)) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $objDir | Out-Null

$inc    = @("-I$depsDir\imgui", "-I$depsDir\imgui\backends", "-I$depsDir\minhook\include")
$cflags = @('-O2', '-DNDEBUG', '-DWIN32_LEAN_AND_MEAN', '-DNOMINMAX', '-D_WIN32_WINNT=0x0A00')
$header = Get-Item -LiteralPath (Join-Path $here 'src\common.h')

$sources = @()
$sources += 'buffer', 'hook', 'trampoline' | ForEach-Object { @{ Path = "$depsDir\minhook\src\$_.c"; C = $true } }
$sources += @{ Path = "$depsDir\minhook\src\hde\hde64.c"; C = $true }
$sources += 'imgui', 'imgui_draw', 'imgui_tables', 'imgui_widgets' | ForEach-Object { @{ Path = "$depsDir\imgui\$_.cpp" } }
$sources += 'imgui_impl_win32', 'imgui_impl_dx11' | ForEach-Object { @{ Path = "$depsDir\imgui\backends\$_.cpp" } }
$sources += Get-ChildItem -LiteralPath (Join-Path $here 'src') -Filter *.cpp | ForEach-Object { @{ Path = $_.FullName } }

Write-Host 'Compiling'
$objs = @()
foreach ($s in $sources) {
    $src = Get-Item -LiteralPath $s.Path
    $obj = Join-Path $objDir ($src.BaseName + '.o')
    $objs += $obj
    if (Test-Path -LiteralPath $obj) {
        $objTime = (Get-Item -LiteralPath $obj).LastWriteTimeUtc
        if ($objTime -gt $src.LastWriteTimeUtc -and $objTime -gt $header.LastWriteTimeUtc) { continue }
    }
    Write-Host "  $($src.Name)"
    if ($s.C) {
        & $cc @cflags @inc -c $src.FullName -o $obj
    } else {
        & $cxx '-std=c++17' @cflags @inc -Wall -Wno-unused-function -c $src.FullName -o $obj
    }
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $($src.Name)" }
}

$asi = Join-Path $out 'MK11Tweaks.asi'
Write-Host "Linking $asi"
& $cxx -shared -static -s -o $asi @objs -ld3d11 -ldxgi -ld3dcompiler -ldwmapi -lgdi32 -limm32 -luser32
if ($LASTEXITCODE -ne 0) { throw 'link failed' }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -LiteralPath $asi, (Join-Path $depsDir 'asi\dinput8.dll') -Destination $dist -Force
Write-Host "Built -> $dist"

if ($Install) {
    & (Join-Path $gameRoot 'installer\Install.ps1')
    exit $LASTEXITCODE
}
