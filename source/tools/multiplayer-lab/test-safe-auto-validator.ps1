#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-safe-auto-' + [Guid]::NewGuid().ToString('N') + '.log')

function Write-PassFixture {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO enabled=true world_version=10')
    for ($turn = 1; $turn -le 3; $turn++) {
        $request = 100 + $turn
        $route = 200 + $turn
        $generation = 300 + $turn
        $world = 10 + ($turn * 3)
        $source = if ($turn -eq 1) { 'existing_result' } else { 'search_completion' }
        $lines.Add("[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO_ARMED source=$source generation=$generation turn=$turn world_version=$world")
        $lines.Add("[CombatSolver/MultiplayerSafeExecute] MP2B_END_TURN_REVALIDATED request_id=$request turn=$turn action_count=2 decision=Safe reason=safe_end_turn world_version=$world last_accepted_world_version=$world route_generation=$route")
        $lines.Add("[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=$request action_index=2 type=EndPlayerTurnAction turn=$turn card=- local_net_id=1000 custom_network_api_used=false")
        $lines.Add("[CombatSolver/MultiplayerSafeExecute] MP2B_SAFE_END_TURN_ACCEPTED request_id=$request turn=$turn action_count=2 route_generation=$route before_world_version=$world after_world_version=$($world + 1) next_local_turn=$($turn + 1) session_cleared=true authorization_cleared=true continuation_pending=false automatic_end_turn=true custom_network_api_used=false")
        $lines.Add("[CombatSolver/MultiplayerProbe] MP_REACTIVE_TURN_BOUNDARY previous=round=1;side=Player;local_net_id=1000;turn=$turn;phase=Play current=round=1;side=Player;local_net_id=1000;turn=$($turn + 1);phase=Play world_version=$($world + 2) observation_sequence=$turn fresh_probe=true fresh_capture=true")
        $lines.Add("[CombatSolver/MultiplayerSafeExecute] MP_REACTIVE_FRESH_SEARCH generation=$($generation + 1) route_generation=$($route + 1) world_version=$($world + 2) turn=$($turn + 1) reason=AutoTurnStart fresh_probe=true fresh_capture=true after_safe_end_turn=true previous_end_turn_request_id=$request previous_end_turn_turn=$turn cross_turn_reuse=false")
    }
    [IO.File]::WriteAllLines($fixture, $lines)
}

function Invoke-Validator {
    param([int]$ExpectedExit)
    & pwsh -NoLogo -NoProfile -File (Join-Path $scriptRoot 'validate-safe-auto-results.ps1') -LogPath $fixture -MinLocalTurns 3
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "Safe Auto validator returned $LASTEXITCODE, expected $ExpectedExit."
    }
}

try {
    Write-PassFixture
    Invoke-Validator 0

    $lines = [Collections.Generic.List[string]](Get-Content -LiteralPath $fixture)
    $lines.Insert(3, '[CombatSolver/Test] UI_ACTION action=deploy')
    [IO.File]::WriteAllLines($fixture, $lines)
    Invoke-Validator 1

    Write-PassFixture
    $lines = [Collections.Generic.List[string]](Get-Content -LiteralPath $fixture)
    $lines.Insert(3, '[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO_STOP reason=manual_local_state_change world_version=14')
    [IO.File]::WriteAllLines($fixture, $lines)
    Invoke-Validator 1

    [IO.File]::WriteAllLines($fixture, @(
        '[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO_ARMED source=search_completion generation=1 turn=1 world_version=1'
    ))
    Invoke-Validator 2

    Write-Output 'MULTIPLAYER_SAFE_AUTO_VALIDATOR_PASS'
} finally {
    if (Test-Path -LiteralPath $fixture -PathType Leaf) {
        Remove-Item -LiteralPath $fixture -Force
    }
}
