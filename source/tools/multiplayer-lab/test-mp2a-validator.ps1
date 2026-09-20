#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validator = Join-Path $PSScriptRoot 'validate-mp2a-results.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-mp2a-validator-' + [guid]::NewGuid().ToString('N'))
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
        '[CombatSolver/MultiplayerSafeExecute] LAB_CAPABILITY enabled=true scope=owned_client_instance max_actions=1 automatic_end_turn=false custom_network_api=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2A_DEPLOY_START turn=1 action_count=1 search_world_version=4 stop_reason=mp2a_single_action_limit',
        '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED type=PlayCardAction turn=1 card=STRIKE local_net_id=1000 custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] DEPLOY_END turn=1 action_count=1 end_turn=false stop_reason=mp2a_single_action_limit search_world_version=4 current_world_version=4 automatic_end_turn=false custom_network_api_used=false',
        '[CombatSolver/MultiplayerSafeExecute] MP2A_WORLD_CHANGED world_version=5 reason=main_thread_monitor',
        '[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START world_version=5 request_id=2'
    )
    Invoke-Case -Name 'pass' -Lines $base -ExpectedExitCode 0

    $duplicate = @($base[0..2] + $base[2] + $base[3..5])
    Invoke-Case -Name 'duplicate-action' -Lines $duplicate -ExpectedExitCode 1

    $noResearch = @($base[0..4])
    Invoke-Case -Name 'missing-research' -Lines $noResearch -ExpectedExitCode 2

    Write-Output 'MP2A_VALIDATOR_OK checks=3'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
