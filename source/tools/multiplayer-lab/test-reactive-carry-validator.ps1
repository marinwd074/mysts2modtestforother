#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-reactive-carry-' + [Guid]::NewGuid().ToString('N') + '.log')

function Write-Fixture {
    param([ValidateSet('A', 'B', 'C')][string]$Smoke)

    $lines = [Collections.Generic.List[string]]::new()
    $turnCount = if ($Smoke -eq 'C') { 3 } else { 1 }
    for ($turn = 1; $turn -le $turnCount; $turn++) {
        $request = 100 + $turn
        $route = 200 + $turn
        $search = 300 + $turn
        $worldBefore = 10 + ($turn * 2)
        $worldAfter = $worldBefore + 1
        $lines.Add("[CombatSolver/MultiplayerSafeExecute] MP2B_END_TURN_REVALIDATED request_id=$request turn=$turn action_count=2 decision=Safe reason=safe_end_turn world_version=$worldAfter last_accepted_world_version=$worldAfter route_generation=$route")
        $lines.Add("[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=$request action_index=2 type=EndPlayerTurnAction turn=$turn card=- local_net_id=1000 custom_network_api_used=false")
        $lines.Add("[CombatSolver/MultiplayerSafeExecute] MP2B_SAFE_END_TURN_ACCEPTED request_id=$request turn=$turn action_count=2 route_generation=$route before_world_version=$worldAfter after_world_version=$($worldAfter + 1) next_local_turn=$($turn + 1) session_cleared=true authorization_cleared=true automatic_end_turn=true custom_network_api_used=false")
        if ($Smoke -eq 'B') {
            $lines.Add("[CombatSolver/MultiplayerProbe] MP_REACTIVE_WORLD_DELTA world_version=$($worldAfter + 2) reason=main_thread_monitor remote_public_changed=true local_private_changed=false fresh_probe=true")
        }
        $lines.Add("[CombatSolver/MultiplayerProbe] MP_REACTIVE_TURN_BOUNDARY previous=round=1;side=Player;local_net_id=1000;turn=$turn;phase=Play current=round=1;side=Player;local_net_id=1000;turn=$($turn + 1);phase=Play world_version=$($worldAfter + 2) observation_sequence=$turn fresh_probe=true fresh_capture=true")
        $lines.Add("[CombatSolver/MultiplayerSafeExecute] MP_REACTIVE_FRESH_SEARCH generation=$search route_generation=$($route + 1) world_version=$($worldAfter + 2) turn=$($turn + 1) reason=AutoTurnStart fresh_probe=true fresh_capture=true after_safe_end_turn=true previous_end_turn_request_id=$request previous_end_turn_turn=$turn cross_turn_reuse=false")
    }
    [IO.File]::WriteAllLines($fixture, $lines)
}

function Write-ReuseFixture {
    $request = 201
    $turn = 1
    $route = 401
    $world = 20
    [IO.File]::WriteAllLines($fixture, @(
        "[CombatSolver/MultiplayerSafeExecute] MP2B_END_TURN_REVALIDATED request_id=$request turn=$turn action_count=2 decision=Safe reason=safe_end_turn world_version=$world last_accepted_world_version=$world route_generation=$route",
        "[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=$request action_index=2 type=EndPlayerTurnAction turn=$turn card=- local_net_id=1000 custom_network_api_used=false",
        "[CombatSolver/MultiplayerSafeExecute] MP2B_SAFE_END_TURN_ACCEPTED request_id=$request turn=$turn action_count=2 route_generation=$route before_world_version=$world after_world_version=$($world + 1) next_local_turn=2 session_cleared=true authorization_cleared=true automatic_end_turn=true custom_network_api_used=false",
        "[CombatSolver/MultiplayerProbe] MP_REACTIVE_TURN_BOUNDARY previous=round=1;side=Player;local_net_id=1000;turn=1;phase=Play current=round=2;side=Player;local_net_id=1000;turn=2;phase=Play world_version=$($world + 2) observation_sequence=1 fresh_probe=true fresh_capture=true",
        "[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REUSED turn=2 route_identity=route-reuse source_world_version=20 minimum_world_version=21 actual_world_version=22 local_state_exact=true reason=exact"
    ))
}

try {
    foreach ($smoke in @('A', 'B', 'C')) {
        Write-Fixture $smoke
        $arguments = @(
            '-NoLogo',
            '-NoProfile',
            '-File',
            (Join-Path $scriptRoot 'validate-reactive-carry-results.ps1'),
            '-LogPath',
            $fixture,
            '-Smoke',
            $smoke
        )
        if ($smoke -ne 'C') {
            $arguments += @('-RequestId', '101')
        }
        & pwsh @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Reactive Carry validator fixture failed for Smoke $smoke."
        }
    }
    Write-ReuseFixture
    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-File',
        (Join-Path $scriptRoot 'validate-reactive-carry-results.ps1'),
        '-LogPath',
        $fixture,
        '-Smoke',
        'A',
        '-RequestId',
        '201'
    )
    & pwsh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Reactive Carry validator rejected an exact Joint continuation reuse fixture.'
    }

    Write-Output 'MULTIPLAYER_REACTIVE_CARRY_VALIDATOR_PASS'
} finally {
    if (Test-Path -LiteralPath $fixture -PathType Leaf) {
        Remove-Item -LiteralPath $fixture -Force
    }
}
