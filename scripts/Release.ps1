<#
.SYNOPSIS
    Builds one game's mods and packages them with that game's installer; optionally publishes
    the package as its own GitHub release.

.DESCRIPTION
    1. Clears <Game>\dist and runs every <Game>\<Mod>\build.cmd (each stages its output there).
    2. Stages dist + installer + READMEs in .release\<Game>-v<Version>\ and zips it.
    3. With -Publish: creates the tag <Game>-v<Version> on the current commit, a GitHub release
       whose notes are <Game>\README.md, and uploads the zip. Requires a clean working tree
       that is pushed, and uses the GitHub login saved in Git Credential Manager.

.EXAMPLE
    scripts\release.cmd MK11 1.0.1
.EXAMPLE
    scripts\release.cmd MK11 1.0.1 -Publish
#>
param(
    [Parameter(Mandatory = $true, Position = 0)] [string] $Game,
    [Parameter(Mandatory = $true, Position = 1)] [string] $Version,
    [switch] $Publish
)

$ErrorActionPreference = 'Stop'
$repo    = Split-Path $PSScriptRoot -Parent
$gameDir = Join-Path $repo $Game
$tag     = "$Game-v$Version"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Invoke-Git { $out = & git.exe -C $repo @args; if ($LASTEXITCODE -ne 0) { throw "git $args failed" }; $out }

if (-not (Test-Path -LiteralPath (Join-Path $gameDir 'installer\Install.ps1'))) {
    $games = Get-ChildItem -LiteralPath $repo -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'installer') } | ForEach-Object Name
    throw "Unknown game '$Game'. Games: $($games -join ', ')"
}
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.]+)?$') { throw "Version should look like 1.2.3, got '$Version'." }

# --- GitHub ------------------------------------------------------------------------------
$api = $null
if ($Publish) {
    if (Invoke-Git status --porcelain) { throw 'Working tree has uncommitted changes; commit (and push) before publishing.' }
    Invoke-Git fetch --quiet origin
    $head = Invoke-Git rev-parse HEAD
    if ($head -ne (Invoke-Git rev-parse '@{u}')) { throw 'HEAD is not pushed (or is behind origin); push/pull before publishing.' }

    $remote = Invoke-Git remote get-url origin
    if ($remote -notmatch 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?$') { throw "origin is not a GitHub repo: $remote" }
    $slug = "$($matches[1])/$($matches[2])"

    $env:GCM_INTERACTIVE = 'never'
    $cred  = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $token = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
    if (-not $token) { throw 'No GitHub login in Git Credential Manager (push to github.com once to store one).' }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $api = @{ Headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json' } }
    try {
        Invoke-RestMethod @api -Uri "https://api.github.com/repos/$slug/releases/tags/$tag" | Out-Null
        throw "Release $tag already exists."
    } catch [Net.WebException] {
        if ($_.Exception.Response.StatusCode -ne 404) { throw }
    }
}

# --- Build ---------------------------------------------------------------------------------
$dist = Join-Path $gameDir 'dist'
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }

$mods = Get-ChildItem -LiteralPath $gameDir -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'build.cmd') }
foreach ($mod in $mods) {
    Step "Building $($mod.Name)"
    & (Join-Path $mod.FullName 'build.cmd')
    if ($LASTEXITCODE -ne 0) { throw "$($mod.Name) build failed." }
}

# --- Package -------------------------------------------------------------------------------
Step "Packaging $tag"
$outDir = Join-Path $repo '.release'
$stage  = Join-Path $outDir $tag
$zip    = Join-Path $outDir "$tag.zip"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $zip)   { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item -Path (Join-Path $dist '*') -Destination $stage -Recurse
Copy-Item -Path (Join-Path $gameDir 'installer\*') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $gameDir 'README.md') -Destination $stage
foreach ($mod in $mods) {
    $readme = Join-Path $mod.FullName 'README.md'
    $inStage = Join-Path $stage "$($mod.Name)\README.md"
    if ((Test-Path -LiteralPath $readme) -and -not (Test-Path -LiteralPath $inStage)) {
        Copy-Item -LiteralPath $readme -Destination (Join-Path $stage "README-$($mod.Name).md")
    }
}

# Third-party files the installer can use offline, listed one URL per line in <Game>\bundle.txt.
# Downloads are cached in .tools\cache (some hosts are very slow).
$bundleList = Join-Path $gameDir 'bundle.txt'
if (Test-Path -LiteralPath $bundleList) {
    $cache = Join-Path $repo '.tools\cache'
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    foreach ($url in Get-Content -LiteralPath $bundleList | Where-Object { $_ -match '^\s*https?://' }) {
        $url  = $url.Trim()
        $name = [Uri]::UnescapeDataString(($url -split '/')[-1])
        $file = Join-Path $cache $name
        if (-not (Test-Path -LiteralPath $file)) {
            Write-Host "    downloading $url (cached in .tools\cache)"
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $url -OutFile "$file.part" -UseBasicParsing
            Move-Item -LiteralPath "$file.part" -Destination $file
        }
        Copy-Item -LiteralPath $file -Destination $stage
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { '    ' + $_.FullName.Substring($stage.Length + 1) }
Write-Host "    -> $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)"

if (-not $Publish) {
    Write-Host ''
    Write-Host "Package only. Re-run with -Publish to release it as $tag."
    exit 0
}

# --- Publish -------------------------------------------------------------------------------
Step "Publishing $tag"
$readme = Get-Content -LiteralPath (Join-Path $gameDir 'README.md') -Raw -Encoding UTF8
$title  = ([regex]::Match($readme, '^#\s+(.+)$', 'Multiline').Groups[1].Value).Trim()
if (-not $title) { $title = $Game }
# Relative links in the README point into the repo; make them absolute at this tag.
$notes = [regex]::Replace($readme, '\]\((?!https?:|#)([^)]+)\)', { param($m) "](https://github.com/$slug/tree/$tag/$Game/$($m.Groups[1].Value))" })
$notes = ($notes -replace '^#\s+.+\r?\n', '').Trim()

$body = @{
    tag_name         = $tag
    target_commitish = $head
    name             = "$title v$Version"
    body             = $notes
} | ConvertTo-Json
$release = Invoke-RestMethod @api -Method Post -Uri "https://api.github.com/repos/$slug/releases" `
    -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))

$uploadUrl = ($release.upload_url -replace '\{.*\}$', '') + '?name=' + [Uri]::EscapeDataString("$tag.zip")
Invoke-RestMethod @api -Method Post -Uri $uploadUrl -ContentType 'application/zip' -InFile $zip | Out-Null

Write-Host ''
Write-Host "Released: $($release.html_url)" -ForegroundColor Green
