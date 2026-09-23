#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameData,
    [Parameter(Mandatory)][string]$RitsuProps,
    [Parameter(Mandatory)][string]$RitsuRoot,
    [string]$Workspace = (Join-Path $env:TEMP 'p0-historical-baseline'),
    [string]$BaselineCommit = '380b0801c5882c502d099d704a4bd5d55082e15a'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Workspace = [System.IO.Path]::GetFullPath($Workspace)
$GameData = [System.IO.Path]::GetFullPath($GameData)
$RitsuProps = [System.IO.Path]::GetFullPath($RitsuProps)
$RitsuRoot = [System.IO.Path]::GetFullPath($RitsuRoot)

Remove-Item -LiteralPath $Workspace -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Workspace -Force | Out-Null

$baseline = Join-Path $Workspace 'baseline'
$env:GIT_LFS_SKIP_SMUDGE = '1'
git clone --quiet --no-checkout https://github.com/marinwd074/mysts2modtestforother.git $baseline
if ($LASTEXITCODE -ne 0) { throw 'Failed to clone repository for P0 historical baseline.' }
git -C $baseline checkout --quiet --detach $BaselineCommit
if ($LASTEXITCODE -ne 0) { throw "Failed to checkout P0 baseline commit $BaselineCommit." }

$fixture = Join-Path $baseline 'source/tools/GeneratedCombatScenarios/regression-necrobinder-elite.json'
$fixtureText = Get-Content -LiteralPath $fixture -Raw
if ($fixtureText -notmatch '"SOULBOUND"') {
    throw 'Historical P0 fixture no longer contains expected SOULBOUND input.'
}
$fixtureText = $fixtureText.Replace('"SOULBOUND"', '"DEATH_MARCH"')
$fixtureText = $fixtureText.Replace('"mode": "Deploy"', '"mode": "Search"')
Set-Content -LiteralPath $fixture -Value $fixtureText -Encoding utf8

# The old OfflineSearchHarness intentionally disabled two production settings for profiling.
# P0's documented launcher did not override either setting, so restore the real unattended defaults
# before comparing the historical solver under a wall-clock budget.
$modRuntime = Join-Path $baseline 'source/tools/OfflineSearchHarness/ModRuntime.cs'
$modText = Get-Content -LiteralPath $modRuntime -Raw
if ($modText -notmatch 'EnableNoGcRegion = false' -or
    $modText -notmatch 'StopAtAcceptableBattleHpLoss = false') {
    throw 'Historical OfflineSearchHarness settings patch targets were not found.'
}
$modText = $modText.Replace('EnableNoGcRegion = false', 'EnableNoGcRegion = true')
$modText = $modText.Replace('StopAtAcceptableBattleHpLoss = false', 'StopAtAcceptableBattleHpLoss = true')
Set-Content -LiteralPath $modRuntime -Value $modText -Encoding utf8

$buildArgs = @(
    '-c', 'Release',
    '-p:CopyModOnBuild=false',
    "-p:Sts2DataDir=$GameData",
    "-p:RitsuLibReferencesProps=$RitsuProps",
    "-p:RitsuWorkshopRoot=$RitsuRoot",
    '-p:RitsuLibReferenceTarget=0.107.1',
    '--nologo'
)
dotnet build (Join-Path $baseline 'source/CombatSolver.csproj') @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Historical CombatSolver Release build failed.' }

$harnessArgs = @(
    '-c', 'Release',
    "-p:Sts2DataDir=$GameData",
    "-p:RitsuLibReferencesProps=$RitsuProps",
    "-p:RitsuWorkshopRoot=$RitsuRoot",
    '-p:RitsuLibReferenceTarget=0.107.1',
    '--nologo'
)
dotnet build (Join-Path $baseline 'source/tools/OfflineSearchHarness/OfflineSearchHarness.csproj') @harnessArgs
if ($LASTEXITCODE -ne 0) { throw 'Historical OfflineSearchHarness build failed.' }

$requestPath = Join-Path $Workspace 'request.json'
@{
    schemaVersion = 1
    runId = 'P0-HISTORICAL-BASELINE'
    scenarioId = 'P0-HISTORICAL-BASELINE'
    generatedScenarioPath = $fixture
    evidenceDirectory = (Join-Path $Workspace 'evidence')
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $requestPath -Encoding utf8

$out = Join-Path $Workspace 'run'
$harnessDll = Join-Path $baseline 'source/tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll'
$runArgs = @(
    '--request', $requestPath,
    '--label', 'p0-historical',
    '--profile', 'Medium',
    '--dop', '1',
    '--budget-ms', '5000',
    '--potion-policy', 'Smart',
    '--search-mode', 'Coordinator',
    '--use-portfolio',
    '--out', $out,
    '--workspace', $Workspace
)
& dotnet $harnessDll @runArgs
$runExit = $LASTEXITCODE

$resultPath = Join-Path $out 'result.json'
if (-not (Test-Path -LiteralPath $resultPath)) {
    throw "Historical harness produced no result.json (exit=$runExit)."
}
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -Depth 100
$metrics = $result.solverMetrics
$timeBoundary = [bool]$result.timeBoundaryObserved -or "$($metrics.boundary)" -eq 'TimeLimit'

$evidence = [ordered]@{
    baselineCommit = $BaselineCommit
    pinnedTarget = '0.107.1'
    fixtureRepair = 'SOULBOUND->DEATH_MARCH'
    searchMode = 'Coordinator'
    profile = 'Medium'
    potionPolicy = 'Smart'
    fixedBudgetMilliseconds = 5000
    maxDegreeOfParallelism = 1
    beamWidthPortfolio = $true
    historicalHarnessExitCode = $runExit
    status = if ($runExit -eq 0) { 'RAN' } else { 'HARNESS_FAILED' }
    timeBoundaryObserved = $timeBoundary
    boundary = $metrics.boundary
    projectedBattleHpLost = $metrics.projectedBattleHpLost
    finalHp = $metrics.finalHp
    finalEnemyHp = $metrics.finalEnemyHp
    combatEndedTurn = $metrics.combatEndedTurn
    totalExpanded = $metrics.totalExpanded
    totalTransitions = $metrics.totalTransitions
    totalChoiceBranches = $metrics.totalChoiceBranches
}
$evidencePath = Join-Path $Workspace 'p0-historical-baseline-evidence.json'
$evidence | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $evidencePath -Encoding utf8
Get-Content -LiteralPath $evidencePath

if ($runExit -ne 0) {
    throw "Historical P0 harness failed with exit code $runExit."
}
