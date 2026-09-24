#requires -Version 7.0

[CmdletBinding()]
param(
    [switch]$NoRestore,
    [switch]$SkipPython
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-PowerShellContract {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Script,
        [string[]]$Arguments = @()
    )

    Write-Output "RUN: $Name"
    & pwsh -NoLogo -NoProfile -File (Join-Path $repositoryRoot $Script) @Arguments
    $exitCode = $LASTEXITCODE
    $results.Add([pscustomobject]@{ Name = $Name; Status = if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' } })
}

function Invoke-DotnetContract {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Project,
        [string[]]$Arguments = @()
    )

    Write-Output "RUN: $Name"
    $dotnetArguments = @('run', '--project', (Join-Path $repositoryRoot $Project), '-c', 'Release')
    if ($NoRestore) {
        $dotnetArguments += '--no-restore'
    }
    if ($Arguments.Count -gt 0) {
        $dotnetArguments += '--'
        $dotnetArguments += $Arguments
    }
    & dotnet @dotnetArguments
    $exitCode = $LASTEXITCODE
    $results.Add([pscustomobject]@{ Name = $Name; Status = if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' } })
}

Push-Location -LiteralPath $repositoryRoot
try {
    Invoke-DotnetContract 'StateFingerprintChecks' 'tools/StateFingerprintChecks/StateFingerprintChecks.csproj'
    Invoke-DotnetContract 'HistoryCounterKeyChecks' 'tools/HistoryCounterKeyChecks/HistoryCounterKeyChecks.csproj'
    Invoke-DotnetContract 'BfwsResearchChecks' 'tools/BfwsResearchChecks/BfwsResearchChecks.csproj'
    Invoke-DotnetContract 'PotionStrategyChecks' 'tools/PotionStrategyChecks/PotionStrategyChecks.csproj'
    Invoke-DotnetContract 'TurnStartChoicePreviewChecks' 'tools/TurnStartChoicePreviewChecks/TurnStartChoicePreviewChecks.csproj'
    Invoke-DotnetContract 'CardHookReceiverChecks' 'tools/CardHookReceiverChecks/CardHookReceiverChecks.csproj'
    Invoke-DotnetContract 'TurnPhaseMirrorChecks' 'tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj'
    Invoke-DotnetContract 'PredictionStateStoreChecks' 'tools/PredictionStateStoreChecks/PredictionStateStoreChecks.csproj'
    Invoke-DotnetContract 'DiagnosticLogTests' 'tools/DiagnosticLogTests/DiagnosticLogTests.csproj'
    Invoke-DotnetContract 'MultiplayerSafeExecuteChecks' 'tools/MultiplayerSafeExecuteChecks/MultiplayerSafeExecuteChecks.csproj'
    Invoke-DotnetContract 'MultiplayerCarryRankingChecks' 'tools/MultiplayerCarryRankingChecks/MultiplayerCarryRankingChecks.csproj'
    Invoke-DotnetContract 'MultiplayerLocalCrossTurnChecks' 'tools/MultiplayerLocalCrossTurnChecks/MultiplayerLocalCrossTurnChecks.csproj'
    Invoke-DotnetContract 'MultiplayerRootCaptureChecks' 'tools/MultiplayerRootCaptureChecks/MultiplayerRootCaptureChecks.csproj'
    Invoke-DotnetContract 'Sts2LocalInspectorChecks' 'tools/Sts2LocalInspector/Sts2LocalInspector.csproj' -Arguments @('--self-test')
    Invoke-PowerShellContract 'MonsterTargetFanoutAuditChecks' 'tools/test-monster-target-fanout-audit.ps1'
    Invoke-DotnetContract 'AncillaryWorkChecks' 'tools/AncillaryWorkChecks/AncillaryWorkChecks.csproj'
    Invoke-PowerShellContract 'MultiplayerSafeExecuteMp2BEvidenceChecks' 'tools/multiplayer-lab/test-mp2b-validator.ps1'
    Invoke-PowerShellContract 'MultiplayerSafeExecuteMp2BInterferenceChecks' 'tools/multiplayer-lab/test-mp2b-interference-validator.ps1'
    Invoke-PowerShellContract 'MultiplayerReactiveCarryEvidenceChecks' 'tools/multiplayer-lab/test-reactive-carry-validator.ps1'
    Invoke-PowerShellContract 'MultiplayerSafeAutoEvidenceChecks' 'tools/multiplayer-lab/test-safe-auto-validator.ps1'
    Invoke-PowerShellContract 'MultiplayerCarryRankingEvidenceChecks' 'tools/multiplayer-lab/test-carry-ranking-validator.ps1'
    Invoke-PowerShellContract 'MultiplayerJointContinuationEvidenceChecks' 'tools/multiplayer-lab/test-joint-continuation-validator.ps1'
    Invoke-PowerShellContract 'MultiplayerU3ScenarioEvidenceChecks' 'tools/multiplayer-lab/test-u3-scenario-validator.ps1'
    Invoke-PowerShellContract 'MultiplayerBeamRetentionAbEvidenceChecks' 'tools/multiplayer-lab/test-beam-retention-ab-validator.ps1'
    Invoke-PowerShellContract 'MultiplayerU6RuntimeClosureChecks' 'tools/multiplayer-lab/test-u6-runtime-closure-validator.ps1'
    Invoke-PowerShellContract 'P0BaselineClassifierChecks' 'tools/test-p0-baseline-classifier.ps1'
    Invoke-PowerShellContract 'P0BaselineManifestChecks' 'tools/test-p0-baseline-manifest.ps1'
    Invoke-PowerShellContract 'U0BaselineChecks' 'tools/test-u0-baseline.ps1'
    Invoke-PowerShellContract 'U1ActionPostStateChecks' 'tools/test-u1-action-poststate.ps1'
    Invoke-PowerShellContract 'U2SearchKernelChecks' 'tools/test-u2-search-kernel.ps1'
    Invoke-PowerShellContract 'MultiplayerContinuationLifecycleChecks' 'tools/test-multiplayer-continuation-lifecycle.ps1'
    Invoke-PowerShellContract 'MultiplayerSnapshotChecks' 'tools/test-headless-runtime.ps1' -Arguments @('-MultiplayerSnapshot')

    if ($SkipPython) {
        $results.Add([pscustomobject]@{ Name = 'BeamRankSortChecks'; Status = 'SKIP' })
        Write-Output 'SKIP: BeamRankSortChecks (-SkipPython)'
    } else {
        $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
        if ($null -eq $pythonCommand) {
            $pythonCommand = Get-Command python3 -ErrorAction SilentlyContinue
        }
        if ($null -eq $pythonCommand) {
            $results.Add([pscustomobject]@{ Name = 'BeamRankSortChecks'; Status = 'SKIP' })
            Write-Output 'SKIP: BeamRankSortChecks (Python is unavailable)'
        } else {
            Write-Output 'RUN: BeamRankSortChecks'
            & $pythonCommand.Source 'tools/BeamRankSortChecks/run.py'
            $exitCode = $LASTEXITCODE
            $results.Add([pscustomobject]@{
                    Name = 'BeamRankSortChecks'
                    Status = if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }
                })
        }
    }
} finally {
    Pop-Location
}

$passCount = @($results | Where-Object Status -eq 'PASS').Count
$failCount = @($results | Where-Object Status -eq 'FAIL').Count
$skipCount = @($results | Where-Object Status -eq 'SKIP').Count
Write-Output ("PASS: {0} FAIL: {1} SKIP: {2}" -f $passCount, $failCount, $skipCount)
if ($failCount -gt 0) {
    exit 1
}

