#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
$classifier = Join-Path $scriptRoot 'classify-p0-baseline-result.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-p0-classifier-' + [Guid]::NewGuid().ToString('N') + '.log')
$checks = 0

function Check {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
    $script:checks++
    Write-Output ("PASS {0}: {1}" -f $script:checks, $Message)
}

function Classify {
    param([Parameter(Mandatory)][string[]]$Lines)
    [IO.File]::WriteAllLines($fixture, $Lines)
    $raw = & pwsh -NoLogo -NoProfile -File $classifier -LogPath $fixture -Json
    if ($LASTEXITCODE -ne 0) {
        throw "P0 classifier exited with $LASTEXITCODE."
    }
    return (($raw | Out-String) | ConvertFrom-Json)
}

try {
    $simulation = Classify @(
        '{"Time":1,"Level":"info","Message":"[CombatSolver/Evidence] ROUTE_REPLAY {\\"schemaVersion\\":1,\\"firstScalarDifference\\":2,\\"actionCount\\":4}"}'
    )
    Check ($simulation.primaryClassification -eq 'simulation_error') 'Replay scalar divergence classifies as simulation_error.'

    $searchMiss = Classify @(
        '[CombatSolver/Test] MANUAL_ROUTE_IMPROVED original_turn=1 current_turn=1 previous_projected_battle_hp_lost=8 current_projected_battle_hp_lost=3 difference=-5'
    )
    Check ($searchMiss.primaryClassification -eq 'search_miss_evidence') 'Observed better route classifies as search_miss_evidence rather than simulator drift.'

    $teammate = Classify @(
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REJECTED turn=2 route_identity=r1 source_world_version=10 minimum_world_version=11 actual_world_version=12 local_state_exact=true reason=remote_public_mismatch',
        '[CombatSolver/Test] SEARCH_REUSE_MISS turn=2 reason=state_mismatch cached_turns=2 previous_boundary=None continuation_reject_reason=remote_public_mismatch local_state_exact=true diff_count=0 field=multiplayer_validation'
    )
    Check (($teammate.classifications -contains 'teammate_prediction_deviation') -and -not ($teammate.classifications -contains 'simulation_error')) 'Exact local state plus remote mismatch classifies as teammate_prediction_deviation.'

    $runtime = Classify @(
        '[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REJECTED turn=2 route_identity=r2 source_world_version=10 minimum_world_version=11 actual_world_version=12 local_state_exact=false reason=combat_identity_mismatch'
    )
    Check ($runtime.primaryClassification -eq 'runtime_state_mismatch') 'Non-forecast continuation rejection classifies as runtime_state_mismatch.'

    $none = Classify @(
        '[CombatSolver/Test] SEARCH_COMPLETE turn=1'
    )
    Check ($none.primaryClassification -eq 'unclassified') 'Logs without P0 failure evidence remain unclassified.'

    Write-Output ("P0_BASELINE_CLASSIFIER_PASS checks={0}" -f $checks)
} finally {
    if (Test-Path -LiteralPath $fixture -PathType Leaf) {
        Remove-Item -LiteralPath $fixture -Force
    }
}
