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
        '[CombatSolver/MultiplayerSafeExecute] MP2B_CAPABILITY enabled=true max_actions=2 attribution=revalidation automatic_end_turn=false custom_network_api=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn=1 request_id=12 route_generation=4 action_count=2 max_actions=2 search_world_version=4 stop_reason=mp2b_two_action_limit',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=12 action_index=0 type=PlayCardAction turn=1 card=STRIKE local_net_id=1000 custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED request_id=12 action_index=0 card=STRIKE decision=SafeToContinue reason=safe_to_continue before_world_version=4 after_world_version=5 observation_sequence=8',
        '[CombatSolver/MultiplayerSafeExecute] MP2B_REMOTE_DELTA_ABORT request_id=12 turn=1 completed_actions=1 reason=remote_or_unknown_change last_accepted_world_version=5',
        '[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START world_version=5 request_id=13'
    )
    Invoke-Case -Name 'pass-after-first-action' -Lines $base -ExpectedExitCode 0

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

    Write-Output 'MP2B_INTERFERENCE_VALIDATOR_OK checks=5'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
