#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [ValidateSet('R1', 'R2', 'All')]
    [string]$Phase = 'All',

    [string]$OutputPath = '',

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$records = [Collections.Generic.List[object]]::new()
$globalIndex = 0
$resolvedLogs = [Collections.Generic.List[string]]::new()
foreach ($pathValue in $LogPath) {
    $path = (Resolve-Path -LiteralPath $pathValue -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Carry Ranking log path is not a file: $path"
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
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'UNVERIFIED')][string]$Status,
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

function Join-Evidence {
    param([object[]]$Items)
    return ($Items | ForEach-Object { Format-Evidence $_ } | Join-String -Separator ' | ')
}

function Get-Token {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )
    $pattern = '(?:^|\s)' + [regex]::Escape($Name) + '=([^\s]+)'
    if ($Text -match $pattern) { return $Matches[1] }
    return $null
}

function Get-IntToken {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )
    $value = Get-Token $Text $Name
    if ($null -eq $value -or $value -notmatch '^-?\d+$') { return $null }
    return [int]$value
}

$contexts = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerCarry\] MP_CARRY_CONTEXT_CAPTURE\b'
    })
$rankings = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerCarry\] MP_CARRY_RANKING\b'
    })

$badPrivacy = @($contexts | Where-Object {
        $_.Text -notmatch '\bremote_private=false\b' -or
        $_.Text -notmatch '\bcontext_reused=false\b'
    })
if ($badPrivacy.Count -gt 0) {
    Add-Check 'publicContextOnly' FAIL (Join-Evidence $badPrivacy) 'Carry context must be fresh and must not read remote private state.'
} elseif ($contexts.Count -gt 0) {
    Add-Check 'publicContextOnly' PASS (Format-Evidence $contexts[0])
} else {
    Add-Check 'publicContextOnly' UNVERIFIED '' 'No Carry context capture marker was observed.'
}

if ($Phase -in @('R1', 'All')) {
    $eligibleContexts = @($contexts | Where-Object {
            (Get-IntToken $_.Text 'remote_players') -ge 1 -and
            (Get-IntToken $_.Text 'enemies') -ge 1 -and
            (Get-IntToken $_.Text 'all_player_threats') -ge 1
        })
    $malformedContexts = @($contexts | Where-Object {
            $null -eq (Get-IntToken $_.Text 'remote_players') -or
            $null -eq (Get-IntToken $_.Text 'enemies') -or
            $null -eq (Get-IntToken $_.Text 'all_player_threats') -or
            $null -eq (Get-IntToken $_.Text 'unknown_threats')
        })
    if ($malformedContexts.Count -gt 0) {
        Add-Check 'R1ObservableThreatContext' FAIL (Join-Evidence $malformedContexts) 'Carry context marker is missing required machine-readable threat counts.'
    } elseif ($eligibleContexts.Count -gt 0) {
        Add-Check 'R1ObservableThreatContext' PASS (Format-Evidence $eligibleContexts[0]) 'At least one real root observed a base-game all-player public threat.'
    } elseif ($contexts.Count -gt 0) {
        Add-Check 'R1ObservableThreatContext' UNVERIFIED (Join-Evidence @($contexts | Select-Object -First 3)) 'The run had Carry roots but no base-game AttackIntent threat; use another normal attacking turn.'
    } else {
        Add-Check 'R1ObservableThreatContext' UNVERIFIED '' 'No Carry root was captured.'
    }
}

$rankingContradictions = [Collections.Generic.List[object]]::new()
foreach ($record in $rankings) {
    $preference = Get-IntToken $record.Text 'carryPreference'
    $removed = Get-IntToken $record.Text 'threatsRemoved'
    $unknown = Get-IntToken $record.Text 'unknownRiskCount'
    $reason = Get-Token $record.Text 'carryPreferenceReason'
    if ($null -eq $preference -or $null -eq $removed -or $null -eq $unknown -or $null -eq $reason) {
        $rankingContradictions.Add($record)
        continue
    }
    if (($preference -gt 0 -and $removed -le 0) -or
        ($reason -eq 'unknown_enemy_targeting_neutral' -and ($preference -ne 0 -or $removed -ne 0))) {
        $rankingContradictions.Add($record)
    }
}
if ($rankingContradictions.Count -gt 0) {
    Add-Check 'rankingSemantics' FAIL (Join-Evidence $rankingContradictions) 'Carry ranking markers contradict the evaluator contract.'
} elseif ($rankings.Count -gt 0) {
    Add-Check 'rankingSemantics' PASS (Format-Evidence $rankings[0])
} else {
    Add-Check 'rankingSemantics' UNVERIFIED '' 'No Carry ranking marker was observed.'
}

if ($Phase -in @('R2', 'All')) {
    $positiveSelected = @($rankings | Where-Object {
            $_.Text -match '\bselected=true\b' -and
            $_.Text -match '\benabled=true\b' -and
            (Get-IntToken $_.Text 'carryPreference') -gt 0 -and
            (Get-IntToken $_.Text 'threatsRemoved') -gt 0 -and
            (Get-IntToken $_.Text 'remoteRiskAfter') -lt (Get-IntToken $_.Text 'remoteRiskBefore')
        })
    if ($rankingContradictions.Count -gt 0) {
        Add-Check 'R2PositiveCarrySelection' FAIL (Join-Evidence $rankingContradictions) 'Ranking evidence is internally inconsistent.'
    } elseif ($positiveSelected.Count -gt 0) {
        Add-Check 'R2PositiveCarrySelection' PASS (Format-Evidence $positiveSelected[0]) 'The selected real route removed a proven public team threat and received positive carry preference.'
    } elseif ($rankings.Count -gt 0) {
        Add-Check 'R2PositiveCarrySelection' UNVERIFIED (Join-Evidence @($rankings | Select-Object -First 5)) 'Ranking ran, but this fixture did not select a route with positive Carry preference.'
    } else {
        Add-Check 'R2PositiveCarrySelection' UNVERIFIED '' 'No ranking result was emitted.'
    }
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
    phase = $Phase
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($resolvedLogs)
    contextCount = $contexts.Count
    rankingCount = $rankings.Count
    checks = @($checks)
    limitations = @(
        'R1 proves runtime public-threat capture, not teammate behavior prediction.',
        'R2 requires a naturally selected positive Carry route; a run without such a route is UNVERIFIED, not FAIL.',
        'This validator never promotes synthetic fixtures to runtime evidence.'
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
    Write-Output "MULTIPLAYER_CARRY_RANKING_$Phase`_$status contexts=$($contexts.Count) rankings=$($rankings.Count)"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}
if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
