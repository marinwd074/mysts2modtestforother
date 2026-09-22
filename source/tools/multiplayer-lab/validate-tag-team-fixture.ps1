#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$records = [Collections.Generic.List[object]]::new()
foreach ($pathValue in $LogPath) {
    $path = (Resolve-Path -LiteralPath $pathValue -ErrorAction Stop).Path
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $path) {
        $lineNumber++
        $records.Add([pscustomobject]@{
                Path = $path
                LineNumber = $lineNumber
                Text = [string]$line
            })
    }
}

function Evidence([object]$record) {
    return '{0}:{1}: {2}' -f $record.Path, $record.LineNumber, $record.Text
}

$sourceComplete = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerFixture\] FIXTURE_COMPLETE\b.*\bname=tag-team-source\b'
    })
$attackComplete = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerFixture\] FIXTURE_COMPLETE\b.*\bname=tag-team-aoe\b'
    })
$replayLines = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerFixture\] TAG_TEAM_REPLAY_OBSERVED\b'
    })

$checks = [Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [string]$Status, [string]$Detail = '', [string]$EvidenceText = '') {
    $checks.Add([ordered]@{
            name = $Name
            status = $Status
            detail = if ($Detail) { $Detail } else { $null }
            evidence = if ($EvidenceText) { $EvidenceText } else { $null }
        })
}

foreach ($pair in @(
        @{ Name = 'sourceFixtureCompleted'; Records = $sourceComplete },
        @{ Name = 'attackFixtureCompleted'; Records = $attackComplete }
    )) {
    if ($pair.Records.Count -eq 1) {
        Add-Check $pair.Name PASS '' (Evidence $pair.Records[0])
    } elseif ($pair.Records.Count -eq 0) {
        Add-Check $pair.Name UNVERIFIED 'Required fixture completion was not observed.'
    } else {
        Add-Check $pair.Name FAIL 'Fixture completed more than once.' (($pair.Records | ForEach-Object { Evidence $_ }) -join ' | ')
    }
}

$validReplay = $null
$invalidReplay = [Collections.Generic.List[string]]::new()
foreach ($record in $replayLines) {
    $pattern = 'card=HYPERBEAM\b.*\bowner=(\d+)\b.*\bapplier=(\d+)\b.*\bplay_count_before=(\d+)\b.*\bplay_count_after=(\d+)\b.*\bextra_plays=(\d+)\b'
    if ($record.Text -notmatch $pattern) {
        $invalidReplay.Add((Evidence $record))
        continue
    }
    $owner = [uint64]$Matches[1]
    $applier = [uint64]$Matches[2]
    $before = [int]$Matches[3]
    $after = [int]$Matches[4]
    $extra = [int]$Matches[5]
    if ($owner -eq $applier -or $after -ne ($before + 1) -or $extra -ne 1) {
        $invalidReplay.Add((Evidence $record))
        continue
    }
    $validReplay = $record
    break
}

$bothCompleted = $sourceComplete.Count -eq 1 -and $attackComplete.Count -eq 1
if ($null -ne $validReplay) {
    Add-Check 'aoeReplayGrantedToOtherPlayer' PASS '' (Evidence $validReplay)
} elseif ($invalidReplay.Count -gt 0) {
    Add-Check 'aoeReplayGrantedToOtherPlayer' FAIL 'Replay marker did not satisfy other-player + exactly-one-extra-play semantics.' ($invalidReplay -join ' | ')
} elseif ($bothCompleted) {
    Add-Check 'aoeReplayGrantedToOtherPlayer' FAIL 'Both fixtures completed but no HYPERBEAM Tag Team replay was observed.'
} else {
    Add-Check 'aoeReplayGrantedToOtherPlayer' UNVERIFIED 'Scenario is incomplete; replay evidence is not yet available.'
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
    checks = @($checks)
    limitations = @(
        'PASS proves the native TagTeamPower increased HYPERBEAM play count by exactly one for another player in the observed multiplayer fixture.',
        'It does not yet prove CombatSolver Safe Execute may deploy TAG_TEAM automatically.'
    )
}

$jsonText = $result | ConvertTo-Json -Depth 7
if ($Json) {
    Write-Output $jsonText
} else {
    Write-Output "MULTIPLAYER_TAG_TEAM_$status"
    foreach ($check in $checks) {
        Write-Output ("{0} {1}" -f $check.status, $check.name)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
