#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Host', 'Client')]
    [string]$Role,

    [Parameter(Mandatory = $true)]
    [string]$InstanceRoot,

    [ValidateSet('', 'host', 'join')]
    [string]$FastMpMode = '',

    [ValidateSet('', 'probe', 'advisor', 'safe-execute-lab')]
    [string]$MultiplayerMode = '',

    [UInt64]$ClientId = 0,

    [switch]$ForceSteamOff
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\headless-runtime.ps1')
. (Join-Path $PSScriptRoot 'multiplayer-common.ps1')

$instance = Read-MultiplayerInstance $InstanceRoot
$profileName = [string]$instance.Profile.profile
if ($Role -eq 'Host' -and $profileName -ne 'HostVanilla') {
    throw "Host launcher requires HostVanilla, received $profileName."
}
if ($Role -eq 'Client' -and $profileName -eq 'HostVanilla') {
    throw 'Client launcher cannot use a HostVanilla instance.'
}
if ($Role -eq 'Host' -and $ClientId -ne 0) {
    throw 'ClientId is only valid for a Client launcher.'
}

$existingState = Get-MultiplayerOwnedProcessState $instance
if ($existingState.state -eq 'Owned') {
    [ordered]@{
        status = 'ALREADY_RUNNING'
        role = $Role
        profile = $profileName
        runtimeRoot = $instance.Root
        processId = $existingState.process.Id
        logPath = [string]$existingState.marker.logPath
        runtimeEvidenceEligible = $false
    } | ConvertTo-Json -Depth 8
    exit 0
}
if ($existingState.state -eq 'Stale') {
    Remove-MultiplayerProcessMarker $instance
}

New-Item -ItemType Directory -Path $instance.LogsRoot, $instance.RoamingRoot, $instance.LocalRoot -Force | Out-Null
$runId = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Role.ToLowerInvariant(), ([guid]::NewGuid().ToString('N').Substring(0, 8))
$logPath = Join-Path $instance.LogsRoot "$runId.log"
$resultPath = Join-Path $instance.LogsRoot "$runId.start.json"

$arguments = [Collections.Generic.List[string]]::new()
[void]$arguments.Add('--log-file')
[void]$arguments.Add($logPath)
if ($ForceSteamOff.IsPresent) {
    [void]$arguments.Add('--force-steam=off')
}
if (-not [string]::IsNullOrWhiteSpace($FastMpMode)) {
    # This is intentionally opt-in. The current 0.107.1 command-line entry
    # point is not itself MP-0 evidence and the launcher never auto-joins.
    [void]$arguments.Add("--fastmp=$FastMpMode")
}
if ($ClientId -ne 0) {
    # FastMpJoin defaults to 1000; local multi-client runs need unique IDs.
    [void]$arguments.Add("--clientId=$ClientId")
}

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $instance.GameExe
$startInfo.WorkingDirectory = $instance.GameRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $false
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
$startInfo.Environment['APPDATA'] = $instance.RoamingRoot
$startInfo.Environment['LOCALAPPDATA'] = $instance.LocalRoot
$startInfo.Environment['COMBATSOLVER_MULTIPLAYER_INSTANCE'] = $instance.Root
if ($Role -eq 'Client') {
    # Probe evidence is deliberately Lab-only; ordinary desktop launches stay quiet.
    $startInfo.Environment['COMBATSOLVER_MULTIPLAYER_PROBE_EVIDENCE'] = '1'
}
if (-not [string]::IsNullOrWhiteSpace($MultiplayerMode)) {
    if ($MultiplayerMode -eq 'safe-execute-lab' -and
        ($Role -ne 'Client' -or $profileName -ne 'ClientCombatSolver')) {
        throw 'safe-execute-lab is restricted to an owned ClientCombatSolver lab instance.'
    }
    $startInfo.Environment['COMBATSOLVER_MULTIPLAYER_MODE'] = $MultiplayerMode
}
foreach ($argument in $arguments) {
    [void]$startInfo.ArgumentList.Add($argument)
}

$process = [Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$startedUtc = [DateTimeOffset]::UtcNow
try {
    if (-not $process.Start()) {
        throw 'The multiplayer game process did not start.'
    }
    Start-Sleep -Milliseconds 750
    $identity = Get-MultiplayerProcessIdentity $process $instance.GameExe
    $marker = [ordered]@{
        schemaVersion = 1
        role = $Role
        profile = $profileName
        runtimeRoot = $instance.Root
        gameRoot = $instance.GameRoot
        executable = $identity.executable
        pid = $identity.pid
        processStartTimeUtc = $identity.processStartTimeUtc
        startedUtc = $startedUtc.ToString('O')
        logPath = $logPath
        fastMpMode = if ([string]::IsNullOrWhiteSpace($FastMpMode)) { $null } else { $FastMpMode }
        multiplayerMode = if ([string]::IsNullOrWhiteSpace($MultiplayerMode)) { $null } else { $MultiplayerMode }
        clientId = if ($ClientId -eq 0) { $null } else { $ClientId }
        forceSteamOff = $ForceSteamOff.IsPresent
        runtimeEvidenceEligible = $false
    }
    Write-HeadlessJson $instance.ProcessMarkerPath $marker
    $result = [ordered]@{
        status = 'STARTED'
        role = $Role
        profile = $profileName
        runtimeRoot = $instance.Root
        processId = $identity.pid
        logPath = $logPath
        processMarkerPath = $instance.ProcessMarkerPath
        multiplayerMode = if ([string]::IsNullOrWhiteSpace($MultiplayerMode)) { $null } else { $MultiplayerMode }
        clientId = if ($ClientId -eq 0) { $null } else { $ClientId }
        runtimeEvidenceEligible = $false
    }
    Write-HeadlessJson $resultPath $result
    $result | ConvertTo-Json -Depth 8
}
catch {
    $startError = $_.Exception
    try {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            [void]$process.WaitForExit(5000)
        }
    }
    catch {
        throw "Multiplayer startup failed: $($startError.Message); owned-process cleanup failed: $($_.Exception.Message)"
    }
    throw $startError
}
