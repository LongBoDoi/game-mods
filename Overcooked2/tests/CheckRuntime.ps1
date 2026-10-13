param(
    [string]$GameDir = 'E:\Steam\steamapps\common\Overcooked! 2',
    [string]$PluginPath = (Join-Path $PSScriptRoot '..\dist\LobbyTools.dll')
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.RemoveSearchDirectory('.')
$resolver.RemoveSearchDirectory('bin')
$resolver.AddSearchDirectory((Join-Path $GameDir 'Overcooked2_Data\Managed'))
$resolver.AddSearchDirectory((Join-Path $GameDir 'BepInEx\core'))
$reader = New-Object Mono.Cecil.ReaderParameters
$reader.AssemblyResolver = $resolver
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $PluginPath).Path, $reader)
try {
    $failed = @()
    $checked = 0
    $references = @($assembly.MainModule.GetTypeReferences()) + @($assembly.MainModule.GetMemberReferences())
    foreach ($reference in $references) {
        if ($reference -isnot [Mono.Cecil.TypeReference] -and
            $reference -isnot [Mono.Cecil.MethodReference] -and
            $reference -isnot [Mono.Cecil.FieldReference]) { continue }
        try {
            if (-not $reference.Resolve()) { $failed += "Missing runtime reference: $($reference.FullName)" }
            else { $checked++ }
        } catch { $failed += "Cannot resolve $($reference.FullName): $($_.Exception.Message)" }
    }
    # Protected methods used through reflection do not appear as IL member references.
    $game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Overcooked2_Data\Managed\Assembly-CSharp.dll'))
    try {
        $flow = $game.MainModule.GetType('ClientLobbyFlowController')
        foreach ($name in 'TryJoinGame', 'HostGame') {
            if (-not ($flow.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })) {
                $failed += "Missing force-host method: ClientLobbyFlowController.$name()"
            } else { $checked++ }
        }
        $telemetry = @{
            'ConnectionModeSwitcher' = @('m_LocalServer')
            'Team17.Online.Multiplayer.Server' = @('m_RemoteClientConnections')
            'Team17.Online.Multiplayer.Connection.RemoteConnection' = @('m_ReliableLatencyMeasure', 'm_UnreliableLatencyMeasure')
            'Team17.Online.Multiplayer.Connection.LatencyMeasure' = @('m_OneWayLatencyHistory', 'm_fTimeTimeReceived')
        }
        foreach ($typeName in $telemetry.Keys) {
            $type = $game.MainModule.GetType($typeName)
            foreach ($name in $telemetry[$typeName]) {
                if (-not ($type.Fields | Where-Object Name -eq $name)) {
                    $failed += "Missing connection telemetry field: $typeName.$name"
                } else { $checked++ }
            }
        }
        $baseConnection = $game.MainModule.GetType('Team17.Online.Multiplayer.Connection.BaseConnection')
        if (-not ($baseConnection.Methods | Where-Object { $_.Name -eq 'GetLatencyTestPaused' -and $_.Parameters.Count -eq 0 })) {
            $failed += 'Missing continuous-ping patch target: BaseConnection.GetLatencyTestPaused()'
        } else { $checked++ }
    } finally { $game.Dispose() }
    if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
    Write-Host "PASS: $checked type/member references resolve against the shipped game runtime."
} finally {
    $assembly.Dispose()
    $resolver.Dispose()
}
