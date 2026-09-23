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
        throw "U1 contract missing: $Label"
    }
}

function Assert-Before {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$First,
        [Parameter(Mandatory)][string]$Second,
        [Parameter(Mandatory)][string]$Label
    )
    $firstIndex = $Text.IndexOf($First, [System.StringComparison]::Ordinal)
    $secondIndex = $Text.IndexOf($Second, [System.StringComparison]::Ordinal)
    if ($firstIndex -lt 0 -or $secondIndex -lt 0 -or $firstIndex -ge $secondIndex) {
        throw "U1 ordering contract failed: $Label"
    }
}

$policy = Read-RepoFile 'src/Runtime/MultiplayerSafeExecutePolicy.cs'
$deployment = Read-RepoFile 'src/Runtime/SolverController.Deployment.cs'
$expectedState = Read-RepoFile 'src/Runtime/SolverController.SafeExecutionExpectedState.cs'
$tests = Read-RepoFile 'tools/MultiplayerSafeExecuteChecks/Program.cs'

Assert-Contains $policy 'ExpectedContinuationStateMatched' 'semantic continuation match fact'
Assert-Contains $policy 'ExpectedRemoteStateMatched' 'semantic remote match fact'
Assert-Contains $policy '!facts.ExpectedRemoteStateMatched' 'remote semantic mismatch rejection'
Assert-Contains $policy '!facts.ExpectedContinuationStateMatched' 'predicted semantic mismatch rejection'
Assert-Before $policy '!facts.ExpectedRemoteStateMatched' 'return facts.HasNextAction' 'semantic gate runs before continuation authorization'

Assert-Contains $deployment '"safe_execute_pre_action"' 'fresh observation before every local action'
Assert-Contains $deployment 'U1_PRE_ACTION_PROBE' 'pre-action evidence'
Assert-Before $deployment '"safe_execute_pre_action"' 'TryBeginAction(' 'fresh observation precedes action authorization'
Assert-Contains $deployment 'CaptureExpectedSafeExecutionPostActionAsync(' 'predicted one-action post-state capture'
Assert-Before $deployment 'CaptureExpectedSafeExecutionPostActionAsync(' 'card.TryManualPlay(target)' 'prediction is frozen before native submission'
Assert-Contains $deployment 'CompareSafeExecutionPostAction(' 'settled live/predicted comparison'
Assert-Contains $deployment 'ActionQueueIdle: actionQueueIdle' 'queue state is measured instead of hard-coded'
Assert-Contains $deployment 'legacy_mismatches=' 'historical heuristics remain diagnostic-only'

Assert-Contains $expectedState 'CombatRootSnapshot.Capture(state)' 'fresh detached root capture'
Assert-Contains $expectedState 'ReplayDiagnosticPrefix([action])' 'production replay path'
Assert-Contains $expectedState 'CaptureDiagnosticContinuation(snapshot)' 'production predicted continuation stamp'
Assert-Contains $expectedState 'MultiplayerContinuationRemoteFingerprint.CapturePredicted(' 'predicted teammate semantic fingerprint'
Assert-Contains $expectedState 'ContinuationStamp.CaptureLive(state, state.Players.ToArray())' 'live semantic state comparison'
Assert-Contains $expectedState 'MultiplayerContinuationRemoteFingerprint.CaptureLive(' 'live teammate semantic comparison'
if ($expectedState.Contains('ManualPlay(', [System.StringComparison]::Ordinal) -or
    $expectedState.Contains('OnPlayWrapper(', [System.StringComparison]::Ordinal)) {
    throw 'U1 helper must not implement a second card execution path.'
}

Assert-Contains $tests 'legacy heuristics disagree' 'modeled local chain behavior check'
Assert-Contains $tests 'ExpectedRemoteStateMatched = false' 'remote insertion behavior check'
Assert-Contains $tests 'ExpectedContinuationStateMatched = false' 'simulation mismatch behavior check'

Write-Output 'U1_ACTION_POSTSTATE_CHECKS_PASS semantic-replay/pre-action-probe/fail-closed/legacy-diagnostic'
