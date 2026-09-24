#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$validator = Join-Path $scriptRoot 'validate-beam-retention-ab-results.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-beam-ab-' + [Guid]::NewGuid().ToString('N') + '.log')

function Invoke-Case {
    param(
        [Parameter(Mandatory)][string[]]$Lines,
        [Parameter(Mandatory)][int]$ExpectedExit,
        [Parameter(Mandatory)][string]$ExpectedToken
    )
    [IO.File]::WriteAllLines($fixture, $Lines)
    $output = @(& pwsh -NoLogo -NoProfile -File $validator -LogPath $fixture)
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "Beam A/B validator returned $LASTEXITCODE, expected $ExpectedExit. Output: $($output -join ' | ')"
    }
    if (($output -join [Environment]::NewLine) -notmatch [regex]::Escape($ExpectedToken)) {
        throw "Beam A/B validator output did not contain '$ExpectedToken'. Output: $($output -join ' | ')"
    }
}

$start = '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB_START beam=24 detailed=true'
$drop = '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB sample=1 boundary=7 turn=1 pool=60 limit=24 legacy_only_raw=2 legacy_only_selected=1 legacy_rank=20 production_raw_rank=31 production_selected_rank=0 all_alive=true loss_eq=0.100000 worst_player_loss=0.100000 team_loss=0.080000 enemy_durability=0.400000 prefix=1:PlayCard:STRIKE'
$pruned = '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB_FINAL sample=1 boundary=7 legacy_only_raw=2 legacy_only_selected=1 legacy_rank=20 production_raw_rank=31 portfolio_rank=0 final_rank=0 rescued_by_outer_portfolio=false survived_incumbent=false beam_pruned=true final_pruned=true prefix=1:PlayCard:STRIKE'
$rescued = '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB_FINAL sample=1 boundary=7 legacy_only_raw=2 legacy_only_selected=1 legacy_rank=20 production_raw_rank=31 portfolio_rank=28 final_rank=28 rescued_by_outer_portfolio=true survived_incumbent=true beam_pruned=false final_pruned=false prefix=1:PlayCard:STRIKE'
$rawOnly = '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB sample=1 boundary=7 turn=1 pool=60 limit=24 legacy_only_raw=2 legacy_only_selected=0 legacy_rank=20 production_raw_rank=31 production_selected_rank=18 all_alive=true loss_eq=0.100000 worst_player_loss=0.100000 team_loss=0.080000 enemy_durability=0.400000 prefix=1:PlayCard:STRIKE'

try {
    Invoke-Case -Lines @($start) -ExpectedExit 0 -ExpectedToken 'classification=no_difference_observed'
    Invoke-Case -Lines @($start, $rawOnly) -ExpectedExit 0 -ExpectedToken 'classification=raw_rank_difference_only'
    Invoke-Case -Lines @($start, $drop, $pruned) -ExpectedExit 0 -ExpectedToken 'classification=beam_pruning_observed'
    Invoke-Case -Lines @($start, $drop, $rescued) -ExpectedExit 0 -ExpectedToken 'classification=outer_portfolio_rescue_observed'
    Invoke-Case -Lines @($start, $drop) -ExpectedExit 1 -ExpectedToken 'MULTIPLAYER_BEAM_RETENTION_AB_FAIL'
    Invoke-Case -Lines @('[CombatSolver/Multiplayer] MP_OBJECTIVE strategy=AdaptiveLethalTempo') -ExpectedExit 2 -ExpectedToken 'MULTIPLAYER_BEAM_RETENTION_AB_UNVERIFIED'
    Write-Output 'MULTIPLAYER_BEAM_RETENTION_AB_VALIDATOR_PASS'
} finally {
    if (Test-Path -LiteralPath $fixture -PathType Leaf) {
        Remove-Item -LiteralPath $fixture -Force
    }
}
