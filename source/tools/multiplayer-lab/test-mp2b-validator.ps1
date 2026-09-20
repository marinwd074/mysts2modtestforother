#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validator = Join-Path $PSScriptRoot 'validate-mp2b-results.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-mp2b-validator-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Invoke-Case {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Lines,
        [Parameter(Mandatory)][int]$ExpectedExitCode
    )
    $path = Join-Path $root "$Name.log"
    [IO.File]::WriteAllLines($path, $Lines, [Text.UTF8Encoding]::new($false))
    $output = & pwsh -NoLogo -NoProfile -File $validator -LogPath $path -Json 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $ExpectedExitCode) {
        throw "$Name expected exit $ExpectedExitCode but received $exitCode. Output: $($output -join ' ')"
    }
}

try {
    $base = @(
        '[CombatSolver/MultiplayerSafeExecute] FORMAL_CAPABILITY enabled=true scope=explicit_opt_in max_actions=2 automatic_end_turn=false custom_network_api=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_CAPABILITY enabled=true max_actions=2 attribution=revalidation automatic_end_turn=false custom_network_api=false',
        '[CombatSolver/Evidence] ROUTE_REPLAY {"actionCount":4}',
        '[CombatSolver/Evidence] ROUTE_ACTION {"index":3,"action":{"Kind":"EndTurn"}}',
        '[CombatSolver/Test] RESULT replays=2 choice_replay_attempts=0',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=1 request_id=9 route_generation=4 action_count=2 max_actions=2 search_world_version=4 stop_reason=mp2b_two_action_limit',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=9 action_index=0 type=PlayCardAction turn=1 card=STRIKE local_net_id=1000 custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED request_id=9 action_index=0 card=STRIKE decision=SafeToContinue reason=safe_to_continue before_world_version=4 after_world_version=5 observation_sequence=8',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=9 action_index=1 type=PlayCardAction turn=1 card=DEFEND local_net_id=1000 custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED request_id=9 action_index=1 card=DEFEND decision=ExpectedLocalChange reason=expected_local_change before_world_version=5 after_world_version=6 observation_sequence=9',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_END request_id=9 turn=1 action_count=2 end_turn=false stop_reason=mp2b_two_action_limit search_world_version=4 last_accepted_world_version=6 automatic_end_turn=false custom_network_api_used=false',
        '[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START world_version=6 request_id=10'
    )
    Invoke-Case -Name 'pass' -Lines $base -ExpectedExitCode 0

    $remoteAbort = @($base | Where-Object { $_ -notmatch 'MP2B_ACTION_RECONCILED request_id=9 action_index=1' -and $_ -notmatch 'NATIVE_ACTION_CAPTURED request_id=9 action_index=1' -and $_ -notmatch 'MP2B_DEPLOY_END' }) + @(
        '[CombatSolver/MultiplayerSafeExecute] MP2B_REMOTE_DELTA_ABORT request_id=9 turn=1 completed_actions=1 reason=remote_or_unknown_change last_accepted_world_version=5'
    )
    Invoke-Case -Name 'remote-abort-is-not-normal-pass' -Lines $remoteAbort -ExpectedExitCode 1

    $duplicate = @($base[0..5] + $base[5] + $base[6..8])
    Invoke-Case -Name 'duplicate-action' -Lines $duplicate -ExpectedExitCode 1

    $missingResearch = @($base | Where-Object { $_ -notmatch 'SEARCH_DEBOUNCED_START' })
    Invoke-Case -Name 'missing-research' -Lines $missingResearch -ExpectedExitCode 2

    $manualEndTurn = $base + @(
        '[DEBUG] [ActionExecutor] Executing action: EndPlayerTurnAction for player 1000 turn 1'
    )
    Invoke-Case -Name 'manual-end-turn-is-not-allowed-in-normal-smoke' -Lines $manualEndTurn -ExpectedExitCode 1

    Write-Output 'MP2B_VALIDATOR_OK checks=5'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
