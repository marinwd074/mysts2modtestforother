#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-joint-continuation-' + [Guid]::NewGuid().ToString('N') + '.log')
$validator = Join-Path $scriptRoot 'validate-joint-continuation-results.ps1'

function Invoke-Validator {
    param(
        [Parameter(Mandatory)][ValidateSet('Reuse', 'Mismatch', 'RouteReplay')][string]$Mode,
        [Parameter(Mandatory)][int]$ExpectedExit,
        [string]$ExpectedRejectReason = 'remote_public_mismatch',
        [int]$MinReplays = 1
    )
    & pwsh -NoLogo -NoProfile -File $validator -LogPath $fixture -Mode $Mode -ExpectedRejectReason $ExpectedRejectReason -MinReplays $MinReplays
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "Joint continuation validator returned $LASTEXITCODE for $Mode, expected $ExpectedExit."
    }
}

try {
    [IO.File]::WriteAllLines($fixture, @(
        '[CombatSolver/MultiplayerProbe] MP_LOCAL_CROSS_TURN_FRESH_PROBE reason=continuation_validation changed=true world_version=12 fresh_probe=true',
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_VALIDATE turn=2 route_identity=route-a source_world_version=10 minimum_world_version=11 actual_world_version=12 fresh_probe_changed=true',
        '[CombatSolver/Test] SEARCH_REUSED from_turn=1 turn=2 validation=exact_state_text remaining_turns=2 route_identity=route-a old_authorization_dead=true new_authorization_pending=false',
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REUSED turn=2 route_identity=route-a source_world_version=10 minimum_world_version=11 actual_world_version=12 local_state_exact=true reason=exact'
    ))
    Invoke-Validator -Mode Reuse -ExpectedExit 0

    $messages = @(Get-Content -LiteralPath $fixture)
    [IO.File]::WriteAllLines($fixture, @($messages | ForEach-Object {
        @{ Time = 1; Level = 'info'; Message = $_ } | ConvertTo-Json -Compress
    }))
    Invoke-Validator -Mode Reuse -ExpectedExit 0

    [IO.File]::WriteAllLines($fixture, @(
        '[CombatSolver/MultiplayerProbe] MP_LOCAL_CROSS_TURN_FRESH_PROBE reason=continuation_validation changed=true world_version=12 fresh_probe=true',
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_VALIDATE turn=2 route_identity=route-b source_world_version=10 minimum_world_version=11 actual_world_version=12 fresh_probe_changed=true',
        '[CombatSolver/Test] SEARCH_REUSE_MISS turn=2 reason=state_mismatch cached_turns=2 previous_boundary=None continuation_reject_reason=remote_public_mismatch local_state_exact=true diff_count=0 field=multiplayer_validation',
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REJECTED turn=2 route_identity=route-b source_world_version=10 minimum_world_version=11 actual_world_version=12 local_state_exact=true reason=remote_public_mismatch',
        '[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_SEARCH_START generation=9 world_version=12 turn=2 reason=AutoTurnStart'
    ))
    Invoke-Validator -Mode Mismatch -ExpectedExit 0

    $messages = @(Get-Content -LiteralPath $fixture)
    [IO.File]::WriteAllLines($fixture, @($messages | ForEach-Object {
        @{ Time = 1; Level = 'info'; Message = $_.Replace('remote_public_mismatch', 'local_state_mismatch') } | ConvertTo-Json -Compress
    }))
    Invoke-Validator -Mode Mismatch -ExpectedExit 0 -ExpectedRejectReason local_state_mismatch
    Invoke-Validator -Mode Mismatch -ExpectedExit 1

    $replayFixture = @(
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_VALIDATE turn=2 route_identity=old source_world_version=10 minimum_world_version=11 actual_world_version=12 local_core_reject_reason=non_shuffle_rng_changed:targets allow_living_enemy_hp_decrease=true',
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REJECTED turn=2 route_identity=old reason=local_state_mismatch',
        '[CombatSolver/MultiplayerSafeExecute] MP_REACTIVE_FRESH_SEARCH generation=2 turn=2 world_version=12 fresh_probe=true fresh_capture=true after_safe_end_turn=true previous_end_turn_request_id=1 cross_turn_reuse=false',
        '[CombatSolver/Test] SEARCH_REQUEST generation=2 turn=2',
        '[CombatSolver/Test] MP_LOCAL_XTURN_ROUTE_REPLAY status=accepted fresh_root=true expanded=0 actions=3 hp_loss=16 elapsed_ms=2.0 quality=equivalent_partial full_search_skipped=true',
        '[CombatSolver/Test] SEARCH_REQUEST_PHASE {"RecordedSolverCount":1,"ExpandedNodes":0,"LogicalTransitions":3,"Members":[{"Kind":"RouteReplay","Outcome":"Completed","Work":{"ExpandedNodes":0,"TransitionCount":3}}]}',
        '[CombatSolver/Test] SEARCH_RESULT_ROUTE_CAPTURE generation=2 deployment_scope=SearchCompletion route_scope=PartialLocalCrossTurnProjection route_identity=new',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=2 request_id=2 route_generation=2 route_identity=new new_authorization=true action_count=1 search_world_version=12',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED request_id=2 action_index=0 card=STRIKE_IRONCLAD decision=SafeToContinue native_action_captured=true action_queue_idle=true',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_END_TURN_REVALIDATED request_id=2 turn=2 decision=Safe',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=2 type=EndPlayerTurnAction',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_SAFE_END_TURN_ACCEPTED request_id=2 turn=2 route_identity=new action_count=1 session_cleared=true authorization_cleared=true automatic_end_turn=true custom_network_api_used=false'
    )
    [IO.File]::WriteAllLines($fixture, $replayFixture)
    Invoke-Validator -Mode RouteReplay -ExpectedExit 0
    Invoke-Validator -Mode RouteReplay -MinReplays 2 -ExpectedExit 2
    [IO.File]::WriteAllLines($fixture, @($replayFixture | ForEach-Object {
        @{Time=1;Level='info';Message=$_} | ConvertTo-Json -Compress
    }))
    Invoke-Validator -Mode RouteReplay -ExpectedExit 0
    foreach ($mutation in @(
        @('new_authorization=true','new_authorization=false'),
        @('local_core_reject_reason=non_shuffle_rng_changed:targets','local_core_reject_reason=non_shuffle_rng_changed:card_generation'),
        @('"Kind":"RouteReplay"','"Kind":"Baseline"'),
        @('generation=2 deployment_scope=SearchCompletion','generation=3 deployment_scope=SearchCompletion'),
        @('MP2B_ACTION_RECONCILED request_id=2','MP2B_ACTION_RECONCILED request_id=1'),
        @('route_identity=new','route_identity=old'),
        @('quality=equivalent_partial','quality=not_proven'),
        @('action_index=0','action_index=1'),
        @('authorization_cleared=true','authorization_cleared=false')
    )) {
        [IO.File]::WriteAllLines($fixture, @($replayFixture | ForEach-Object { $_.Replace($mutation[0],$mutation[1]) }))
        Invoke-Validator -Mode RouteReplay -ExpectedExit $(if($mutation[0] -like 'generation=*') {2} else {1})
    }
    $splitRequest = @($replayFixture[0..4]) + '[CombatSolver/Test] SEARCH_REQUEST generation=3 turn=3' + @($replayFixture[5..11])
    [IO.File]::WriteAllLines($fixture, $splitRequest)
    Invoke-Validator -Mode RouteReplay -ExpectedExit 1
    [IO.File]::WriteAllLines($fixture, @($replayFixture | Where-Object { $_ -notmatch 'MP_LOCAL_XTURN_ROUTE_REPLAY ' }))
    Invoke-Validator -Mode RouteReplay -ExpectedExit 2

    [IO.File]::WriteAllLines($fixture, @(
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_VALIDATE turn=2 route_identity=route-c source_world_version=10 minimum_world_version=11 actual_world_version=12 fresh_probe_changed=true',
        '[CombatSolver/Test] SEARCH_REUSE_MISS turn=2 reason=state_mismatch cached_turns=2 previous_boundary=None continuation_reject_reason=scaling_mismatch local_state_exact=true diff_count=0 field=multiplayer_validation',
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REJECTED turn=2 route_identity=route-c source_world_version=10 minimum_world_version=11 actual_world_version=12 local_state_exact=true reason=scaling_mismatch',
        '[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_SEARCH_START generation=10 world_version=12 turn=2 reason=AutoTurnStart'
    ))
    Invoke-Validator -Mode Mismatch -ExpectedExit 1

    Write-Output 'MULTIPLAYER_JOINT_CONTINUATION_VALIDATOR_PASS'
} finally {
    if (Test-Path -LiteralPath $fixture -PathType Leaf) {
        Remove-Item -LiteralPath $fixture -Force
    }
}
exit 0
