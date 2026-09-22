#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validator = Join-Path $PSScriptRoot 'validate-console-fixture.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("combatsolver-console-fixture-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

function Write-Fixture {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][object]$Value
    )
    $path = Join-Path $tempRoot $Name
    $Value | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

function Invoke-Expected {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$ExpectedExit
    )
    & pwsh -NoLogo -NoProfile -File $validator -FixturePath $Path *> $null
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "Validator exit mismatch for $Path. Expected $ExpectedExit, got $LASTEXITCODE."
    }
}

try {
    $valid = Write-Fixture 'valid.json' ([ordered]@{
        schemaVersion = 1
        name = 'tag-team-basic'
        waitFor = 'local_playable_turn'
        commands = @(
            'energy 10',
            'card TAG_TEAM hand',
            'card STRIKE_IRONCLAD hand',
            'block 7',
            'damage 3 0',
            'heal 3 0'
        )
    })
    Invoke-Expected $valid 0

    $badVerb = Write-Fixture 'bad-verb.json' ([ordered]@{
        schemaVersion = 1
        name = 'unsafe-local-command'
        waitFor = 'local_playable_turn'
        commands = @('god')
    })
    Invoke-Expected $badVerb 1

    $badWait = Write-Fixture 'bad-wait.json' ([ordered]@{
        schemaVersion = 1
        name = 'bad-wait'
        waitFor = 'after_reward'
        commands = @('energy 3')
    })
    Invoke-Expected $badWait 1

    $badSchema = Write-Fixture 'bad-schema.json' ([ordered]@{
        schemaVersion = 2
        name = 'future-schema'
        waitFor = 'local_playable_turn'
        commands = @('draw 1')
    })
    Invoke-Expected $badSchema 1

    $badName = Write-Fixture 'bad-name.json' ([ordered]@{
        schemaVersion = 1
        name = 'tag team ambiguous'
        waitFor = 'local_playable_turn'
        commands = @('draw 1')
    })
    Invoke-Expected $badName 1

    Write-Output 'PASS: multiplayer console fixture validator checks'
    exit 0
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
