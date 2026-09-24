#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validator = Join-Path $PSScriptRoot 'validate-u6-runtime-closure.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-u6-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Invoke-Case {
    param(
        [string]$Name,
        [string[]]$Lines,
        [int]$ExpectedExit,
        [string]$ExpectedStatus
    )
    $log = Join-Path $root ($Name + '.log')
    [IO.File]::WriteAllLines($log, $Lines, [Text.UTF8Encoding]::new($false))
    $output = & pwsh -NoLogo -NoProfile -File $validator -LogPath $log -Json 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne $ExpectedExit) {
        throw "$Name exit=$exit expected=$ExpectedExit output=$($output -join ' ')"
    }
    $result = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if ($result.status -ne $ExpectedStatus) {
        throw "$Name status=$($result.status) expected=$ExpectedStatus"
    }
    Write-Output "PASS $Name status=$ExpectedStatus"
}

$base = @(
    '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=1 request_id=31 route_generation=7 route_identity=abc new_authorization=true action_count=1 max_actions=1 search_world_version=8 stop_reason=kind_teammateforecast',
    '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=31 action_index=0 type=PlayCardAction turn=1 card=BASH local_net_id=1 custom_network_api_used=false',
    '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_END request_id=31 turn=1 action_count=1 end_turn=false stop_reason=kind_teammateforecast search_world_version=8 last_accepted_world_version=9 automatic_end_turn=false custom_network_api_used=false',
    '[CombatSolver/Multiplayer] MP_U5_OBSERVE turn=1 executed_actions=1 boundary=teammate_forecast proactive_wait=false timeout_ms=0 replan=true',
    '[CombatSolver/MultiplayerProbe] MP_REACTIVE_WORLD_DELTA world_version=10 reason=auto_turn_update remote_public_changed=true remote_readable_changed=true local_private_changed=false fresh_probe=true',
    '[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START world_version=10 request_id=77',
    '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=1 request_id=32 route_generation=8 route_identity=def new_authorization=true action_count=1 max_actions=1 search_world_version=10 stop_reason=safe_local_play_card'
)

try {
    Invoke-Case -Name 'pass' -Lines $base -ExpectedExit 0 -ExpectedStatus 'PASS'
    Invoke-Case -Name 'pass-no-redeploy' -Lines @(
        $base[0], $base[1], $base[2], $base[3], $base[4], $base[5]
    ) -ExpectedExit 0 -ExpectedStatus 'PASS'
    Invoke-Case -Name 'fail-old-request-reuse' -Lines @(
        $base[0], $base[1], $base[2], $base[3],
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=31 action_index=1 type=PlayCardAction turn=1 card=STRIKE local_net_id=1 custom_network_api_used=false',
        $base[4], $base[5], $base[6]
    ) -ExpectedExit 1 -ExpectedStatus 'FAIL'
    Invoke-Case -Name 'fail-stale-next-authorization' -Lines @(
        $base[0], $base[1], $base[2], $base[3], $base[4], $base[5],
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=1 request_id=31 route_generation=8 route_identity=stale new_authorization=true action_count=1 max_actions=1 search_world_version=9 stop_reason=safe_local_play_card'
    ) -ExpectedExit 1 -ExpectedStatus 'FAIL'
    Invoke-Case -Name 'unverified-no-remote-delta' -Lines @(
        $base[0], $base[1], $base[2], $base[3]
    ) -ExpectedExit 2 -ExpectedStatus 'UNVERIFIED'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
