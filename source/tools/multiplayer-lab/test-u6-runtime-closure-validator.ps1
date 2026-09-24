#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validator = Join-Path $PSScriptRoot 'validate-u6-runtime-closure.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-u6-validator-' + [guid]::NewGuid().ToString('N'))
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
    if ($LASTEXITCODE -ne $ExpectedExitCode) {
        throw "$Name expected exit $ExpectedExitCode but got $LASTEXITCODE. Output: $($output -join ' ')"
    }
}

try {
    $semanticMismatch = @(
        '[CombatSolver/MultiplayerSafeExecute] MP2B_CAPABILITY enabled=true action_limit=route_bounded attribution=revalidation automatic_end_turn=false custom_network_api=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=1 request_id=30 route_generation=7 action_count=2 max_actions=2 search_world_version=4 stop_reason=allow',
        '[CombatSolver/MultiplayerSafeExecute] U1_PRE_ACTION_PROBE request_id=30 turn=1 action_index=0 changed=false world_version=4 last_accepted_world_version=4',
        '[CombatSolver/MultiplayerSafeExecute] U1_EXPECTED_POST_STATE action=STRIKE state_fp=A:B remote_fp=C:D boundary=None elapsed_ms=1',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=30 action_index=0 type=PlayCardAction turn=1 card=STRIKE local_net_id=1000 custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] U1_POST_STATE_COMPARE request_id=30 turn=1 action_index=0 card=STRIKE continuation_match=true remote_match=false expected_state_fp=A:B actual_state_fp=A:B expected_remote_fp=C:D actual_remote_fp=E:F differences=[]',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED request_id=30 action_index=0 card=STRIKE decision=RemoteOrUnknownChange reason=remote_or_unknown_change semantic_state_match=true semantic_remote_match=false before_world_version=4 after_world_version=5 observation_sequence=8',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_REMOTE_DELTA_ABORT request_id=30 turn=1 completed_actions=1 reason=remote_or_unknown_change last_accepted_world_version=4',
        '[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START world_version=5 request_id=31'
    )
    Invoke-Case -Name 'semantic-remote-mismatch-pass' -Lines $semanticMismatch -ExpectedExitCode 0

    $preActionChange = @(
        '[CombatSolver/MultiplayerSafeExecute] MP2B_CAPABILITY enabled=true action_limit=route_bounded attribution=revalidation automatic_end_turn=false custom_network_api=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=1 request_id=40 route_generation=9 action_count=3 max_actions=3 search_world_version=10 stop_reason=allow',
        '[CombatSolver/MultiplayerSafeExecute] U1_PRE_ACTION_PROBE request_id=40 turn=1 action_index=0 changed=false world_version=10 last_accepted_world_version=10',
        '[CombatSolver/MultiplayerSafeExecute] U1_EXPECTED_POST_STATE action=STRIKE state_fp=A:B remote_fp=C:D boundary=None elapsed_ms=1',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=40 action_index=0 type=PlayCardAction turn=1 card=STRIKE local_net_id=1000 custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] U1_POST_STATE_COMPARE request_id=40 turn=1 action_index=0 card=STRIKE continuation_match=true remote_match=true expected_state_fp=A:B actual_state_fp=A:B expected_remote_fp=C:D actual_remote_fp=C:D differences=[]',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED request_id=40 action_index=0 card=STRIKE decision=SafeToContinue reason=safe_to_continue semantic_state_match=true semantic_remote_match=true before_world_version=10 after_world_version=11 observation_sequence=20',
        '[CombatSolver/MultiplayerSafeExecute] U1_PRE_ACTION_PROBE request_id=40 turn=1 action_index=1 changed=true world_version=12 last_accepted_world_version=11',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_ABORT request_id=40 turn=1 completed_actions=1 reason=world_version_not_accepted last_accepted_world_version=11',
        '[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START world_version=12 request_id=41'
    )
    Invoke-Case -Name 'pre-action-world-change-pass' -Lines $preActionChange -ExpectedExitCode 0

    $staleSubmit = $preActionChange + @(
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=40 action_index=1 type=PlayCardAction turn=1 card=DEFEND local_net_id=1000 custom_network_api_used=false'
    )
    Invoke-Case -Name 'stale-native-submit-fails' -Lines $staleSubmit -ExpectedExitCode 1

    Write-Output 'U6_RUNTIME_CLOSURE_VALIDATOR_OK checks=3'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
