<#
    Background helper for the Mod Updater plugin. The plugin starts it hidden when the game
    launches (and again from the Check button in MOD Settings); it never asks anything itself.
    The plugin asks the player, in a dialog, once a new version is downloaded.

    1. Asks GitHub for the newest Overcooked2-v<version> release of this repo.
    2. If it is newer than the package last installed (BepInEx\config\com.manhl.overcooked2.package.txt),
       downloads its zip and unpacks it into BepInEx\cache\ModUpdater\staged.
    3. Waits for the game (-WaitPid) to close - a loaded DLL cannot be replaced - and then runs the
       release's own installer with -Update, but only if the player chose Update in the dialog:
       the plugin then loaded the new versions into the running game and left install.flag
       behind. Otherwise the download stays staged and the player is asked again next launch.

    Progress goes to BepInEx\cache\ModUpdater\state.txt as key=value lines, which the plugin
    shows in MOD Settings; everything else goes to update.log next to it. Messages are codes,
    the plugin turns them into text.
#>
param(
    [Parameter(Mandatory = $true)] [string] $GameDir,
    [int] $WaitPid = 0,
    [switch] $NoCheck
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$Repo        = 'LongBoDoi/game-mods'
$Game        = 'Overcooked2'
$GameDir     = $GameDir.TrimEnd('\')
$work        = Join-Path $GameDir 'BepInEx\cache\ModUpdater'
$staged      = Join-Path $work 'staged'
$stateFile   = Join-Path $work 'state.txt'
$logFile     = Join-Path $work 'update.log'
$installFlag = Join-Path $work 'install.flag'
$packageFile = Join-Path $GameDir 'BepInEx\config\com.manhl.overcooked2.package.txt'

New-Item -ItemType Directory -Force -Path $work | Out-Null

function Write-Log($msg) {
    try { Add-Content -LiteralPath $logFile -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg) -Encoding UTF8 } catch {}
}

# Written whole and swapped in, so the plugin never reads half a file.
function Set-State([string]$state, [hashtable]$extra = @{}) {
    $lines = @("state=$state", "installed=$script:installed", "time=$((Get-Date).ToString('o'))")
    foreach ($key in $extra.Keys) { $lines += "$key=$(("$($extra[$key])" -replace '[\r\n]+', ' ').Trim())" }
    $temp = "$stateFile.tmp"
    [IO.File]::WriteAllLines($temp, $lines, (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $temp -Destination $stateFile -Force
    Write-Log "state=$state $(($extra.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')"
}

function Get-StagedVersion {
    $file = Join-Path $staged 'version.txt'
    if ((Test-Path -LiteralPath (Join-Path $staged 'Install.ps1')) -and (Test-Path -LiteralPath $file)) {
        return (Get-Content -LiteralPath $file -TotalCount 1).Trim()
    }
    return $null
}

function Test-Newer([string]$candidate, [string]$current) {
    if (-not $candidate) { return $false }
    if (-not $current -or $current -eq 'unknown') { return $true }
    try { return ([version]$candidate -gt [version]$current) } catch { return $candidate -ne $current }
}

# One helper at a time per machine: a second start (the Check button while another is waiting
# for the game to close) leaves the work to the one already running.
$mutex = New-Object Threading.Mutex($false, 'Local\com.manhl.overcooked2.modupdater')
if (-not $mutex.WaitOne(0)) { Write-Log 'Another helper is already running; leaving it to that one.'; exit 0 }

try {
    $script:installed = 'unknown'
    if (Test-Path -LiteralPath $packageFile) {
        $value = (Get-Content -LiteralPath $packageFile -TotalCount 1).Trim()
        if ($value -match '^\d+(\.\d+){1,3}$') { $script:installed = $value }
    }

    # --- 1. What is the newest release? -------------------------------------------------------
    $latest = $null
    if (-not $NoCheck) {
        Set-State 'checking'
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $releases = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/$Repo/releases?per_page=50" `
                -Headers @{ 'User-Agent' = 'OC2-ModUpdater'; Accept = 'application/vnd.github+json' } -TimeoutSec 30
            foreach ($release in $releases) {
                if ($release.draft -or $release.prerelease) { continue }
                if ($release.tag_name -notmatch "^$Game-v(\d+\.\d+\.\d+)$") { continue }
                $version = $matches[1]
                $asset = $release.assets | Where-Object { $_.name -like "$Game-v*.zip" } | Select-Object -First 1
                if (-not $asset) { continue }
                if (-not $latest -or [version]$version -gt [version]$latest.Version) {
                    $latest = [pscustomobject]@{ Version = $version; Url = $asset.browser_download_url }
                }
            }
        } catch {
            Set-State 'error' @{ error = 'offline'; detail = $_.Exception.Message }
            # A package downloaded on an earlier run can still be installed below.
        }
        if (-not $latest -and -not (Get-StagedVersion)) {
            if ((Get-Content -LiteralPath $stateFile -TotalCount 1) -ne 'state=error') { Set-State 'error' @{ error = 'norelease' } }
            exit 0
        }
    }

    # --- 2. Download it -------------------------------------------------------------------------
    if ($latest) {
        if (-not (Test-Newer $latest.Version $script:installed)) {
            if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Recurse -Force }
            Remove-Item -LiteralPath $installFlag -Force -ErrorAction SilentlyContinue
            Set-State 'uptodate' @{ latest = $latest.Version }
            exit 0
        }
        if ((Get-StagedVersion) -ne $latest.Version) {
            Set-State 'downloading' @{ latest = $latest.Version }
            $zip = Join-Path $work 'download.zip'
            $unpack = Join-Path $work 'staged.tmp'
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $latest.Url -OutFile $zip -TimeoutSec 300 `
                    -Headers @{ 'User-Agent' = 'OC2-ModUpdater' }
                if (Test-Path -LiteralPath $unpack) { Remove-Item -LiteralPath $unpack -Recurse -Force }
                Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force
                if (-not (Test-Path -LiteralPath (Join-Path $unpack 'Install.ps1'))) { throw 'The release zip has no Install.ps1.' }
                # Older packages carry no version.txt; the tag says the same thing.
                Set-Content -LiteralPath (Join-Path $unpack 'version.txt') -Value $latest.Version -Encoding ASCII
                if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Recurse -Force }
                Move-Item -LiteralPath $unpack -Destination $staged
            } catch {
                Set-State 'error' @{ error = 'download'; latest = $latest.Version; detail = $_.Exception.Message }
                exit 1
            } finally {
                Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $ready = Get-StagedVersion
    if (-not $ready -or -not (Test-Newer $ready $script:installed)) { exit 0 }
    Set-State 'ready' @{ latest = $ready }

    # --- 3. Install it once the game is closed ------------------------------------------------------
    if ($WaitPid -gt 0) {
        $game = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
        if ($game) {
            Write-Log "Waiting for the game (pid $WaitPid) to close."
            $game.WaitForExit()
            Start-Sleep -Seconds 2   # let Windows release the DLLs
        }
    }

    if (-not (Test-Path -LiteralPath $installFlag)) {
        Write-Log "Version $ready stays staged: the player did not choose to update."
        exit 0
    }

    Set-State 'applying' @{ latest = $ready }
    $installer = Join-Path $staged 'Install.ps1'
    $powershell = Join-Path $PSHOME 'powershell.exe'
    # Windows PowerShell turns a child's stderr lines into errors, which 'Stop' would make fatal
    # here; the exit code is what says whether the install worked.
    $ErrorActionPreference = 'Continue'
    $output = @(& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -GameDir $GameDir -Update 2>&1 |
        ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $output | ForEach-Object { Write-Log "  | $_" }

    if ($code -eq 0) {
        $script:installed = $ready
        # The installer records this too; written here as well so an older installer cannot leave
        # the old number behind and have the same update offered forever.
        Set-Content -LiteralPath $packageFile -Value $ready -Encoding ASCII
        Remove-Item -LiteralPath $staged -Recurse -Force -ErrorAction SilentlyContinue
        Set-State 'applied' @{ latest = $ready }
    } else {
        $reason = if ($output -match 'is running') { 'running' } elseif ($output -match 'Cannot write') { 'denied' } else { 'install' }
        Set-State 'error' @{ error = $reason; latest = $ready; detail = "installer exit code $code" }
    }

    if ($code -eq 0) { Remove-Item -LiteralPath $installFlag -Force -ErrorAction SilentlyContinue }
} catch {
    Set-State 'error' @{ error = 'unexpected'; detail = $_.Exception.Message }
    exit 1
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
