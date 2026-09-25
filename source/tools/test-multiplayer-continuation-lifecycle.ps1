#requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$combatPlan = Get-Content -LiteralPath (Join-Path $root 'src/Search/CombatPlan.cs') -Raw
$retention = Get-Content -LiteralPath (Join-Path $root 'src/Search/CombatBeamSolver.Retention.cs') -Raw
$terminal = Get-Content -LiteralPath (Join-Path $root 'src/Search/CombatBeamSolver.Terminal.cs') -Raw
$contracts = Get-Content -LiteralPath (Join-Path $root 'src/Search/MultiplayerLocalCrossTurnContracts.cs') -Raw
$combatRoot = Get-Content -LiteralPath (Join-Path $root 'src/Runtime/CombatRootSnapshot.cs') -Raw
$carryCapture = Get-Content -LiteralPath (Join-Path $root 'src/Runtime/MultiplayerCarryRankingContextCapture.cs') -Raw

function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

Require (-not $combatPlan.Contains('ContinuationRemoteFingerprint')) 'Continuation snapshot must not retain a remote fingerprint.'
Require (-not $combatPlan.Contains('RemotePublicFingerprint')) 'Continuation expectation/validation must not carry a remote fingerprint.'
Require (-not $retention.Contains('MultiplayerContinuationRemoteFingerprint.CapturePredicted')) 'Retention must not compute a continuation remote fingerprint.'
Require (-not $terminal.Contains('ContinuationRemoteFingerprint')) 'Terminal continuation building must not require a frozen remote fingerprint.'
Require (-not $contracts.Contains('remote_public_mismatch')) 'Remote fingerprint mismatch must not be a continuation reuse gate.'
Require ($combatPlan.Contains('MultiplayerScalingHooks')) 'Shared multiplayer scaling remains a continuation boundary.'
Require ($combatPlan.Contains('CardMultiplayerConstraint')) 'Card multiplayer constraint remains a continuation boundary.'
Require ($contracts.Contains('world_version_not_advanced')) 'WorldVersion advancement remains a continuation boundary.'
Require ($combatRoot.Contains('MultiplayerCarryRankingContextCapture.CaptureContinuationBoundary(')) 'Local-core multiplayer must capture continuation compatibility metadata even when team prediction is disabled.'
Require ($carryCapture.Contains('enabled: false')) 'Continuation-only metadata capture must not enable Carry Ranking.'
Require ($carryCapture.Contains('state.MultiplayerScalingModel?.ShouldReceiveCombatHooks')) 'Continuation-only capture must preserve multiplayer scaling hooks.'
Require ($carryCapture.Contains('state.RunState.CardMultiplayerConstraint.ToString()')) 'Continuation-only capture must preserve the card multiplayer constraint.'

$buildStart = $terminal.IndexOf('private IReadOnlyList<CachedContinuation> BuildContinuations')
$buildEnd = $terminal.IndexOf('private MultiplayerContinuationExpectation CreateMultiplayerContinuationExpectation')
if ($buildStart -lt 0 -or $buildEnd -le $buildStart) {
    throw 'Could not locate BuildContinuations lifecycle contract.'
}
$buildBlock = $terminal.Substring($buildStart, $buildEnd - $buildStart)
if ($buildBlock -match 'node\.Snapshot\.Simulator') {
    throw 'BuildContinuations must not access a historical node simulator after retention has released it.'
}

Write-Output 'MULTIPLAYER_CONTINUATION_LIFECYCLE_CHECKS_PASS no-remote-fingerprint/world-version+rules/local-core-compatibility'
