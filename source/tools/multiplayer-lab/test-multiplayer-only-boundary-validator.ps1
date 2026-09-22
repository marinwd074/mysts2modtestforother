#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-multiplayer-only-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$logPath = Join-Path $tempRoot 'combat.log'
$base = @(
    '[CombatSolver/MultiplayerFixture] FIXTURE_COMPLETE name=tag-team-source commands=2 world_version=20',
    '[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO enabled=true world_version=21',
    '[CombatSolver/MultiplayerSafeExecute] DEPLOY_PREFIX_STOP turn=1 action=multiplayer_only_card',
    '[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO_STOP reason=multiplayer_only_card turn=1'
)
function Check([string[]]$lines, [int]$expectedExit, [string]$expectedStatus) {
    [IO.File]::WriteAllLines($logPath, $lines)
    $output = @(& pwsh -NoLogo -NoProfile -File (Join-Path $scriptRoot 'validate-multiplayer-only-boundary.ps1') -LogPath $logPath -Json)
    if ($LASTEXITCODE -ne $expectedExit) { throw "Expected exit $expectedExit, got $LASTEXITCODE. $($output -join ' ')" }
    $result = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if ($result.status -ne $expectedStatus) { throw "Expected $expectedStatus, got $($result.status)." }
}
try {
    Check $base 0 PASS
    Check @($base[0..2] + '[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED request_id=1 action_index=1 type=PlayCardAction turn=1 card=TAG_TEAM custom_network_api_used=false' + $base[3]) 1 FAIL
    Check @($base[0..1]) 2 UNVERIFIED
    Write-Output 'MULTIPLAYER_ONLY_BOUNDARY_VALIDATOR_PASS'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
