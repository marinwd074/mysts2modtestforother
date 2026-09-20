#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-carry-ranking-' + [Guid]::NewGuid().ToString('N') + '.log')

function Invoke-Validator {
    param(
        [Parameter(Mandatory)][string[]]$Lines,
        [Parameter(Mandatory)][int]$ExpectedExitCode,
        [Parameter(Mandatory)][string]$ExpectedToken
    )
    [IO.File]::WriteAllLines($fixture, $Lines)
    $output = @(& pwsh -NoLogo -NoProfile -File (Join-Path $scriptRoot 'validate-carry-ranking-results.ps1') -LogPath $fixture -Phase All)
    if ($LASTEXITCODE -ne $ExpectedExitCode) {
        throw "Carry Ranking validator exit code mismatch. Expected $ExpectedExitCode, actual $LASTEXITCODE. Output: $($output -join ' | ')"
    }
    if (($output -join [Environment]::NewLine) -notmatch [regex]::Escape($ExpectedToken)) {
        throw "Carry Ranking validator output did not contain '$ExpectedToken'. Output: $($output -join ' | ')"
    }
}

try {
    Invoke-Validator -ExpectedExitCode 0 -ExpectedToken 'MULTIPLAYER_CARRY_RANKING_All_PASS' -Lines @(
        '[CombatSolver/MultiplayerCarry] MP_CARRY_CONTEXT_CAPTURE world_version=10 remote_players=1 enemies=2 all_player_threats=1 unknown_threats=1 remote_private=false context_reused=false public_fingerprint=fixture',
        '[CombatSolver/MultiplayerCarry] MP_CARRY_RANKING rank=1 selected=true enabled=true remoteRiskBefore=5 remoteRiskAfter=4 threatsRemoved=1 unknownRiskCount=0 carryPreference=1 carryPreferenceReason=public_remote_threat_removed current_turn_card=true actions=PlayCard:STRIKE',
        '[CombatSolver/MultiplayerCarry] MP_CARRY_RANKING rank=2 selected=false enabled=true remoteRiskBefore=4 remoteRiskAfter=4 threatsRemoved=0 unknownRiskCount=1 carryPreference=0 carryPreferenceReason=unknown_enemy_targeting_neutral current_turn_card=true actions=PlayCard:DEFEND'
    )

    Invoke-Validator -ExpectedExitCode 2 -ExpectedToken 'MULTIPLAYER_CARRY_RANKING_All_UNVERIFIED' -Lines @(
        '[CombatSolver/MultiplayerCarry] MP_CARRY_CONTEXT_CAPTURE world_version=10 remote_players=1 enemies=1 all_player_threats=1 unknown_threats=0 remote_private=false context_reused=false public_fingerprint=fixture',
        '[CombatSolver/MultiplayerCarry] MP_CARRY_RANKING rank=1 selected=true enabled=true remoteRiskBefore=1 remoteRiskAfter=1 threatsRemoved=0 unknownRiskCount=0 carryPreference=0 carryPreferenceReason=no_meaningful_team_risk_difference current_turn_card=true actions=PlayCard:DEFEND'
    )

    Invoke-Validator -ExpectedExitCode 1 -ExpectedToken 'MULTIPLAYER_CARRY_RANKING_All_FAIL' -Lines @(
        '[CombatSolver/MultiplayerCarry] MP_CARRY_CONTEXT_CAPTURE world_version=10 remote_players=1 enemies=1 all_player_threats=1 unknown_threats=0 remote_private=true context_reused=false public_fingerprint=fixture',
        '[CombatSolver/MultiplayerCarry] MP_CARRY_RANKING rank=1 selected=true enabled=true remoteRiskBefore=1 remoteRiskAfter=0 threatsRemoved=0 unknownRiskCount=0 carryPreference=1 carryPreferenceReason=public_remote_threat_removed current_turn_card=true actions=PlayCard:STRIKE'
    )

    Write-Output 'MULTIPLAYER_CARRY_RANKING_VALIDATOR_PASS'
} finally {
    if (Test-Path -LiteralPath $fixture -PathType Leaf) {
        Remove-Item -LiteralPath $fixture -Force
    }
}
