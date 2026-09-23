#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameData,
    [Parameter(Mandatory)][string]$RitsuProps,
    [Parameter(Mandatory)][string]$RitsuRoot,
    [Parameter(Mandatory)][string]$CurrentEvidencePath,
    [string]$BaselineCommit = '380b0801c5882c502d099d704a4bd5d55082e15a',
    [string]$Workspace = (Join-Path $PSScriptRoot '../.local/p0-baseline-ab')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$GameData = [IO.Path]::GetFullPath($GameData)
$RitsuProps = [IO.Path]::GetFullPath($RitsuProps)
$RitsuRoot = [IO.Path]::GetFullPath($RitsuRoot)
$CurrentEvidencePath = [IO.Path]::GetFullPath($CurrentEvidencePath)
$Workspace = [IO.Path]::GetFullPath($Workspace)
$repoRoot = (git rev-parse --show-toplevel).Trim()

if (-not (Test-Path -LiteralPath $CurrentEvidencePath)) {
    throw "Current P0/P1 evidence missing: $CurrentEvidencePath"
}

Remove-Item -LiteralPath $Workspace -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Workspace -Force | Out-Null
$baselineRoot = Join-Path $Workspace 'baseline-source'
$baselineOut = Join-Path $Workspace 'baseline-run'
$current = Get-Content -LiteralPath $CurrentEvidencePath -Raw | ConvertFrom-Json -Depth 100

# Historical comparison needs only the frozen production source. Pinned game/Ritsu binaries
# are supplied by the current verified runner, so never smudge historical LFS content.
$env:GIT_LFS_SKIP_SMUDGE = '1'
git fetch origin $BaselineCommit --depth=1
if ($LASTEXITCODE -ne 0) { throw "git fetch baseline commit failed: $BaselineCommit" }
git worktree add --detach $baselineRoot $BaselineCommit
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed: $BaselineCommit" }

try {
    # Frozen P0 source predates two namespace-only fixes needed to compile against pinned
    # 0.107.1 metadata. No search implementation is copied from a later revision.
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
        'using CombatSolver.Engine.Common;' + [Environment]::NewLine +
        'using CombatSolver.Engine.InCombat.Mirrors.Orbs;')
    Set-Content -LiteralPath $fingerprintPath -Value $fingerprintText -Encoding utf8

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
    if ($LASTEXITCODE -ne 0) { throw 'Frozen production CombatSolver build failed.' }

    $baselineDll = Get-ChildItem -LiteralPath (Join-Path $baselineRoot 'source') -Recurse -Filter 'CombatSolver.dll' |
        Where-Object { $_.FullName -match '[\\/]Release[\\/]' } |
        Sort-Object { if ($_.FullName -match '[\\/]\.godot[\\/]') { 0 } else { 1 } }, FullName |
        Select-Object -First 1
    if ($null -eq $baselineDll) { throw 'Frozen production CombatSolver.dll not found after build.' }

    # Compile the current pinned bootstrap/fixture driver against the frozen production DLL.
    # Its AssemblyName is OfflineSearchHarness because that is the friend assembly explicitly
    # granted internal access by the frozen production project.
    $historicalHarnessProject = Join-Path $repoRoot 'source/tools/P0HistoricalPinnedHarness/P0HistoricalPinnedHarness.csproj'
    $buildHarnessArgs = @(
        'build', $historicalHarnessProject,
        '-c', 'Release',
        "-p:Sts2DataDir=$GameData",
        "-p:RitsuWorkshopRoot=$RitsuRoot",
        "-p:RitsuLibReferencesProps=$RitsuProps",
        '-p:RitsuLibReferenceTarget=0.107.1',
        "-p:CombatSolverDll=$($baselineDll.FullName)",
        '--nologo'
    )
    & dotnet @buildHarnessArgs
    if ($LASTEXITCODE -ne 0) { throw 'P0 historical production-DLL harness build failed.' }

    $harnessDll = Join-Path $repoRoot 'source/tools/P0HistoricalPinnedHarness/bin/Release/net9.0/OfflineSearchHarness.dll'
    if (-not (Test-Path -LiteralPath $harnessDll)) {
        throw "Historical P0 harness not found: $harnessDll"
    }

    & dotnet $harnessDll --out $baselineOut
    $baselineExit = $LASTEXITCODE

    $baselineEvidencePath = Join-Path $baselineOut 'p0-historical-pinned-evidence.json'
    if ($baselineExit -ne 0 -or -not (Test-Path -LiteralPath $baselineEvidencePath)) {
        throw "Historical production-DLL harness failed. exit=$baselineExit evidence=$baselineEvidencePath"
    }

    $baseline = Get-Content -LiteralPath $baselineEvidencePath -Raw | ConvertFrom-Json -Depth 100

    # Refuse to classify if the two runs are not using the same P0 search policy.
    $policyPairs = @(
        @('preset', [string]$baseline.settings.preset, [string]$current.settings.preset),
        @('profileBeamWidth', [string]$baseline.settings.profileBeamWidth, [string]$current.settings.profileBeamWidth),
        @('profileMaxExpandedNodes', [string]$baseline.settings.profileMaxExpandedNodes, [string]$current.settings.profileMaxExpandedNodes),
        @('requestBudgetMilliseconds', [string]$baseline.settings.requestBudgetMilliseconds, [string]$current.settings.requestBudgetMilliseconds),
        @('MaxDegreeOfParallelism', [string]$baseline.settings.MaxDegreeOfParallelism, [string]$current.settings.MaxDegreeOfParallelism),
        @('UseBeamWidthPortfolio', [string]$baseline.settings.UseBeamWidthPortfolio, [string]$current.settings.UseBeamWidthPortfolio),
        @('UseNoveltyPortfolio', [string]$baseline.settings.UseNoveltyPortfolio, [string]$current.settings.UseNoveltyPortfolio),
        @('StopAtAcceptableBattleHpLoss', [string]$baseline.settings.StopAtAcceptableBattleHpLoss, [string]$current.settings.StopAtAcceptableBattleHpLoss),
        @('potionPolicy', [string]$baseline.settings.potionPolicy, [string]$current.settings.potionPolicy)
    )
    foreach ($pair in $policyPairs) {
        if ($pair[1] -ne $pair[2]) {
            throw "P0 A/B policy mismatch $($pair[0]): baseline=$($pair[1]) current=$($pair[2])"
        }
    }

    $baselineBoundary = [string]$baseline.search.boundary
    $currentBoundary = [string]$current.p0.spRegression.Boundary
    $baselineFixedWork = $baseline.fixedWork
    $currentFixedWork = $current.p0.fixedWork
    if ($null -eq $baselineFixedWork -or $null -eq $currentFixedWork) {
        throw 'P0 fixed-work evidence is missing from baseline or current run.'
    }
    if ([int]$baseline.settings.fixedWorkNodeBudget -ne [int]$current.settings.p0FixedWorkNodeBudget) {
        throw "P0 fixed-work node budget mismatch: baseline=$($baseline.settings.fixedWorkNodeBudget) current=$($current.settings.p0FixedWorkNodeBudget)"
    }
    if (-not [bool]$baselineFixedWork.pass -or -not [bool]$currentFixedWork.Pass) {
        throw 'P0 fixed-work probe did not produce comparable routes.'
    }
    if ([string]$baselineFixedWork.boundary -eq 'TimeLimit' -or [string]$currentFixedWork.Boundary -eq 'TimeLimit') {
        throw 'P0 fixed-work probe unexpectedly hit a wall-clock TimeLimit.'
    }

    $sameFixedWorkObservedKeys =
        [int]$baselineFixedWork.projectedBattleHpLost -eq [int]$currentFixedWork.ProjectedBattleHpLost -and
        [int]$baselineFixedWork.finalHp -eq [int]$currentFixedWork.FinalHp -and
        [int]$baselineFixedWork.finalEnemyHp -eq [int]$currentFixedWork.FinalEnemyHp
    $baselineObservedDominance =
        [int]$baselineFixedWork.projectedBattleHpLost -le [int]$currentFixedWork.ProjectedBattleHpLost -and
        [int]$baselineFixedWork.finalHp -ge [int]$currentFixedWork.FinalHp -and
        [int]$baselineFixedWork.finalEnemyHp -le [int]$currentFixedWork.FinalEnemyHp -and
        -not $sameFixedWorkObservedKeys
    $currentObservedDominance =
        [int]$currentFixedWork.ProjectedBattleHpLost -le [int]$baselineFixedWork.projectedBattleHpLost -and
        [int]$currentFixedWork.FinalHp -ge [int]$baselineFixedWork.finalHp -and
        [int]$currentFixedWork.FinalEnemyHp -le [int]$baselineFixedWork.finalEnemyHp -and
        -not $sameFixedWorkObservedKeys
    $p0FixedWorkClassification = if ($sameFixedWorkObservedKeys) {
        'OBSERVED_EQUIVALENT'
    } elseif ($baselineObservedDominance) {
        'BASELINE_OBSERVED_DOMINANCE'
    } elseif ($currentObservedDominance) {
        'CURRENT_OBSERVED_DOMINANCE'
    } else {
        'MIXED_OBSERVED_KEYS'
    }
    $baselineTimeBoundary = $baselineBoundary -eq 'TimeLimit'
    $currentTimeBoundary = $currentBoundary -eq 'TimeLimit'
    $baselineValid = [bool]$baseline.search.pass -and -not $baselineTimeBoundary
    $currentValid = [bool]$current.p0.spRegression.Pass -and -not $currentTimeBoundary

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

    if (-not [bool]$current.p0.joint.Pass) {
        throw "P0 Joint continuation gate failed: $($current.p0.joint | ConvertTo-Json -Compress -Depth 20)"
    }

    if (-not [bool]$current.p1.AdaptiveFastBeatsSlowContract -or
        -not [bool]$current.p1.AllPlayersAliveHardBoundaryContract) {
        throw 'P1 objective math contract failed in pinned runtime evidence.'
    }

    $adaptiveProperty = $current.p1.PSObject.Properties['Adaptive']
    $minimizeProperty = $current.p1.PSObject.Properties['MinimizeTeamLoss']
    $adaptiveFixedWorkProperty = $current.p1.PSObject.Properties['AdaptiveFixedWork']
    $minimizeFixedWorkProperty = $current.p1.PSObject.Properties['MinimizeTeamLossFixedWork']
    if ($null -eq $adaptiveProperty -or $null -eq $minimizeProperty -or
        $null -eq $adaptiveFixedWorkProperty -or $null -eq $minimizeFixedWorkProperty) {
        throw 'P1 runtime evidence is missing one or more timed/fixed-work strategy results.'
    }
    $adaptive = $adaptiveProperty.Value
    $minimize = $minimizeProperty.Value
    $adaptiveFixedWork = $adaptiveFixedWorkProperty.Value
    $minimizeFixedWork = $minimizeFixedWorkProperty.Value
    $adaptiveBoundary = [string]$adaptive.Boundary
    $minimizeBoundary = [string]$minimize.Boundary

    $adaptiveTimeLimitedVictory =
        $adaptiveBoundary -eq 'TimeLimit' -and
        @($adaptive.Actions).Count -gt 0 -and
        [bool]$adaptive.AllPlayersAlive -and
        [int]$adaptive.FinalEnemyHp -eq 0
    $minimizeTimeLimitedVictory =
        $minimizeBoundary -eq 'TimeLimit' -and
        @($minimize.Actions).Count -gt 0 -and
        [bool]$minimize.AllPlayersAlive -and
        [int]$minimize.FinalEnemyHp -eq 0
    $p1FixedWorkPass =
        [bool]$current.p1.FixedWorkSemanticPass -and
        [bool]$adaptiveFixedWork.Pass -and
        [bool]$minimizeFixedWork.Pass -and
        [string]$adaptiveFixedWork.Boundary -ne 'TimeLimit' -and
        [string]$minimizeFixedWork.Boundary -ne 'TimeLimit' -and
        [bool]$adaptiveFixedWork.AllPlayersAlive -and
        [bool]$minimizeFixedWork.AllPlayersAlive -and
        [int]$adaptiveFixedWork.FinalEnemyHp -eq 0 -and
        [int]$minimizeFixedWork.FinalEnemyHp -eq 0

    $p1RuntimeClassification = if ([bool]$current.p1.Pass) {
        'PASS'
    } elseif ($adaptiveTimeLimitedVictory -and $minimizeTimeLimitedVictory -and $p1FixedWorkPass) {
        'FIXED_WORK_PASS_TIME_BOUNDARY'
    } elseif ($adaptiveTimeLimitedVictory -and $minimizeTimeLimitedVictory) {
        'INCONCLUSIVE_TIME_BOUNDARY'
    } else {
        throw "P1 runtime failure is not a pure valid-victory TimeLimit boundary. adaptive=$adaptiveBoundary minimize=$minimizeBoundary"
    }

    $summary = [ordered]@{
        status = if ($classification -in @('CURRENT_REGRESSION_CANDIDATE', 'INFRASTRUCTURE_OR_NON_TIME_FAILURE')) { 'FAIL' } else { 'PASS' }
        classification = $classification
        pinnedTarget = '0.107.1'
        baselineCommit = $BaselineCommit
        p0FixedWork = [ordered]@{
            classification = $p0FixedWorkClassification
            nodeBudget = [int]$current.settings.p0FixedWorkNodeBudget
            baselineBoundary = [string]$baselineFixedWork.boundary
            baselineFirstAction = [string]$baselineFixedWork.firstAction
            baselineProjectedBattleHpLost = [int]$baselineFixedWork.projectedBattleHpLost
            baselineFinalHp = [int]$baselineFixedWork.finalHp
            baselineFinalEnemyHp = [int]$baselineFixedWork.finalEnemyHp
            baselineExpandedNodes = [long]$baselineFixedWork.expandedNodes
            currentBoundary = [string]$currentFixedWork.Boundary
            currentFirstAction = [string]$currentFixedWork.FirstAction
            currentProjectedBattleHpLost = [int]$currentFixedWork.ProjectedBattleHpLost
            currentFinalHp = [int]$currentFixedWork.FinalHp
            currentFinalEnemyHp = [int]$currentFixedWork.FinalEnemyHp
            currentExpandedNodes = [long]$currentFixedWork.ExpandedNodes
        }
        p0Joint = [ordered]@{
            status = 'PASS'
            exactReuse = [bool]$current.p0.joint.ExactReuse
            exactReason = [string]$current.p0.joint.ExactReason
            mismatchRejected = [bool]$current.p0.joint.MismatchRejected
            mismatchReason = [string]$current.p0.joint.MismatchReason
        }
        p1 = [ordered]@{
            objectiveContracts = 'PASS'
            runtimeClassification = $p1RuntimeClassification
            adaptiveBoundary = $adaptiveBoundary
            adaptiveFinalEnemyHp = [int]$adaptive.FinalEnemyHp
            adaptiveAllPlayersAlive = [bool]$adaptive.AllPlayersAlive
            minimizeBoundary = $minimizeBoundary
            minimizeFinalEnemyHp = [int]$minimize.FinalEnemyHp
            minimizeAllPlayersAlive = [bool]$minimize.AllPlayersAlive
            fixedWorkSemanticPass = $p1FixedWorkPass
            fixedWorkNodeBudget = [int]$current.settings.p1FixedWorkNodeBudget
            adaptiveFixedWorkBoundary = [string]$adaptiveFixedWork.Boundary
            adaptiveFixedWorkFinalEnemyHp = [int]$adaptiveFixedWork.FinalEnemyHp
            adaptiveFixedWorkExpandedNodes = [long]$adaptiveFixedWork.ExpandedNodes
            minimizeFixedWorkBoundary = [string]$minimizeFixedWork.Boundary
            minimizeFixedWorkFinalEnemyHp = [int]$minimizeFixedWork.FinalEnemyHp
            minimizeFixedWorkExpandedNodes = [long]$minimizeFixedWork.ExpandedNodes
        }
        baseline = [ordered]@{
            boundary = $baselineBoundary
            timeBoundary = $baselineTimeBoundary
            valid = $baselineValid
            firstAction = $baseline.search.firstAction
            projectedBattleHpLost = $baseline.search.projectedBattleHpLost
            finalHp = $baseline.search.finalHp
            finalEnemyHp = $baseline.search.finalEnemyHp
            expandedNodes = $baseline.search.expandedNodes
            portfolioMembers = $baseline.search.portfolioMembers
        }
        current = [ordered]@{
            boundary = $currentBoundary
            timeBoundary = $currentTimeBoundary
            valid = $currentValid
            firstAction = $current.p0.spRegression.FirstAction
            projectedBattleHpLost = $current.p0.spRegression.ProjectedBattleHpLost
            finalHp = $current.p0.spRegression.FinalHp
            finalEnemyHp = $current.p0.spRegression.FinalEnemyHp
            expandedNodes = $current.p0.spRegression.ExpandedNodes
            portfolioMembers = $current.p0.spRegression.PortfolioMembers
        }
    }

    $summaryPath = Join-Path $Workspace 'p0-baseline-ab.json'
    $summary | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Get-Content -LiteralPath $summaryPath

    if ($summary.status -ne 'PASS') {
        throw "P0 baseline A/B failed: $classification"
    }
    Write-Output "P0_BASELINE_AB $classification"
    Write-Output "P0_FIXED_WORK $p0FixedWorkClassification"
    Write-Output "P0_JOINT PASS"
    Write-Output "P1_OBJECTIVE_CONTRACTS PASS"
    Write-Output "P1_RUNTIME $p1RuntimeClassification"
}
finally {
    Push-Location $repoRoot
    try { git worktree remove --force $baselineRoot 2>$null } finally { Pop-Location }
}
