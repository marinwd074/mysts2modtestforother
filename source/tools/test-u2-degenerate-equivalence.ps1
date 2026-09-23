#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$HarnessDll = (Join-Path $PSScriptRoot 'OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll'),
    [string]$Workspace = (Join-Path $PSScriptRoot '../.local/u2-degenerate-equivalence'),
    [string]$Character = 'IRONCLAD',
    [string]$Encounter = 'FUZZY_WURM_CRAWLER_WEAK',
    [string]$Seed = 'U2DEGENERATE1',
    [int]$Beam = 24,
    [int]$Nodes = 4000,
    [int]$BudgetMilliseconds = 600000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$HarnessDll = [System.IO.Path]::GetFullPath($HarnessDll)
$Workspace = [System.IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $HarnessDll)) {
    throw "OfflineSearchHarness not built: $HarnessDll"
}

Remove-Item -LiteralPath $Workspace -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Workspace -Force | Out-Null

function Invoke-U2Run {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$RoutePolicy
    )

    $out = Join-Path $Workspace $Label
    $harnessArgs = @(
        '--label', $Label,
        '--character', $Character,
        '--encounter', $Encounter,
        '--seed', $Seed,
        '--profile', 'Custom',
        '--beam', "$Beam",
        '--nodes', "$Nodes",
        '--dop', '1',
        '--budget-ms', "$BudgetMilliseconds",
        '--potion-policy', 'Smart',
        '--search-mode', 'Evaluate',
        '--route-policy', $RoutePolicy,
        '--out', $out,
        '--workspace', $Workspace
    )
    & dotnet $HarnessDll @harnessArgs
    if ($LASTEXITCODE -ne 0) {
        throw "U2 harness run failed: $Label route=$RoutePolicy exit=$LASTEXITCODE"
    }

    $path = Join-Path $out 'harness-result.json'
    if (-not (Test-Path -LiteralPath $path)) {
        throw "U2 harness result missing: $path"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
}

$single = Invoke-U2Run -Label 'single' -RoutePolicy 'SinglePlayerFullRoute'
$multiDegenerate = Invoke-U2Run -Label 'multi-degenerate' -RoutePolicy 'MultiplayerLocalCrossTurn'

$singlePolicy = $single.searchPolicy
$multiPolicy = $multiDegenerate.searchPolicy
if ($singlePolicy.UseMultiplayerTeamObjective -ne $false -or
    $multiPolicy.UseMultiplayerTeamObjective -ne $false) {
    throw 'U2 fixture must compare both route policies under the same single-player objective.'
}

$singleActions = @($single.search.planActions)
$multiActions = @($multiDegenerate.search.planActions)
$singleFirst = if ($singleActions.Count -gt 0) { [string]$singleActions[0] } else { '<none>' }
$multiFirst = if ($multiActions.Count -gt 0) { [string]$multiActions[0] } else { '<none>' }

if ($singleFirst -cne $multiFirst) {
    throw "U2 first-action mismatch. single=$singleFirst multi=$multiFirst"
}

$metricNames = @(
    'boundary',
    'projectedBattleHpLost',
    'finalHp',
    'finalEnemyHp',
    'combatEndedTurn',
    'potionCount',
    'onlyDeathRoutes'
)
foreach ($name in $metricNames) {
    $left = $single.search.solverMetrics.$name
    $right = $multiDegenerate.search.solverMetrics.$name
    if ("$left" -cne "$right") {
        throw "U2 terminal-value mismatch at $name. single=$left multi=$right"
    }
}

if ($singleActions.Count -ne $multiActions.Count) {
    throw "U2 fixed-tie-break sequence length mismatch. single=$($singleActions.Count) multi=$($multiActions.Count)"
}
for ($index = 0; $index -lt $singleActions.Count; $index++) {
    if ([string]$singleActions[$index] -cne [string]$multiActions[$index]) {
        throw "U2 fixed-tie-break sequence mismatch at index $index. single=$($singleActions[$index]) multi=$($multiActions[$index])"
    }
}

$result = [ordered]@{
    status = 'PASS'
    seed = $Seed
    character = $Character
    encounter = $Encounter
    beam = $Beam
    nodes = $Nodes
    budgetMilliseconds = $BudgetMilliseconds
    firstAction = $singleFirst
    actionCount = $singleActions.Count
    terminal = [ordered]@{}
}
foreach ($name in $metricNames) {
    $result.terminal[$name] = $single.search.solverMetrics.$name
}
$resultPath = Join-Path $Workspace 'u2-degenerate-equivalence.json'
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $resultPath -Encoding utf8

Write-Output 'U2DegenerateEquivalence PASS: first action + terminal value + fixed-tie-break action sequence are identical'
Write-Output "Evidence: $resultPath"
