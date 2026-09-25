#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('A', 'B', 'C')]
    [string]$Smoke,

    [string]$OutputPath = '',

    [Nullable[int]]$RequestId,

    [ValidateRange(3, 3)]
    [int]$MinLocalTurns = 3,

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
        throw "Reactive Carry log path is not a file: $path"
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

function RecordsAfter {
    param(
        [Parameter(Mandatory)][int]$Index,
        [int]$Before = [int]::MaxValue
    )
    return @($records | Where-Object { $_.Index -gt $Index -and $_.Index -lt $Before })
}

$accepted = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_SAFE_END_TURN_ACCEPTED\b' -and
        ($null -eq $RequestId -or $_.Text -match ('request_id=' + [regex]::Escape([string]$RequestId) + '\b'))
    })
$requiredCount = if ($Smoke -eq 'C') { $MinLocalTurns } else { 1 }

if ($accepted.Count -eq 0) {
    Add-Check 'safeEndTurnAccepted' UNVERIFIED '' 'No Safe EndTurn acceptance marker was observed.'
} elseif ($Smoke -ne 'C' -and $accepted.Count -ne 1) {
    Add-Check 'safeEndTurnAccepted' FAIL (Join-Evidence $accepted) 'Smoke A/B must contain exactly one accepted Safe EndTurn.'
} elseif ($accepted.Count -ge $requiredCount) {
    Add-Check 'safeEndTurnAccepted' PASS (Join-Evidence @($accepted | Select-Object -First $requiredCount))
} else {
    Add-Check 'safeEndTurnAccepted' FAIL (Join-Evidence $accepted) "Smoke $Smoke requires $requiredCount accepted local turn(s)."
}

$selectedAccepted = @(
    if ($Smoke -eq 'C') {
        $accepted | Select-Object -First $requiredCount
    } else {
        $accepted | Select-Object -First 1
    }
)

$scopedRecords = if ($null -eq $RequestId) {
    $records
} else {
    @($records | Where-Object {
            $_.Text -match ('request_id=' + [regex]::Escape([string]$RequestId) + '\b')
        })
}

$turns = @()
$requests = @()
$freshSearches = [Collections.Generic.List[object]]::new()
$continuationReuses = [Collections.Generic.List[object]]::new()
$missingNextPlan = [Collections.Generic.List[object]]::new()
$ambiguousNextPlan = [Collections.Generic.List[object]]::new()
$reusedOldRequest = [Collections.Generic.List[object]]::new()
$remoteDeltaEvidence = [Collections.Generic.List[object]]::new()
$boundaryEvidence = [Collections.Generic.List[object]]::new()
$nativeEndTurnEvidence = [Collections.Generic.List[object]]::new()
$safeRevalidationEvidence = [Collections.Generic.List[object]]::new()

foreach ($end in $selectedAccepted) {
    $requestId = Get-IntToken $end.Text 'request_id'
    $turn = Get-IntToken $end.Text 'turn'
    if ($null -ne $requestId) { $requests += $requestId }
    if ($null -ne $turn) { $turns += $turn }

    $pre = @($records | Where-Object {
            ($_.Index -lt $end.Index) -and
            ($null -eq $requestId -or $_.Text -match ('request_id=' + [regex]::Escape([string]$requestId) + '\b')) -and
            $_.Text -match 'MP2B_END_TURN_REVALIDATED\b.*decision=Safe\b'
        } | Select-Object -Last 1)
    if ($pre.Count -eq 1) { $safeRevalidationEvidence.Add($pre[0]) }

    $native = @($records | Where-Object {
            ($_.Index -lt $end.Index) -and
            ($null -eq $requestId -or $_.Text -match ('request_id=' + [regex]::Escape([string]$requestId) + '\b')) -and
            $_.Text -match 'NATIVE_ACTION_CAPTURED\b.*type=EndPlayerTurnAction\b'
        } | Select-Object -Last 1)
    if ($native.Count -eq 1) { $nativeEndTurnEvidence.Add($native[0]) }

    $boundary = @($records | Where-Object {
            ($_.Index -gt $end.Index) -and
            $_.Text -match 'MP_REACTIVE_TURN_BOUNDARY\b.*fresh_probe=true.*fresh_capture=true'
        } | Select-Object -First 1)
    if ($boundary.Count -eq 1) { $boundaryEvidence.Add($boundary[0]) }

    $fresh = @($records | Where-Object {
            ($_.Index -gt $end.Index) -and
            $_.Text -match 'MP_REACTIVE_FRESH_SEARCH\b.*after_safe_end_turn=true' -and
            ($null -eq $requestId -or $_.Text -match ('previous_end_turn_request_id=' + [regex]::Escape([string]$requestId) + '\b'))
        } | Select-Object -First 1)
    $nextTurn = if ($null -eq $turn) { $null } else { $turn + 1 }
    $reuse = @($records | Where-Object {
            ($_.Index -gt $end.Index) -and
            $_.Text -match '\[CombatSolver/Test\] MP_LOCAL_XTURN_CONTINUATION_REUSED\b' -and
            $_.Text -match '\blocal_state_exact=true\b' -and
            $_.Text -match '\breason=exact\b' -and
            ($null -eq $nextTurn -or $_.Text -match ('turn=' + [regex]::Escape([string]$nextTurn) + '\b'))
        } | Select-Object -First 1)

    if ($fresh.Count -eq 1 -and $reuse.Count -eq 1) {
        $ambiguousNextPlan.Add($end)
    } elseif ($fresh.Count -eq 1) {
        $freshSearches.Add($fresh[0])
        $freshIndex = [int]$fresh[0].Index
        $oldAction = @($records | Where-Object {
                ($_.Index -gt $freshIndex) -and
                ($null -eq $requestId -or $_.Text -match ('request_id=' + [regex]::Escape([string]$requestId) + '\b')) -and
                $_.Text -match 'NATIVE_ACTION_CAPTURED\b'
            } | Select-Object -First 1)
        if ($oldAction.Count -eq 1) { $reusedOldRequest.Add($oldAction[0]) }
    } elseif ($reuse.Count -eq 1) {
        $continuationReuses.Add($reuse[0])
    } else {
        $missingNextPlan.Add($end)
    }

    $nextPlanIndex = if ($fresh.Count -eq 1) {
        [int]$fresh[0].Index
    } elseif ($reuse.Count -eq 1) {
        [int]$reuse[0].Index
    } else {
        [int]::MaxValue
    }
    $remoteDelta = @($records | Where-Object {
            ($_.Index -gt $end.Index) -and ($_.Index -lt $nextPlanIndex) -and
            (($_.Text -match '\[CombatSolver/MultiplayerProbe\] OBSERVED\b') -or
             ($_.Text -match 'MP2B_WORLD_CHANGED\b'))
        } | Select-Object -First 1)
    if ($remoteDelta.Count -eq 1) { $remoteDeltaEvidence.Add($remoteDelta[0]) }
}

$safeValid = (
    $selectedAccepted.Count -eq $requiredCount -and
    $safeRevalidationEvidence.Count -eq $requiredCount -and
    $nativeEndTurnEvidence.Count -eq $requiredCount -and
    @($selectedAccepted | Where-Object {
        $_.Text -notmatch 'session_cleared=true\b' -or
        $_.Text -notmatch 'authorization_cleared=true\b' -or
        $_.Text -notmatch 'automatic_end_turn=true\b' -or
        $_.Text -notmatch 'custom_network_api_used=false\b'
    }).Count -eq 0
)
if ($safeValid) {
    Add-Check 'safeEndTurnBoundary' PASS (Join-Evidence $safeRevalidationEvidence)
} elseif ($selectedAccepted.Count -eq 0) {
    Add-Check 'safeEndTurnBoundary' UNVERIFIED '' 'No selected Safe EndTurn session exists.'
} else {
    Add-Check 'safeEndTurnBoundary' FAIL (Join-Evidence @($safeRevalidationEvidence + $nativeEndTurnEvidence)) 'Every accepted EndTurn needs a Safe pre-boundary revalidation, native EndPlayerTurnAction, and cleared authorization markers.'
}

$nextPlanCount = $freshSearches.Count + $continuationReuses.Count
$nextPlanPolicyValid = if ($Smoke -eq 'B') {
    $freshSearches.Count -eq $requiredCount -and $continuationReuses.Count -eq 0
} else {
    $nextPlanCount -eq $requiredCount
}
$nextPlanValid = (
    $selectedAccepted.Count -eq $requiredCount -and
    $nextPlanPolicyValid -and
    $missingNextPlan.Count -eq 0 -and
    $ambiguousNextPlan.Count -eq 0 -and
    $boundaryEvidence.Count -eq $requiredCount -and
    $reusedOldRequest.Count -eq 0
)
if ($nextPlanValid) {
    Add-Check 'freshProbeAndSearch' PASS (Join-Evidence @($boundaryEvidence + $freshSearches + $continuationReuses)) 'Each EndTurn reached a fresh Probe/capture boundary, then either an exact Joint continuation reuse or a fresh search. Smoke B requires fresh search after the intentional remote delta.'
} elseif ($selectedAccepted.Count -eq 0) {
    Add-Check 'freshProbeAndSearch' UNVERIFIED '' 'No accepted EndTurn is available for next-plan validation.'
} else {
    Add-Check 'freshProbeAndSearch' FAIL (Join-Evidence @($boundaryEvidence + $freshSearches + $continuationReuses + $missingNextPlan + $ambiguousNextPlan + $reusedOldRequest)) 'Each EndTurn must be followed by exactly one safe next-plan path: exact Joint continuation reuse when the prediction matches, or fresh search when it does not. Smoke B must fresh-search.'
}

$uniqueRequestCount = @($requests | Sort-Object -Unique).Count
$uniqueTurnCount = @($turns | Sort-Object -Unique).Count
if ($Smoke -eq 'C') {
    if ($uniqueRequestCount -eq $requiredCount -and $uniqueTurnCount -eq $requiredCount) {
        Add-Check 'threeLocalTurns' PASS "requests=$($requests -join ',') turns=$($turns -join ',')" 'Three distinct local Safe Execute sessions and turn identities were observed.'
    } elseif ($selectedAccepted.Count -eq 0) {
        Add-Check 'threeLocalTurns' UNVERIFIED '' 'No three-turn Safe Execute sequence was observed.'
    } else {
        Add-Check 'threeLocalTurns' FAIL (Join-Evidence $selectedAccepted) 'The three-turn carry requires distinct request and local turn identities.'
    }
}

if ($Smoke -eq 'B') {
    if ($remoteDeltaEvidence.Count -gt 0) {
        Add-Check 'interveningRemotePublicChange' PASS (Join-Evidence $remoteDeltaEvidence) 'A public world delta was observed between Safe EndTurn and the fresh search.'
    } elseif ($selectedAccepted.Count -eq 0) {
        Add-Check 'interveningRemotePublicChange' UNVERIFIED '' 'No accepted EndTurn is available for remote-change validation.'
    } else {
        Add-Check 'interveningRemotePublicChange' FAIL '' 'Smoke B requires a public world delta after EndTurn and before fresh search.'
    }
}

$forbidden = @($scopedRecords | Where-Object {
        $_.Text -match 'custom_network_api_used=true\b' -or
        $_.Text -match 'MP2B_DEPLOY_ABORT\b' -or
        $_.Text -match 'MP2B_REMOTE_DELTA_ABORT\b'
    })
if ($forbidden.Count -eq 0) {
    Add-Check 'noStaleOrCustomDeployment' PASS
} elseif ($selectedAccepted.Count -eq 0) {
    Add-Check 'noStaleOrCustomDeployment' UNVERIFIED '' 'No selected Reactive Carry session exists.'
} else {
    Add-Check 'noStaleOrCustomDeployment' FAIL (Join-Evidence $forbidden) 'The selected Reactive Carry run contains an aborted/stale deployment or custom network path.'
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
    smoke = $Smoke
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    requiredLocalTurns = $requiredCount
    acceptedEndTurns = $accepted.Count
    requestId = $RequestId
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'This validator proves CombatSolver journal ordering and identity; it does not replace human GUI confirmation of Host/Client roles or the teammate click.',
        'Smoke B requires a public world delta marker; the validator does not infer private teammate intent.'
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
    Write-Output "MULTIPLAYER_REACTIVE_CARRY_$Smoke`_$status logs=$($resolvedLogs.Count) accepted_end_turns=$($accepted.Count)"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
