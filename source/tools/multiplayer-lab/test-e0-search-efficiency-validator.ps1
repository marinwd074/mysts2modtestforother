#requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validator = Join-Path $PSScriptRoot 'validate-e0-search-efficiency-results.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ("combatsolver-e0-validator-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Add-Message {
    param([string] $Path, [string] $Message)
    [pscustomobject]@{
        Time = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        Level = 'info'
        Message = $Message
    } | ConvertTo-Json -Compress | Add-Content -LiteralPath $Path -Encoding utf8
}

function Invoke-Case {
    param(
        [string] $Name,
        [string[]] $Messages,
        [int] $ExpectedExit,
        [string] $ExpectedStatus
    )

    $caseRoot = Join-Path $temp $Name
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    $log = Join-Path $caseRoot 'combat.jsonl'
    foreach ($message in $Messages) {
        Add-Message -Path $log -Message $message
    }
    $out = Join-Path $caseRoot 'result.json'
    & pwsh -NoLogo -NoProfile -File $validator -EvidencePath $log -OutputPath $out
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "$Name exit mismatch: expected=$ExpectedExit actual=$LASTEXITCODE"
    }
    $result = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    if ([string]$result.status -ne $ExpectedStatus) {
        throw "$Name status mismatch: expected=$ExpectedStatus actual=$($result.status)"
    }
}

$runtime = '[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START world_version=3 request_id=9'
$timeline = '[CombatSolver/Test] SEARCH_E0_TIMELINE candidate_id=7 player_count=2 member_id=1 member_kind=potion_disabled generated_ms=100.000 evaluated_ms=120.000 selected_ms=121.000 published_ms=200.000 expanded_at_generation=42 turn_depth=1 context=rules=0.107.1;route=MultiplayerLocalCrossTurn;team=True;objective=Robust;scenario=True;potion=Smart;theft=none;growth=False;turn_setup=False;completion=win'
$member = '[CombatSolver/Test] SEARCH_E0_MEMBER member_id=1 kind=potion_disabled beam=60 second_rank_band=False base_score_only=False novelty=False elapsed_ms=150.000 expanded=1000 transitions=2500'
$shadow = '[CombatSolver/Test] SEARCH_E0_PHASE member_id=1 phase=shadow calls=3 exclusive_ms=12.500'
$scenario = '[CombatSolver/Test] SEARCH_E0_PHASE member_id=1 phase=scenario_matrix calls=1 exclusive_ms=5.500'
$materialize = '[CombatSolver/Test] SEARCH_E0_PHASE member_id=1 phase=materialization calls=1 exclusive_ms=2.000'
$replay = '[CombatSolver/Test] SEARCH_E0_PHASE member_id=1 phase=materialization_replay calls=1 exclusive_ms=1.000'

Invoke-Case -Name 'pass' -ExpectedExit 0 -ExpectedStatus 'PASS' -Messages @(
    $runtime, $timeline, $member, $shadow, $scenario, $materialize, $replay)

Invoke-Case -Name 'unverified' -ExpectedExit 2 -ExpectedStatus 'UNVERIFIED' -Messages @(
    $timeline, $member, $shadow, $scenario)

$badTimeline = $timeline.Replace('selected_ms=121.000', 'selected_ms=221.000')
Invoke-Case -Name 'fail' -ExpectedExit 1 -ExpectedStatus 'FAIL' -Messages @(
    $runtime, $badTimeline, $member, $shadow, $scenario)

Remove-Item -LiteralPath $temp -Recurse -Force
Write-Host 'E0_MULTIPLAYER_VALIDATOR_TEST PASS cases=3'
exit 0
