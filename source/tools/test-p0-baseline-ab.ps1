
#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameData,
    [Parameter(Mandatory)][string]$RitsuProps,
    [Parameter(Mandatory)][string]$RitsuRoot,
    [Parameter(Mandatory)][string]$CurrentEvidencePath,
    [string]$BaselineCommit = '380b0801c5882c502d099d704a4bd5d55082e15a',
    [string]$CurrentFixture = (Join-Path $PSScriptRoot 'GeneratedCombatScenarios/regression-necrobinder-elite.json'),
    [string]$Workspace = (Join-Path $PSScriptRoot '../.local/p0-baseline-ab')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$GameData = [IO.Path]::GetFullPath($GameData)
$RitsuProps = [IO.Path]::GetFullPath($RitsuProps)
$RitsuRoot = [IO.Path]::GetFullPath($RitsuRoot)
$CurrentEvidencePath = [IO.Path]::GetFullPath($CurrentEvidencePath)
$CurrentFixture = [IO.Path]::GetFullPath($CurrentFixture)
$Workspace = [IO.Path]::GetFullPath($Workspace)

if (-not (Test-Path -LiteralPath $CurrentEvidencePath)) {
    throw "Current P0/P1 evidence missing: $CurrentEvidencePath"
}
if (-not (Test-Path -LiteralPath $CurrentFixture)) {
    throw "Current repaired P0 fixture missing: $CurrentFixture"
}

Remove-Item -LiteralPath $Workspace -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Workspace -Force | Out-Null
$baselineRoot = Join-Path $Workspace 'baseline-source'
$baselineOut = Join-Path $Workspace 'baseline-run'
$current = Get-Content -LiteralPath $CurrentEvidencePath -Raw | ConvertFrom-Json -Depth 100

# Historical source comparison needs code only. Game/Ritsu binaries come from the already
# verified pinned paths passed into this script, so do not smudge multi-gigabyte LFS content.
$env:GIT_LFS_SKIP_SMUDGE = '1'

git fetch origin $BaselineCommit --depth=1
if ($LASTEXITCODE -ne 0) { throw "git fetch baseline commit failed: $BaselineCommit" }
git worktree add --detach $baselineRoot $BaselineCommit
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed: $BaselineCommit" }

try {
    $baselineFixture = Join-Path $baselineRoot 'source/tools/GeneratedCombatScenarios/regression-necrobinder-elite.json'
    Copy-Item -LiteralPath $CurrentFixture -Destination $baselineFixture -Force
    $fixture = Get-Content -LiteralPath $baselineFixture -Raw | ConvertFrom-Json -Depth 100
    $fixture.mode = 'Search'
    $fixture | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $baselineFixture -Encoding utf8

    # The historical OfflineSearchHarness was a profiling harness and explicitly disabled
    # these two production defaults. P0's documented unattended launcher did not, so restore
    # them before using wall-clock completion as an A/B criterion.
    $baselineModRuntime = Join-Path $baselineRoot 'source/tools/OfflineSearchHarness/ModRuntime.cs'
    $baselineModText = Get-Content -LiteralPath $baselineModRuntime -Raw
    if ($baselineModText -notmatch 'EnableNoGcRegion = false' -or
        $baselineModText -notmatch 'StopAtAcceptableBattleHpLoss = false') {
        throw 'Historical harness default-setting patch targets were not found.'
    }
    $baselineModText = $baselineModText.Replace('EnableNoGcRegion = false', 'EnableNoGcRegion = true')
    $baselineModText = $baselineModText.Replace('StopAtAcceptableBattleHpLoss = false', 'StopAtAcceptableBattleHpLoss = true')
    Set-Content -LiteralPath $baselineModRuntime -Value $baselineModText -Encoding utf8

    # The frozen P0 commit predates two namespace-only compile fixes for the pinned 0.107.1
    # model/simulation types. Apply only those using fixes; no search implementation is copied.
    $fingerprintPath = Join-Path $baselineRoot 'source/src/Search/ShadowFutureStateFingerprint.cs'
    $fingerprintText = Get-Content -LiteralPath $fingerprintPath -Raw
    if ($fingerprintText -notmatch 'using MegaCrit\.Sts2\.Core\.Models\.Orbs;' -or
        $fingerprintText -match 'using CombatSolver\.Engine\.Common;') {
        throw 'Historical ShadowFutureStateFingerprint compile-fix targets were not found as expected.'
    }
    $fingerprintText = $fingerprintText.Replace(
        'using MegaCrit.Sts2.Core.Models.Orbs;',
        'using MegaCrit.Sts2.Core.Models;')
    $fingerprintText = $fingerprintText.Replace(
        'using CombatSolver.Engine.InCombat.Mirrors.Orbs;',
        "using CombatSolver.Engine.Common;`r`nusing CombatSolver.Engine.InCombat.Mirrors.Orbs;")
    Set-Content -LiteralPath $fingerprintPath -Value $fingerprintText -Encoding utf8

    # The historical OfflineSearchHarness calls test-only scenario/session types, while the
    # production csproj at this exact commit already excluded src/Testing. For the temporary
    # A/B build only, include those original historical test sources. Search/runtime production
    # sources remain frozen at the baseline commit.
    $baselineProject = Join-Path $baselineRoot 'source/CombatSolver.csproj'
    $projectText = Get-Content -LiteralPath $baselineProject -Raw
    $testingExclude = '    <Compile Remove="src/Testing/**/*.cs" />'
    if (-not $projectText.Contains($testingExclude, [StringComparison]::Ordinal)) {
        throw 'Historical CombatSolver test-source exclusion was not found.'
    }
    $projectText = $projectText.Replace(
        $testingExclude,
        '    <Compile Remove="src/Runtime/UnattendedTestRunner.Compatibility.cs" />' + [Environment]::NewLine +
        '    <Compile Remove="src/Runtime/TestingBridge/UnattendedAsyncActivityTracker.cs" />')
    Set-Content -LiteralPath $baselineProject -Value $projectText -Encoding utf8

    $requestPath = Join-Path $Workspace 'baseline-request.json'
    [ordered]@{
        schemaVersion = 1
        runId = 'p0-baseline-ab'
        scenarioId = 'P0-BASELINE-AB'
        generatedScenarioPath = $baselineFixture
    } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $requestPath -Encoding utf8

    $buildMainArgs = @(
        'build', (Join-Path $baselineRoot 'source/CombatSolver.csproj'),
        '-c', 'Release',
        '-p:CopyModOnBuild=false',
        "-p:Sts2DataDir=$GameData",
        "-p:RitsuLibReferencesProps=$RitsuProps",
        '-p:RitsuLibReferenceTarget=0.107.1',
        '--nologo'
    )
    & dotnet @buildMainArgs
    if ($LASTEXITCODE -ne 0) { throw 'Baseline CombatSolver build failed.' }

    $baselineDll = Get-ChildItem -LiteralPath (Join-Path $baselineRoot 'source') -Recurse -Filter 'CombatSolver.dll' |
        Where-Object { $_.FullName -match '[\\/]Release[\\/]' } |
        Sort-Object { if ($_.FullName -match '[\\/]\.godot[\\/]') { 0 } else { 1 } }, FullName |
        Select-Object -First 1
    if ($null -eq $baselineDll) { throw 'Baseline CombatSolver.dll not found after build.' }

    $buildHarnessArgs = @(
        'build', (Join-Path $baselineRoot 'source/tools/OfflineSearchHarness/OfflineSearchHarness.csproj'),
        '-c', 'Release',
        "-p:Sts2DataDir=$GameData",
        "-p:RitsuWorkshopRoot=$RitsuRoot",
        "-p:RitsuLibReferencesProps=$RitsuProps",
        '-p:RitsuLibReferenceTarget=0.107.1',
        "-p:CombatSolverDll=$($baselineDll.FullName)",
        '--nologo'
    )
    & dotnet @buildHarnessArgs
    if ($LASTEXITCODE -ne 0) { throw 'Baseline OfflineSearchHarness build failed.' }

    $baselineHarness = Join-Path $baselineRoot 'source/tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll'
    if (-not (Test-Path -LiteralPath $baselineHarness)) {
        throw "Baseline OfflineSearchHarness.dll not found: $baselineHarness"
    }

    $runArgs = @(
        '--request', $requestPath,
        '--label', 'P0-BASELINE-COMMIT',
        '--profile', 'Medium',
        '--dop', '1',
        '--budget-ms', '5000',
        '--potion-policy', 'Smart',
        '--search-mode', 'Coordinator',
        '--use-portfolio',
        '--out', $baselineOut,
        '--workspace', $Workspace
    )
    & dotnet $baselineHarness @runArgs
    $baselineExit = $LASTEXITCODE

    $baselineResultPath = Join-Path $baselineOut 'result.json'
    $baselineHarnessPath = Join-Path $baselineOut 'harness-result.json'
    if (-not (Test-Path -LiteralPath $baselineResultPath) -or -not (Test-Path -LiteralPath $baselineHarnessPath)) {
        throw "Baseline harness did not produce result artifacts. exit=$baselineExit"
    }

    $baselineResult = Get-Content -LiteralPath $baselineResultPath -Raw | ConvertFrom-Json -Depth 100
    $baselineHarnessResult = Get-Content -LiteralPath $baselineHarnessPath -Raw | ConvertFrom-Json -Depth 100
    $baselineBoundary = [string]$baselineHarnessResult.search.solverMetrics.boundary
    $baselineTimeBoundary = [bool]$baselineResult.timeBoundaryObserved -or $baselineBoundary -eq 'TimeLimit'
    $currentBoundary = [string]$current.p0.spRegression.Boundary
    $currentTimeBoundary = $currentBoundary -eq 'TimeLimit'
    $currentValid = [bool]$current.p0.spRegression.Pass -and -not $currentTimeBoundary
    $baselineValid = $baselineExit -eq 0 -and $baselineResult.status -eq 'Passed' -and -not $baselineTimeBoundary

    $classification = if ($baselineValid -and -not $currentValid) {
        'CURRENT_REGRESSION_CANDIDATE'
    } elseif (-not $baselineValid -and -not $currentValid -and $baselineTimeBoundary -and $currentTimeBoundary) {
        'INCONCLUSIVE_TIME_BOUNDARY'
    } elseif (-not $baselineValid -and $currentValid) {
        'CURRENT_VALID_BASELINE_OLD_INVALID'
    } elseif ($baselineValid -and $currentValid) {
        'BOTH_VALID'
    } else {
        'INFRASTRUCTURE_OR_NON_TIME_FAILURE'
    }

    $summary = [ordered]@{
        status = if ($classification -eq 'CURRENT_REGRESSION_CANDIDATE' -or $classification -eq 'INFRASTRUCTURE_OR_NON_TIME_FAILURE') { 'FAIL' } else { 'PASS' }
        classification = $classification
        pinnedTarget = '0.107.1'
        baselineCommit = $BaselineCommit
        baseline = [ordered]@{
            exitCode = $baselineExit
            status = $baselineResult.status
            boundary = $baselineBoundary
            timeBoundary = $baselineTimeBoundary
            valid = $baselineValid
            firstAction = @($baselineHarnessResult.search.planActions)[0]
            solverMetrics = $baselineHarnessResult.search.solverMetrics
            wallSeconds = $baselineResult.wallSeconds
        }
        current = [ordered]@{
            boundary = $currentBoundary
            timeBoundary = $currentTimeBoundary
            valid = $currentValid
            firstAction = $current.p0.spRegression.FirstAction
            projectedBattleHpLost = $current.p0.spRegression.ProjectedBattleHpLost
            finalHp = $current.p0.spRegression.FinalHp
            finalEnemyHp = $current.p0.spRegression.FinalEnemyHp
            combatEndedTurn = $current.p0.spRegression.CombatEndedTurn
            expandedNodes = $current.p0.spRegression.ExpandedNodes
        }
    }

    $summaryPath = Join-Path $Workspace 'p0-baseline-ab.json'
    $summary | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Get-Content -LiteralPath $summaryPath

    if ($summary.status -ne 'PASS') { throw "P0 baseline A/B failed: $classification" }
    Write-Output "P0_BASELINE_AB $classification"
}
finally {
    $repoRoot = git rev-parse --show-toplevel
    Push-Location $repoRoot
    try { git worktree remove --force $baselineRoot 2>$null } finally { Pop-Location }
}
