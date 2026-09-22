#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$FixturePath,

    [string]$OutputPath = '',

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console-fixture-common.ps1')

$fixture = Read-MultiplayerConsoleFixture -FixturePath $FixturePath
$records = [Collections.Generic.List[object]]::new()
$resolvedLogs = [Collections.Generic.List[string]]::new()
$globalIndex = 0
foreach ($pathValue in $LogPath) {
    $path = (Resolve-Path -LiteralPath $pathValue -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Console fixture log path is not a file: $path"
    }
    $resolvedLogs.Add($path)
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $path) {
        $lineNumber++
        $records.Add([pscustomobject]@{
                Index = $globalIndex++
                Path = $path
                LineNumber = $lineNumber
                Text = [string]$line
            })
    }
}

$checks = [Collections.Generic.List[object]]::new()
function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('PASS','FAIL','UNVERIFIED')][string]$Status,
        [string]$Evidence = '',
        [string]$Detail = ''
    )
    $checks.Add([ordered]@{
            name = $Name
            status = $Status
            evidence = if ([string]::IsNullOrWhiteSpace($Evidence)) { $null } else { $Evidence }
            detail = if ([string]::IsNullOrWhiteSpace($Detail)) { $null } else { $Detail }
        })
}

function Format-Evidence {
    param($Record)
    if ($null -eq $Record) { return '' }
    return '{0}:{1}: {2}' -f $Record.Path, $Record.LineNumber, $Record.Text
}

function Find-FirstAfter {
    param(
        [int]$AfterIndex,
        [string]$Pattern
    )
    return @($records | Where-Object {
            $_.Index -gt $AfterIndex -and $_.Text -match $Pattern
        } | Select-Object -First 1)
}

$escapedName = [regex]::Escape([string]$fixture.Name)
$armed = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerFixture\] FIXTURE_ARMED\b' -and
        $_.Text -match ('\bname=' + $escapedName + '\b')
    })
$rejectOrFail = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerFixture\] FIXTURE_(?:REJECT|FAIL)\b'
    })
$complete = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerFixture\] FIXTURE_COMPLETE\b' -and
        $_.Text -match ('\bname=' + $escapedName + '\b')
    })

if ($rejectOrFail.Count -gt 0) {
    Add-Check 'noFixtureFailure' FAIL ((@($rejectOrFail | ForEach-Object { Format-Evidence $_ })) -join ' | ')
} else {
    Add-Check 'noFixtureFailure' PASS
}

if ($armed.Count -eq 0) {
    Add-Check 'fixtureArmedOnce' UNVERIFIED '' 'No matching FIXTURE_ARMED marker was observed.'
} elseif ($armed.Count -ne 1) {
    Add-Check 'fixtureArmedOnce' FAIL ((@($armed | ForEach-Object { Format-Evidence $_ })) -join ' | ') 'Fixture must arm exactly once per process.'
} else {
    Add-Check 'fixtureArmedOnce' PASS (Format-Evidence $armed[0])
}

$sequenceEvidence = [Collections.Generic.List[string]]::new()
$sequenceProblems = [Collections.Generic.List[string]]::new()
$cursor = if ($armed.Count -gt 0) { [int]$armed[0].Index } else { -1 }
for ($index = 0; $index -lt $fixture.Commands.Count; $index++) {
    $expectedCommandJson = ([string]$fixture.Commands[$index] | ConvertTo-Json -Compress)
    $startPattern = '\[CombatSolver/MultiplayerFixture\] FIXTURE_COMMAND_START\b.*\bname=' + $escapedName + '\b.*\bindex=' + $index + '\b.*\bcommand=' + [regex]::Escape($expectedCommandJson) + '(?:\s|$)'
    $starts = @(Find-FirstAfter -AfterIndex $cursor -Pattern $startPattern)
    if ($starts.Count -ne 1) {
        $sequenceProblems.Add("index=$index missing FIXTURE_COMMAND_START")
        continue
    }
    $start = $starts[0]
    $resultPattern = '\[CombatSolver/MultiplayerFixture\] FIXTURE_COMMAND_RESULT\b.*\bname=' + $escapedName + '\b.*\bindex=' + $index + '\b.*\bsuccess=true\b'
    $results = @(Find-FirstAfter -AfterIndex $start.Index -Pattern $resultPattern)
    if ($results.Count -ne 1) {
        $sequenceProblems.Add("index=$index missing successful FIXTURE_COMMAND_RESULT")
        $cursor = [int]$start.Index
        continue
    }
    $result = $results[0]
    $sequenceEvidence.Add((Format-Evidence $start))
    $sequenceEvidence.Add((Format-Evidence $result))
    $cursor = [int]$result.Index
}

if ($sequenceProblems.Count -eq 0 -and $fixture.Commands.Count -gt 0) {
    Add-Check 'orderedCommandSequence' PASS ($sequenceEvidence -join ' | ')
} elseif ($rejectOrFail.Count -eq 0 -and $complete.Count -eq 0) {
    # A truncated/incomplete journal is insufficient evidence, not proof that the
    # runtime command sequence itself failed. An explicit fail/reject or a claimed
    # completion with a mismatched sequence remains a hard failure.
    Add-Check 'orderedCommandSequence' UNVERIFIED ($sequenceEvidence -join ' | ') ($sequenceProblems -join '; ')
} else {
    Add-Check 'orderedCommandSequence' FAIL ($sequenceEvidence -join ' | ') ($sequenceProblems -join '; ')
}

if ($complete.Count -eq 1) {
    $expectedCountToken = 'commands=' + $fixture.Commands.Count
    if ($complete[0].Text -match ('\b' + [regex]::Escape($expectedCountToken) + '\b')) {
        Add-Check 'fixtureCompleted' PASS (Format-Evidence $complete[0])
    } else {
        Add-Check 'fixtureCompleted' FAIL (Format-Evidence $complete[0]) 'Completion command count does not match the fixture.'
    }
} elseif ($complete.Count -eq 0 -and $rejectOrFail.Count -eq 0) {
    Add-Check 'fixtureCompleted' UNVERIFIED '' 'No matching FIXTURE_COMPLETE marker was observed.'
} else {
    Add-Check 'fixtureCompleted' FAIL ((@($complete | ForEach-Object { Format-Evidence $_ })) -join ' | ') 'Expected exactly one completion marker.'
}

$status = if (@($checks | Where-Object status -eq 'FAIL').Count -gt 0) {
    'FAIL'
} elseif (@($checks | Where-Object status -eq 'UNVERIFIED').Count -gt 0) {
    'UNVERIFIED'
} else {
    'PASS'
}

$result = [ordered]@{
    schemaVersion = 1
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    fixtureName = $fixture.Name
    expectedCommands = @($fixture.Commands)
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'PASS proves only that the owned Lab client accepted and completed the fixture command sequence through the runtime runner.',
        'It does not by itself prove Host/Client state equality or the target card/power semantic; those remain separate runtime differentials.'
    )
}

$jsonText = $result | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputFull = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $outputFull
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($outputFull, $jsonText, [Text.UTF8Encoding]::new($false))
}

if ($Json) {
    Write-Output $jsonText
} else {
    Write-Output "MULTIPLAYER_CONSOLE_FIXTURE_$status fixture=$($fixture.Name) commands=$($fixture.Commands.Count)"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
