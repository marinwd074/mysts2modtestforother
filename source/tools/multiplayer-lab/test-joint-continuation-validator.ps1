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
        [Parameter(Mandatory)][ValidateSet('Reuse', 'Mismatch')][string]$Mode,
        [Parameter(Mandatory)][int]$ExpectedExit
    )
    & pwsh -NoLogo -NoProfile -File $validator -LogPath $fixture -Mode $Mode
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

    [IO.File]::WriteAllLines($fixture, @(
        '[CombatSolver/MultiplayerProbe] MP_LOCAL_CROSS_TURN_FRESH_PROBE reason=continuation_validation changed=true world_version=12 fresh_probe=true',
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_VALIDATE turn=2 route_identity=route-b source_world_version=10 minimum_world_version=11 actual_world_version=12 fresh_probe_changed=true',
        '[CombatSolver/Test] SEARCH_REUSE_MISS turn=2 reason=state_mismatch cached_turns=2 previous_boundary=None continuation_reject_reason=remote_public_mismatch local_state_exact=true diff_count=0 field=multiplayer_validation',
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REJECTED turn=2 route_identity=route-b source_world_version=10 minimum_world_version=11 actual_world_version=12 local_state_exact=true reason=remote_public_mismatch',
        '[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_SEARCH_START generation=9 world_version=12 turn=2 reason=AutoTurnStart'
    ))
    Invoke-Validator -Mode Mismatch -ExpectedExit 0

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
