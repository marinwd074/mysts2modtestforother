#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validator = Join-Path $PSScriptRoot 'validate-mp2b-interference-results.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-mp2b-interference-validator-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Invoke-Case {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Lines,
        [Parameter(Mandatory)][int]$ExpectedExitCode,
        [int]$RequestId = 0,
        [int]$MinCompletedActions = 1,
        [int]$MaxActions = 0
    )
    $path = Join-Path $root "$Name.log"
    [IO.File]::WriteAllLines($path, $Lines, [Text.UTF8Encoding]::new($false))
    $validatorArgs = @('-NoLogo', '-NoProfile', '-File', $validator, '-LogPath', $path, '-Json')
    if ($MinCompletedActions -ne 1) { $validatorArgs += @('-MinCompletedActions', $MinCompletedActions) }
    if ($MaxActions -gt 0) { $validatorArgs += @('-MaxActions', $MaxActions) }
    if ($RequestId -gt 0) {
        $validatorArgs += @('-RequestId', $RequestId)
    }
    $output = & pwsh @validatorArgs 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $ExpectedExitCode) {
        throw "$Name expected exit $ExpectedExitCode but received $exitCode. Output: $($output -join ' ')"
    }
}

try {
    $base = @(
        '[CombatSolver/MultiplayerSafeExecute] MP2B_CAPABILITY enabled=true action_limit=selected_route attribution=revalidation automatic_end_turn=false custom_network_api=false',
        '[CombatSolver/Evidence] ROUTE_REPLAY {"actionCount":4}',
        '[CombatSolver/Evidence] ROUTE_ACTION {"index":3,"action":{"Kind":"EndTurn"}}',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=1 request_id=12 route_generation=4 action_count=2 max_actions=2 search_world_version=4 stop_reason=mp2b_two_action_limit',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=12 action_index=0 type=PlayCardAction turn=1 card=STRIKE local_net_id=1000 custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED request_id=12 action_index=0 card=STRIKE decision=SafeToContinue reason=safe_to_continue before_world_version=4 after_world_version=5 observation_sequence=8',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_REMOTE_DELTA_ABORT request_id=12 turn=1 completed_actions=1 reason=remote_or_unknown_change last_accepted_world_version=5',
        '[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START world_version=5 request_id=13'
    )
    Invoke-Case -Name 'pass-after-first-action' -Lines $base -ExpectedExitCode 0

    $secondSession = @($base | Where-Object { $_ -notmatch 'MP2B_CAPABILITY' } | ForEach-Object {
            $_ -replace 'request_id=12\b', 'request_id=13'
        })
    Invoke-Case -Name 'request-id-selects-one-session' -Lines ($base + $secondSession) -ExpectedExitCode 0 -RequestId 12

    $duringRevalidation = @($base | ForEach-Object {
            $_ -replace 'decision=SafeToContinue reason=safe_to_continue', 'decision=RemoteOrUnknownChange reason=remote_or_unknown_change'
        })
    Invoke-Case -Name 'pass-during-revalidation' -Lines $duringRevalidation -ExpectedExitCode 0

    $secondAction = $base + @(
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=12 action_index=1 type=PlayCardAction turn=1 card=DEFEND local_net_id=1000 custom_network_api_used=false'
    )
    Invoke-Case -Name 'second-action-is-fail' -Lines $secondAction -ExpectedExitCode 1

    $missingAbort = @($base | Where-Object { $_ -notmatch 'MP2B_REMOTE_DELTA_ABORT' })
    Invoke-Case -Name 'missing-abort-is-unverified' -Lines $missingAbort -ExpectedExitCode 2

    $missingSearch = @($base | Where-Object { $_ -notmatch 'SEARCH_DEBOUNCED_START' })
    Invoke-Case -Name 'missing-search-is-unverified' -Lines $missingSearch -ExpectedExitCode 2

    $twoActionAbort = @(
        '[CombatSolver/MultiplayerSafeExecute] MP2B_CAPABILITY enabled=true action_limit=selected_route attribution=revalidation automatic_end_turn=false custom_network_api=false',
        '[CombatSolver/Evidence] ROUTE_REPLAY {"actionCount":6}',
        '[CombatSolver/Evidence] ROUTE_ACTION {"index":5,"action":{"Kind":"EndTurn"}}',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=1 request_id=22 route_generation=4 action_count=5 max_actions=6 search_world_version=4 stop_reason=safe_local_play_card',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=22 action_index=0 type=PlayCardAction turn=1 card=STRIKE local_net_id=1000 custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED request_id=22 action_index=0 card=STRIKE decision=SafeToContinue reason=safe_to_continue before_world_version=4 after_world_version=5 observation_sequence=8',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=22 action_index=1 type=PlayCardAction turn=1 card=DEFEND local_net_id=1000 custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED request_id=22 action_index=1 card=DEFEND decision=SafeToContinue reason=safe_to_continue before_world_version=5 after_world_version=6 observation_sequence=9',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_REMOTE_DELTA_ABORT request_id=22 turn=1 completed_actions=2 reason=remote_or_unknown_change last_accepted_world_version=6',
        '[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START world_version=6 request_id=23'
    )
    Invoke-Case -Name 'two-actions-before-remote-abort' -Lines $twoActionAbort -ExpectedExitCode 0 -MinCompletedActions 2 -MaxActions 6

    Write-Output 'MP2B_INTERFERENCE_VALIDATOR_OK checks=6'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
