#requires -Version 7.0

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validator = Join-Path $PSScriptRoot 'validate-beam-objective-ab-results.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-beam-ab-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Run-Case {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Lines,
        [Parameter(Mandatory)][int]$ExpectedExit,
        [Parameter(Mandatory)][string]$ExpectedClassification
    )
    $log = Join-Path $root "$Name.log"
    $out = Join-Path $root "$Name.json"
    Set-Content -LiteralPath $log -Value $Lines -Encoding utf8
    & pwsh -NoLogo -NoProfile -File $validator -LogPath $log -OutputPath $out -Json *> $null
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $ExpectedExit) {
        throw "$Name exit mismatch: expected=$ExpectedExit actual=$exitCode"
    }
    $result = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    if ($result.classification -ne $ExpectedClassification) {
        throw "$Name classification mismatch: expected=$ExpectedClassification actual=$($result.classification)"
    }
}

try {
    Run-Case diagnostic-off @('ordinary log line') 2 'diagnostic_not_active'

    Run-Case raw-only @(
        '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB_START beam=24 detailed=true',
        '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB sample=1 boundary=1 turn=1 pool=30 limit=24 legacy_only_raw=1 legacy_only_selected=0 legacy_rank=24 production_raw_rank=25 production_selected_rank=0 all_alive=true loss_eq=0.1 worst_player_loss=0.1 team_loss=0.1 enemy_durability=0.5 prefix=1:PlayCard:A'
    ) 0 'raw_order_only'

    Run-Case rescued @(
        '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB_START beam=24 detailed=true',
        '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB sample=1 boundary=2 turn=1 pool=30 limit=24 legacy_only_raw=1 legacy_only_selected=1 legacy_rank=24 production_raw_rank=25 production_selected_rank=0 all_alive=true loss_eq=0.1 worst_player_loss=0.1 team_loss=0.1 enemy_durability=0.5 prefix=1:PlayCard:A',
        '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB_FINAL sample=1 boundary=2 legacy_only_raw=1 legacy_only_selected=1 legacy_rank=24 production_raw_rank=25 portfolio_rank=25 final_rank=25 rescued_by_outer_portfolio=true survived_incumbent=true beam_pruned=false final_pruned=false prefix=1:PlayCard:A'
    ) 0 'rescued_by_outer_portfolio'

    Run-Case pruned @(
        '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB_START beam=24 detailed=true',
        '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB sample=1 boundary=3 turn=1 pool=30 limit=24 legacy_only_raw=1 legacy_only_selected=1 legacy_rank=24 production_raw_rank=25 production_selected_rank=0 all_alive=true loss_eq=0.1 worst_player_loss=0.1 team_loss=0.1 enemy_durability=0.5 prefix=1:PlayCard:A',
        '[CombatSolver/Multiplayer] MP_BEAM_RETENTION_AB_FINAL sample=1 boundary=3 legacy_only_raw=1 legacy_only_selected=1 legacy_rank=24 production_raw_rank=25 portfolio_rank=0 final_rank=0 rescued_by_outer_portfolio=false survived_incumbent=false beam_pruned=true final_pruned=true prefix=1:PlayCard:A'
    ) 0 'beam_pruned_legacy_candidate'

    Write-Output 'MULTIPLAYER_BEAM_OBJECTIVE_AB_VALIDATOR_PASS cases=4'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
