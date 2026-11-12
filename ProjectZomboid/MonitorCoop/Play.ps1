<#
    Starts Project Zomboid with MonitorCoop loaded.

    The mod is a Java agent, and an agent needs the Java runtime's own bin folder on the DLL search
    path: the game's ProjectZomboid64.exe starts the virtual machine from the game folder, where
    Java cannot find the library that does the instrumenting, so it refuses to start at all. Running
    the game through the runtime's own javaw.exe - which is how the game's ProjectZomboid64.bat
    starts it too - puts that folder on the path and the agent loads.

    Everything else is the game's: this reads ProjectZomboid64.json and uses the same main class,
    classpath and JVM arguments the game's own launcher would, including the ones it picks for this
    version of Windows. So a game update that changes any of them is picked up by itself, and no
    file in the game folder is modified by any of this.

    Arguments are passed on to the game, so -debug and friends work. Steam hands the launcher the
    original command when it is used through Steam's launch options; that part is dropped.
#>
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $GameArgs
)

$ErrorActionPreference = 'Stop'

function Fail($msg) {
    Write-Host "ERROR: $msg" -ForegroundColor Red
    Write-Host 'Run installer\Install.cmd again, or start the game normally for a game without the mod.'
    exit 1
}

$mod    = $PSScriptRoot
$game   = Split-Path $mod -Parent
$jar    = Join-Path $mod 'MonitorCoop.jar'
$javaw  = Join-Path $game 'jre64\bin\javaw.exe'
$config = Join-Path $game 'ProjectZomboid64.json'

if (-not (Test-Path -LiteralPath $jar))    { Fail "MonitorCoop.jar is not next to this script ($mod)." }
if (-not (Test-Path -LiteralPath $javaw))  { Fail "The game's Java runtime is not where it should be ($javaw)." }
if (-not (Test-Path -LiteralPath $config)) { Fail "The game's launcher settings are not there ($config)." }

try {
    $json = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
} catch {
    Fail "Could not read $config ($($_.Exception.Message))."
}
if (-not $json.mainClass -or -not $json.classpath -or -not $json.vmArgs) {
    Fail "$config does not have the mainClass, classpath and vmArgs the game's launcher needs."
}

# The game's own launcher adds the JVM arguments listed for the newest Windows version that is not
# newer than this one, so "10.0.17134" applies on Windows 10 2004 and later.
$vmArgs = @($json.vmArgs)
if ($json.windows) {
    $current = [Environment]::OSVersion.Version
    $bestVersion = $null
    $bestArgs = $null
    foreach ($entry in $json.windows.PSObject.Properties) {
        $version = $null
        try { $version = [version]$entry.Name } catch { continue }
        if ($version -le $current -and (-not $bestVersion -or $version -gt $bestVersion)) {
            $bestVersion = $version
            $bestArgs = $entry.Value.vmArgs
        }
    }
    if ($bestArgs) { $vmArgs += @($bestArgs) }
}

# Steam's launch options hand us the command Steam would have run; the game does not want its own
# launcher back as an argument.
$passthrough = @()
foreach ($argument in @($GameArgs)) {
    if ($argument -and $argument -notmatch '\.exe"?\s*$') { $passthrough += $argument }
}

if ($passthrough -contains '-nosteam') {
    $vmArgs = $vmArgs | ForEach-Object { if ($_ -eq '-Dzomboid.steam=1') { '-Dzomboid.steam=0' } else { $_ } }
}

$arguments = @()
$arguments += $vmArgs
$arguments += "-javaagent:$(Split-Path $mod -Leaf)/$(Split-Path $jar -Leaf)"
$arguments += "-Djava.class.path=$(@($json.classpath) -join ';')"
$arguments += ($json.mainClass -replace '/', '.')
$arguments += $passthrough

Write-Host 'Starting Project Zomboid with MonitorCoop...'
$process = Start-Process -FilePath $javaw -ArgumentList $arguments -WorkingDirectory $game -PassThru
if (-not $process) { Fail 'The game did not start.' }

# Waiting keeps Steam (and anything else watching this script) in step with the game: while this is
# running, the game is running.
$process.WaitForExit()
exit $process.ExitCode
