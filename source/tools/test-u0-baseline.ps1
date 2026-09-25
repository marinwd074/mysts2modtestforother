#requires -Version 7.0

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

function Read-RepoFile {
    param([Parameter(Mandatory)][string]$RelativePath)
    return [System.IO.File]::ReadAllText((Join-Path $repositoryRoot $RelativePath))
}

function Assert-Contains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Needle,
        [Parameter(Mandatory)][string]$Label
    )
    if (-not $Text.Contains($Needle, [System.StringComparison]::Ordinal)) {
        throw "U0 contract missing: $Label"
    }
}

$searchDiagnostics = Read-RepoFile 'src/Search/SearchDiagnosticsSink.cs'
$finalOrdering = Read-RepoFile 'src/Search/CombatBeamSolver.FinalPlanOrdering.cs'
$deployment = Read-RepoFile 'src/Runtime/SolverController.Deployment.cs'
$fixture = Read-RepoFile 'tools/OfflineSearchHarness/U0BaselineFixture.cs'

Assert-Contains $searchDiagnostics 'Generated,' 'model transition Generated stage'
Assert-Contains $searchDiagnostics 'Expanded,' 'model transition Expanded stage'
Assert-Contains $searchDiagnostics 'ActionAdmitted,' 'model transition action admission stage'

Assert-Contains $finalOrdering '[CombatSolver/U0] FINAL_CANDIDATE ' 'final candidate trace'
Assert-Contains $finalOrdering '[CombatSolver/U0] FINAL_SELECTION ' 'final selection trace'
Assert-Contains $finalOrdering 'scenario_rerank=' 'scenario rerank selection evidence'
Assert-Contains $finalOrdering 'chance_rerank=' 'chance rerank selection evidence'

Assert-Contains $deployment '[CombatSolver/Test] DEPLOY_ACTION ' 'actual deployment trace'
Assert-Contains $deployment 'NATIVE_ACTION_CAPTURED ' 'native action capture trace'
Assert-Contains $deployment '[CombatSolver/Test] DEPLOY_ACTION_COMPLETE ' 'actual action completion trace'
Assert-Contains $deployment 'MP2B_ACTION_RECONCILED ' 'post-action reconciliation trace'

Assert-Contains $fixture 'NoTeammateEvents' 'no-teammate-event fixture'
Assert-Contains $fixture 'ShadowFutureStateFingerprint.Capture(' 'no-event modeled-state equality'
Assert-Contains $fixture 'ReplayFixedTeammateScript(' 'fixed teammate script fixture'
Assert-Contains $fixture 'ShadowTeammatePlanner.ReplayForecastActions(' 'shared production teammate replay path'

if ($fixture.Contains('TryPlayCandidateInPlace(', [System.StringComparison]::Ordinal) -or
    $fixture.Contains('CardModel', [System.StringComparison]::Ordinal)) {
    throw 'U0 fixture must not implement a second card-effect path.'
}

Write-Output 'U0BaselineChecks PASS: model transitions + candidates + selection + minimal execution reconciliation + deterministic fixtures'
