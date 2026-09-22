#requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$combatPlan = Get-Content -LiteralPath (Join-Path $root 'src/Search/CombatPlan.cs') -Raw
$retention = Get-Content -LiteralPath (Join-Path $root 'src/Search/CombatBeamSolver.Retention.cs') -Raw
$terminal = Get-Content -LiteralPath (Join-Path $root 'src/Search/CombatBeamSolver.Terminal.cs') -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-Contains $combatPlan 'ContinuationRemoteFingerprint\s*\{\s*get;\s*private set;' 'SimulationSnapshot must retain a frozen multiplayer continuation fingerprint.'
Assert-Contains $combatPlan 'SetContinuation\([\s\S]*StateFingerprint\? remoteFingerprint' 'SimulationSnapshot.SetContinuation must accept the frozen remote fingerprint.'
Assert-Contains $retention 'MultiplayerContinuationRemoteFingerprint\.CapturePredicted\([\s\S]*simulator[\s\S]*_player' 'Continuation capture must freeze the teammate fingerprint before the simulator can be released.'
Assert-Contains $retention 'SetContinuation\([\s\S]*remoteFingerprint\)' 'Continuation capture must store the frozen teammate fingerprint on the snapshot.'
Assert-Contains $terminal 'node\.Snapshot\.ContinuationRemoteFingerprint' 'BuildContinuations must prefer the frozen teammate fingerprint.'
Assert-Contains $terminal 'MultiplayerContinuationRemoteFingerprint\.CapturePredicted\([\s\S]*replayed\.Simulator' 'Fallback continuation replay must derive the teammate fingerprint from the same replayed world.'

$buildStart = $terminal.IndexOf('private IReadOnlyList<CachedContinuation> BuildContinuations')
$buildEnd = $terminal.IndexOf('private MultiplayerContinuationExpectation CreateMultiplayerContinuationExpectation')
if ($buildStart -lt 0 -or $buildEnd -le $buildStart) {
    throw 'Could not locate BuildContinuations lifecycle contract.'
}
$buildBlock = $terminal.Substring($buildStart, $buildEnd - $buildStart)
if ($buildBlock -match 'node\.Snapshot\.Simulator') {
    throw 'BuildContinuations must not access a historical node simulator after retention has released it.'
}

Write-Output 'MULTIPLAYER_CONTINUATION_LIFECYCLE_CHECKS_PASS'
