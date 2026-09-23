#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$validator = Join-Path $scriptRoot 'validate-u3-scenario-results.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-u3-scenario-' + [Guid]::NewGuid().ToString('N') + '.log')

function Invoke-Validator {
    param(
        [Parameter(Mandatory)][ValidateSet('Matrix', 'Timeout', 'All')][string]$Phase,
        [Parameter(Mandatory)][int]$ExpectedExit,
        [Parameter(Mandatory)][string]$ExpectedToken
    )

    $output = @(& pwsh -NoLogo -NoProfile -File $validator -LogPath $fixture -Phase $Phase)
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "U3 scenario validator returned $LASTEXITCODE for $Phase, expected $ExpectedExit. Output: $($output -join ' | ')"
    }
    if (($output -join [Environment]::NewLine) -notmatch [regex]::Escape($ExpectedToken)) {
        throw "U3 scenario validator output did not contain '$ExpectedToken'. Output: $($output -join ' | ')"
    }
}

$completeMatrix = @(
    '[CombatSolver/Multiplayer] MP_SCENARIO_BUDGET total_node_budget=5000 main_node_budget=4488 reserved=512 decisions=2 scenarios_per_decision=4 decision_budget=128 replay_expanded=200 replay_transitions=240',
    '[CombatSolver/Multiplayer] MP_SCENARIO_COVERAGE baseline_rank=1 complete=true statuses=aggressive:Completed,defensive:Completed,conserve:Terminal,no_action:Completed scenario_count=4 replay_expanded=100',
    '[CombatSolver/Multiplayer] MP_SCENARIO_COVERAGE baseline_rank=2 complete=true statuses=aggressive:Completed,defensive:Terminal,conserve:Completed,no_action:Completed scenario_count=4 replay_expanded=100',
    '[CombatSolver/Multiplayer] MP_SCENARIO_RERANK enabled=true complete=true statuses=aggressive:Completed,defensive:Completed,conserve:Terminal,no_action:Completed scenario_count=4 all_alive=true guaranteed_victory=false worst_loss=0.2000 mean_loss=0.1000 worst_player_loss=0.1000 worst_enemy_durability=0.3000',
    '[CombatSolver/U0] FINAL_SELECTION route_policy=MultiplayerLocalCrossTurn state=1:2 turn=1 action_count=2 complete_victory=false scenario_rerank=true chance_rerank=false actions=PlayCard:STRIKE,EndTurn'
)

$unknownFallback = @(
    '[CombatSolver/Multiplayer] MP_SCENARIO_BUDGET total_node_budget=5000 main_node_budget=4488 reserved=512 decisions=2 scenarios_per_decision=4 decision_budget=128 replay_expanded=170 replay_transitions=205',
    '[CombatSolver/Multiplayer] MP_SCENARIO_COVERAGE baseline_rank=1 complete=true statuses=aggressive:Completed,defensive:Completed,conserve:Completed,no_action:Terminal scenario_count=4 replay_expanded=100',
    '[CombatSolver/Multiplayer] MP_SCENARIO_COVERAGE baseline_rank=2 complete=false statuses=aggressive:Completed,defensive:Unknown,conserve:Completed,no_action:Completed scenario_count=3 replay_expanded=70',
    '[CombatSolver/Multiplayer] MP_SCENARIO_RERANK enabled=false reason=shared_scenario_coverage_incomplete_or_single_current_decision',
    '[CombatSolver/U0] FINAL_SELECTION route_policy=MultiplayerLocalCrossTurn state=3:4 turn=1 action_count=2 complete_victory=false scenario_rerank=false chance_rerank=false actions=PlayCard:DEFEND,EndTurn'
)

$timeoutFallback = @(
    '[CombatSolver/Multiplayer] MP_SCENARIO_RERANK enabled=false reason=reevaluation_budget_unavailable',
    '[CombatSolver/U0] FINAL_SELECTION route_policy=MultiplayerLocalCrossTurn state=5:6 turn=1 action_count=2 complete_victory=false scenario_rerank=false chance_rerank=false actions=PlayCard:BASH,EndTurn'
)

try {
    [IO.File]::WriteAllLines($fixture, $completeMatrix)
    Invoke-Validator -Phase Matrix -ExpectedExit 0 -ExpectedToken 'MULTIPLAYER_U3_SCENARIO_Matrix_PASS'

    [IO.File]::WriteAllLines($fixture, $unknownFallback)
    Invoke-Validator -Phase Matrix -ExpectedExit 0 -ExpectedToken 'MULTIPLAYER_U3_SCENARIO_Matrix_PASS'

    [IO.File]::WriteAllLines($fixture, $timeoutFallback)
    Invoke-Validator -Phase Timeout -ExpectedExit 0 -ExpectedToken 'MULTIPLAYER_U3_SCENARIO_Timeout_PASS'

    [IO.File]::WriteAllLines($fixture, @($completeMatrix + $timeoutFallback))
    Invoke-Validator -Phase All -ExpectedExit 0 -ExpectedToken 'MULTIPLAYER_U3_SCENARIO_All_PASS'

    $badBudget = @($completeMatrix)
    $badBudget[0] = '[CombatSolver/Multiplayer] MP_SCENARIO_BUDGET total_node_budget=5000 main_node_budget=4600 reserved=512 decisions=2 scenarios_per_decision=4 decision_budget=128 replay_expanded=200 replay_transitions=240'
    [IO.File]::WriteAllLines($fixture, $badBudget)
    Invoke-Validator -Phase Matrix -ExpectedExit 1 -ExpectedToken 'MULTIPLAYER_U3_SCENARIO_Matrix_FAIL'

    $badUnknown = @($unknownFallback)
    $badUnknown[3] = '[CombatSolver/Multiplayer] MP_SCENARIO_RERANK enabled=true complete=true statuses=aggressive:Completed,defensive:Unknown,conserve:Completed,no_action:Completed scenario_count=3 all_alive=true guaranteed_victory=false worst_loss=0.2000 mean_loss=0.1000 worst_player_loss=0.1000 worst_enemy_durability=0.3000'
    $badUnknown[4] = '[CombatSolver/U0] FINAL_SELECTION route_policy=MultiplayerLocalCrossTurn state=3:4 turn=1 action_count=2 complete_victory=false scenario_rerank=true chance_rerank=false actions=PlayCard:DEFEND,EndTurn'
    [IO.File]::WriteAllLines($fixture, $badUnknown)
    Invoke-Validator -Phase Matrix -ExpectedExit 1 -ExpectedToken 'MULTIPLAYER_U3_SCENARIO_Matrix_FAIL'

    [IO.File]::WriteAllLines($fixture, @(
        '[CombatSolver/Multiplayer] MP_SCENARIO_BUDGET total_node_budget=5000 main_node_budget=4488 reserved=512 decisions=2 scenarios_per_decision=4 decision_budget=128 replay_expanded=1 replay_transitions=1',
        '[CombatSolver/Multiplayer] MP_SCENARIO_RERANK enabled=false reason=reevaluation_budget_unavailable',
        '[CombatSolver/U0] FINAL_SELECTION route_policy=MultiplayerLocalCrossTurn state=7:8 turn=1 action_count=2 complete_victory=false scenario_rerank=false chance_rerank=false actions=PlayCard:DEFEND,EndTurn'
    ))
    Invoke-Validator -Phase Timeout -ExpectedExit 1 -ExpectedToken 'MULTIPLAYER_U3_SCENARIO_Timeout_FAIL'

    [IO.File]::WriteAllLines($fixture, @(
        '[CombatSolver/Multiplayer] MP_OBJECTIVE strategy=AdaptiveLethalTempo'
    ))
    Invoke-Validator -Phase Matrix -ExpectedExit 2 -ExpectedToken 'MULTIPLAYER_U3_SCENARIO_Matrix_UNVERIFIED'
    Invoke-Validator -Phase Timeout -ExpectedExit 2 -ExpectedToken 'MULTIPLAYER_U3_SCENARIO_Timeout_UNVERIFIED'

    Write-Output 'MULTIPLAYER_U3_SCENARIO_VALIDATOR_PASS'
} finally {
    if (Test-Path -LiteralPath $fixture -PathType Leaf) {
        Remove-Item -LiteralPath $fixture -Force
    }
}
