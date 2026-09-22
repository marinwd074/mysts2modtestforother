#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-console-runtime-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$fixturePath = Join-Path $tempRoot 'fixture.json'
$logPath = Join-Path $tempRoot 'fixture.log'

@{
    schemaVersion = 1
    name = 'tag-team-basic'
    waitFor = 'local_playable_turn'
    commands = @('energy 10', 'card TAG_TEAM hand', 'damage 3 0')
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fixturePath -Encoding UTF8

function Invoke-Expected {
    param(
        [int]$ExpectedExit,
        [ValidateSet('PASS','FAIL','UNVERIFIED')]
        [string]$ExpectedStatus
    )
    $output = @(& pwsh -NoLogo -NoProfile -File (Join-Path $scriptRoot 'validate-console-fixture-results.ps1') `
        -LogPath $logPath -FixturePath $fixturePath -Json 2>$null)
    $actualExit = $LASTEXITCODE
    if ($actualExit -ne $ExpectedExit) {
        throw "Console fixture runtime validator returned $actualExit, expected $ExpectedExit."
    }
    if ($output.Count -eq 0) {
        throw "Console fixture runtime validator emitted no JSON for expected status $ExpectedStatus."
    }
    try {
        $machine = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    }
    catch {
        throw "Console fixture runtime validator emitted invalid JSON for expected status $ExpectedStatus."
    }
    if ([string]$machine.status -ne $ExpectedStatus) {
        throw "Console fixture runtime validator status $($machine.status), expected $ExpectedStatus."
    }
}

try {
    [IO.File]::WriteAllLines($logPath, @(
        '[CombatSolver/MultiplayerFixture] FIXTURE_ARMED name=tag-team-basic commands=3',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_START name=tag-team-basic index=0 command="energy 10" world_version=10',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_RESULT name=tag-team-basic index=0 success=true message="Enqueued energy" world_version_before=10 world_version_after=11',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_START name=tag-team-basic index=1 command="card TAG_TEAM hand" world_version=11',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_RESULT name=tag-team-basic index=1 success=true message="Enqueued card" world_version_before=11 world_version_after=12',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_START name=tag-team-basic index=2 command="damage 3 0" world_version=12',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_RESULT name=tag-team-basic index=2 success=true message="Enqueued damage" world_version_before=12 world_version_after=13',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMPLETE name=tag-team-basic commands=3 world_version=13'
    ))
    Invoke-Expected -ExpectedExit 0 -ExpectedStatus PASS

    Add-Content -LiteralPath $logPath -Value '[CombatSolver/MultiplayerFixture] FIXTURE_FAIL name=tag-team-basic exception=InvalidOperationException message="boom"'
    Invoke-Expected -ExpectedExit 1 -ExpectedStatus FAIL

    [IO.File]::WriteAllLines($logPath, @(
        '[CombatSolver/MultiplayerFixture] FIXTURE_ARMED name=tag-team-basic commands=3',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_START name=tag-team-basic index=0 command="energy 10" world_version=10',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_RESULT name=tag-team-basic index=0 success=true message="Enqueued energy" world_version_before=10 world_version_after=11',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_START name=tag-team-basic index=1 command="card HYPERBEAM hand" world_version=11',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_RESULT name=tag-team-basic index=1 success=true message="Enqueued card" world_version_before=11 world_version_after=12',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_START name=tag-team-basic index=2 command="damage 3 0" world_version=12',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_RESULT name=tag-team-basic index=2 success=true message="Enqueued damage" world_version_before=12 world_version_after=13',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMPLETE name=tag-team-basic commands=3 world_version=13'
    ))
    Invoke-Expected 1

    [IO.File]::WriteAllLines($logPath, @(
        '[CombatSolver/MultiplayerFixture] FIXTURE_ARMED name=tag-team-basic commands=3',
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_START name=tag-team-basic index=0 command="energy 10" world_version=10'
    ))
    Invoke-Expected -ExpectedExit 2 -ExpectedStatus UNVERIFIED

    Write-Output 'MULTIPLAYER_CONSOLE_FIXTURE_RUNTIME_VALIDATOR_PASS'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
