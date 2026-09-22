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

    [ValidateSet('', 'probe', 'advisor', 'safe-execute', 'safe-execute-lab')]
    [string]$MultiplayerMode = '',

    [UInt64]$ClientId = 0,

    [string]$ConsoleFixturePath = '',

    [switch]$ForceSteamOff,

    [switch]$AllowSteam
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\headless-runtime.ps1')
. (Join-Path $PSScriptRoot 'multiplayer-common.ps1')
. (Join-Path $PSScriptRoot 'console-fixture-common.ps1')

$instance = Read-MultiplayerInstance $InstanceRoot
$profileName = [string]$instance.Profile.profile
if ($ForceSteamOff.IsPresent -and $AllowSteam.IsPresent) {
    throw 'ForceSteamOff and AllowSteam are mutually exclusive.'
}
$forceSteamOffEffective = -not $AllowSteam.IsPresent
$modRestartPolicy = if ($profileName -in @('HostCombatSolver', 'ClientRitsuOnly', 'ClientCombatSolver')) {
    'warmup_mod_load_then_restart_before_evidence'
} else {
    'none'
}
if ($Role -eq 'Host' -and $profileName -notin @('HostVanilla', 'HostCombatSolver')) {
    throw "Host launcher requires a Host profile, received $profileName."
}
if ($Role -eq 'Client' -and $profileName -in @('HostVanilla', 'HostCombatSolver')) {
    throw 'Client launcher cannot use a Host instance.'
}
if ($Role -eq 'Host' -and $ClientId -ne 0) {
    throw 'ClientId is only valid for a Client launcher.'
}
if (-not [string]::IsNullOrWhiteSpace($ConsoleFixturePath) -and
    ($Role -ne 'Client' -or $profileName -ne 'ClientCombatSolver')) {
    throw 'Console fixtures are restricted to an owned ClientCombatSolver lab instance.'
}

$existingState = Get-MultiplayerOwnedProcessState $instance
if ($existingState.state -eq 'Owned') {
    if (-not [string]::IsNullOrWhiteSpace($ConsoleFixturePath)) {
        throw 'The instance is already running; restart it to apply a console fixture.'
    }
    [ordered]@{
        status = 'ALREADY_RUNNING'
        role = $Role
        profile = $profileName
        runtimeRoot = $instance.Root
        processId = $existingState.process.Id
        logPath = [string]$existingState.marker.logPath
        forceSteamOff = if ($existingState.marker.ContainsKey('forceSteamOff')) { [bool]$existingState.marker.forceSteamOff } else { $null }
        modRestartPolicy = if ($existingState.marker.ContainsKey('modRestartPolicy')) { [string]$existingState.marker.modRestartPolicy } else { $null }
        runtimeEvidenceEligible = $false
    } | ConvertTo-Json -Depth 8
    exit 0
}
if ($existingState.state -eq 'Stale') {
    Remove-MultiplayerProcessMarker $instance
}

$ownedConsoleFixturePath = $null
if (-not [string]::IsNullOrWhiteSpace($ConsoleFixturePath)) {
    $fixture = Read-MultiplayerConsoleFixture -FixturePath $ConsoleFixturePath
    $fixtureRoot = Join-Path $instance.Root 'console-fixtures'
    Assert-MultiplayerPathWithin -Child $fixtureRoot -Parent $instance.Root
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    $ownedConsoleFixturePath = Join-Path $fixtureRoot 'active.json'
    Assert-MultiplayerPathWithin -Child $ownedConsoleFixturePath -Parent $instance.Root
    Copy-Item -LiteralPath $fixture.Path -Destination $ownedConsoleFixturePath -Force
}

New-Item -ItemType Directory -Path $instance.LogsRoot, $instance.RoamingRoot, $instance.LocalRoot -Force | Out-Null
$runId = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Role.ToLowerInvariant(), ([guid]::NewGuid().ToString('N').Substring(0, 8))
$logPath = Join-Path $instance.LogsRoot "$runId.log"
$resultPath = Join-Path $instance.LogsRoot "$runId.start.json"

$arguments = [Collections.Generic.List[string]]::new()
[void]$arguments.Add('--log-file')
[void]$arguments.Add($logPath)
if ($forceSteamOffEffective) {
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
if ($Role -eq 'Host' -and $profileName -eq 'HostCombatSolver') {
    $startInfo.Environment['COMBATSOLVER_MULTIPLAYER_LAB_HOST_CONSOLE'] = '1'
}
if ($null -ne $ownedConsoleFixturePath) {
    $startInfo.Environment['COMBATSOLVER_MULTIPLAYER_CONSOLE_FIXTURE'] = $ownedConsoleFixturePath
}
if ($Role -eq 'Client') {
    # Probe evidence is deliberately Lab-only; ordinary desktop launches stay quiet.
    $startInfo.Environment['COMBATSOLVER_MULTIPLAYER_PROBE_EVIDENCE'] = '1'
}
if (-not [string]::IsNullOrWhiteSpace($MultiplayerMode)) {
    if ($MultiplayerMode -in @('safe-execute', 'safe-execute-lab') -and
        ($Role -ne 'Client' -or $profileName -ne 'ClientCombatSolver')) {
        throw 'safe-execute modes are restricted to an owned ClientCombatSolver lab instance.'
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
        consoleFixturePath = $ownedConsoleFixturePath
        forceSteamOff = $forceSteamOffEffective
        modRestartPolicy = $modRestartPolicy
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
        consoleFixturePath = $ownedConsoleFixturePath
        forceSteamOff = $forceSteamOffEffective
        modRestartPolicy = $modRestartPolicy
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
