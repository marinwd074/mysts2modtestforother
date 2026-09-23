#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

$manifestPath = Join-Path $sourceRoot 'docs/baseline/p0-baseline.json'
$targetPath = Join-Path $sourceRoot 'build-target.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$target = Get-Content -LiteralPath $targetPath -Raw | ConvertFrom-Json
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

Check ($manifest.schemaVersion -eq 1 -and $manifest.phase -eq 'P0') 'P0 baseline schema and phase are pinned.'
Check ($manifest.target.sts2 -eq $target.game_version) 'P0 STS2 version matches build-target.json.'
Check ($manifest.target.compileSymbol -eq $target.compatibility_symbol) 'P0 compile symbol matches build-target.json.'
Check ($manifest.comparisonProtocol.sameInput -eq $true -and
       $manifest.comparisonProtocol.sameTotalBudget -eq $true -and
       $manifest.comparisonProtocol.teammateForecastCostIncluded -eq $true -and
       $manifest.comparisonProtocol.timeBoundaryInvalidatesSample -eq $true -and
       $manifest.comparisonProtocol.stopRepeatingAfterPass -eq $true) 'P0 comparison protocol keeps equal input/total budget and stop-after-pass rules.'

$sp = @($manifest.workloads | Where-Object id -eq 'SP-REGRESSION')
Check ($sp.Count -eq 1) 'P0 contains exactly one SP-REGRESSION workload.'
$fixtureRelative = [string]$sp[0].fixture
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $sourceRoot '..')).Path
$fixturePath = Join-Path $repoRoot $fixtureRelative
Check (Test-Path -LiteralPath $fixturePath -PathType Leaf) 'SP-REGRESSION fixture exists.'
$fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
Check ($fixture.seed -eq $sp[0].seed -and
       $fixture.characterId -eq $sp[0].character -and
       [int]$fixture.ascension -eq [int]$sp[0].ascension -and
       $fixture.encounterId -eq $sp[0].encounter) 'SP-REGRESSION seed, character, ascension and encounter match the pinned fixture.'
Check ($sp[0].modeOverride -eq 'Search') 'P0 explicitly overrides the historical Deploy fixture to Search mode.'
Check ($sp[0].profile -eq 'Medium' -and
       [int]$sp[0].searchBudgetMilliseconds -eq 5000 -and
       [int]$sp[0].maxDegreeOfParallelism -eq 1 -and
       $sp[0].potionPolicy -eq 'Smart' -and
       $sp[0].fixedSearchBudget -eq $true) 'SP-REGRESSION search policy is Medium/Smart/fixed 5000 ms/DOP1.'
Check ($sp[0].launcherArgs.PerformancePresetForTest -eq 'Medium' -and
       $sp[0].launcherArgs.FixedSearchBudget -eq $true -and
       [int]$sp[0].launcherArgs.SearchBudgetOverrideMilliseconds -eq 5000 -and
       [int]$sp[0].launcherArgs.SearchMaxDegreeOfParallelismForTest -eq 1) 'Machine-readable launcher arguments match the P0 search budget.'

$reuse = @($manifest.workloads | Where-Object id -eq 'MP-JOINT-REUSE')
$mismatch = @($manifest.workloads | Where-Object id -eq 'MP-JOINT-MISMATCH')
Check ($reuse.Count -eq 1 -and $reuse[0].requiredValidatorMode -eq 'Reuse') 'P0 pins the Joint exact-reuse gate.'
Check ($mismatch.Count -eq 1 -and
       $mismatch[0].requiredValidatorMode -eq 'Mismatch' -and
       $mismatch[0].expectedRejectReason -eq 'remote_public_mismatch' -and
       $mismatch[0].expectedClassification -eq 'teammate_prediction_deviation') 'P0 pins the Joint teammate-mismatch gate and expected classification.'

$requiredClasses = @('simulation_error','search_miss_evidence','teammate_prediction_deviation','runtime_state_mismatch','unclassified')
foreach ($class in $requiredClasses) {
    Check ($manifest.failureClasses -contains $class) ("P0 failure class is pinned: {0}" -f $class)
}

Write-Output ("P0_BASELINE_MANIFEST_PASS checks={0}" -f $checks)
