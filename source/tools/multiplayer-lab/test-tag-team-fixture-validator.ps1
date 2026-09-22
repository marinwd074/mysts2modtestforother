#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('combat-solver-tag-team-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$sourceLog = Join-Path $tempRoot 'source.log'
$attackLog = Join-Path $tempRoot 'attack.log'
$combinedLog = Join-Path $tempRoot 'combined.log'

function Invoke-Expected {
    param(
        [Parameter(Mandatory = $true)][int]$ExpectedExit,
        [Parameter(Mandatory = $true)][ValidateSet('PASS','FAIL','UNVERIFIED')][string]$ExpectedStatus
    )
    $combined = [Collections.Generic.List[string]]::new()
    foreach ($path in @($sourceLog, $attackLog)) {
        if (Test-Path -LiteralPath $path) {
            foreach ($line in Get-Content -LiteralPath $path) {
                $combined.Add([string]$line)
            }
        }
    }
    [IO.File]::WriteAllLines($combinedLog, $combined)

    $output = @(& pwsh -NoLogo -NoProfile -File (Join-Path $scriptRoot 'validate-tag-team-fixture.ps1') `
        -LogPath $combinedLog -Json 2>$null)
    $actualExit = $LASTEXITCODE
    if ($actualExit -ne $ExpectedExit) {
        throw "Tag Team validator returned $actualExit, expected $ExpectedExit. Output: $($output -join ' | ')"
    }
    $machine = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if ([string]$machine.status -ne $ExpectedStatus) {
        throw "Tag Team validator status $($machine.status), expected $ExpectedStatus."
    }
}

try {
    [IO.File]::WriteAllLines($sourceLog, @(
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMPLETE name=tag-team-source commands=2 world_version=20'
    ))
    [IO.File]::WriteAllLines($attackLog, @(
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMPLETE name=tag-team-aoe commands=2 world_version=21',
        '[CombatSolver/MultiplayerFixture] TAG_TEAM_REPLAY_OBSERVED name=tag-team-aoe card=HYPERBEAM owner=2 target=7 applier=1 power_owner=- play_count_before=1 play_count_after=2 extra_plays=1'
    ))
    Invoke-Expected -ExpectedExit 0 -ExpectedStatus PASS

    [IO.File]::WriteAllLines($attackLog, @(
        '[CombatSolver/MultiplayerFixture] FIXTURE_COMPLETE name=tag-team-aoe commands=2 world_version=21',
        '[CombatSolver/MultiplayerFixture] TAG_TEAM_REPLAY_OBSERVED name=tag-team-aoe card=HYPERBEAM owner=1 target=7 applier=1 power_owner=- play_count_before=1 play_count_after=2 extra_plays=1'
    ))
    Invoke-Expected -ExpectedExit 1 -ExpectedStatus FAIL

    [IO.File]::WriteAllLines($attackLog, @())
    Invoke-Expected -ExpectedExit 2 -ExpectedStatus UNVERIFIED

    Write-Output 'MULTIPLAYER_TAG_TEAM_VALIDATOR_PASS'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
