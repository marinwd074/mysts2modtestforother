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

function Assert-NotContains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Needle,
        [Parameter(Mandatory)][string]$Label
    )
    if ($Text.Contains($Needle, [System.StringComparison]::Ordinal)) {
        throw "U1 retired gate returned: $Label"
    }
}

$policy = Read-RepoFile 'src/Runtime/MultiplayerSafeExecutePolicy.cs'
$deployment = Read-RepoFile 'src/Runtime/SolverController.Deployment.cs'
$probe = Read-RepoFile 'src/Runtime/MultiplayerClientProbe.cs'

Assert-Contains $policy '!facts.NativeLocalActionCaptured || !facts.ActionQueueIdle' 'native action ownership / queue-settled boundary'
Assert-Contains $policy '!facts.WorldVersionAdvanced || !facts.WorldVersionStable' 'world-version settlement boundary'
Assert-NotContains $policy 'if (!facts.ExpectedRemoteStateMatched)' 'remote semantic hash rejection'
Assert-NotContains $policy 'if (!facts.ExpectedContinuationStateMatched)' 'continuation semantic hash rejection'

Assert-Contains $deployment '"safe_execute_pre_action"' 'fresh observation before every local action'
Assert-Contains $deployment 'WaitForStableSafeExecutionWorldAsync(' 'world-settlement wait'
Assert-Contains $deployment 'ActionQueueIdle: actionQueueIdle' 'queue state is measured'
Assert-NotContains $deployment 'CaptureExpectedSafeExecutionPostActionAsync(' 'per-action prediction replay'
Assert-NotContains $deployment 'CompareSafeExecutionPostAction(' 'post-action semantic replay comparison'
Assert-NotContains $deployment 'before.RemotePublicFingerprint == after.RemotePublicFingerprint' 'remote fingerprint gate'
Assert-NotContains $deployment 'before.LocalFingerprint == after.LocalFingerprint' 'local fingerprint gate'

Assert-NotContains $probe 'StateFingerprint LocalFingerprint,' 'safe boundary local fingerprint field'
Assert-NotContains $probe 'StateFingerprint RemotePublicFingerprint,' 'safe boundary remote fingerprint field'

Write-Output 'U1_ACTION_POSTSTATE_CHECKS_PASS native-action/queue/world-version-only'
