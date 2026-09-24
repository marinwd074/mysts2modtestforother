#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [string]$OutputPath = '',

    [ValidateRange(1, 2147483647)]
    [int]$MinCompletedActions = 1,

    [ValidateRange(0, 2147483647)]
    [int]$MaxActions = 0,

    [int]$RequestId = 0,

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
        throw "Bounded Safe Execute interference log path is not a file: $path"
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

function Test-ContiguousActionIndices {
    param(
        [object[]]$Items,
        [int]$ExpectedCount
    )
    if ($Items.Count -ne $ExpectedCount) { return $false }
    for ($index = 0; $index -lt $ExpectedCount; $index++) {
        if ($Items[$index].Text -notmatch 'action_index=(\d+)\b') { return $false }
        if ([int]$Matches[1] -ne $index) { return $false }
    }
    return $true
}

$capability = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_CAPABILITY .*enabled=true' -and
        ($_.Text -match 'action_limit=route_bounded\b' -or $_.Text -match 'max_actions=(\d+)')
    })
$capabilityValid = $false
if ($capability.Count -eq 1) {
    if ($capability[0].Text -match 'action_limit=route_bounded\b') {
        $capabilityValid = $true
    } elseif ($capability[0].Text -match 'max_actions=(\d+)') {
        # Historical evidence used a fixed capability ceiling. Current production reports
        # route_bounded here and the concrete finite capacity at MP2B_DEPLOY_START.
        $capabilityValid = [int]$Matches[1] -gt 0
    }
}
if ($capability.Count -eq 1 -and $capabilityValid) {
    Add-Check 'boundedCapability' PASS (Format-Evidence $capability[0])
} elseif ($capability.Count -eq 0) {
    Add-Check 'boundedCapability' UNVERIFIED '' 'No route-bounded Safe Execute capability marker was observed.'
} else {
    Add-Check 'boundedCapability' FAIL (Join-Evidence $capability) 'A single route-bounded Safe Execute capability marker is required.'
}

$startPattern = '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_START\b'
$startCandidates = @($records | Where-Object { $_.Text -match $startPattern })
if ($RequestId -gt 0) {
    $start = @($startCandidates | Where-Object {
            $_.Text -match ('request_id=' + [regex]::Escape([string]$RequestId) + '\b')
        })
} else {
    $start = @($startCandidates)
}

$observedRequestId = $null
$startIndex = -1
$deploymentActionCount = 0
$deploymentMaxActions = 0
if ($start.Count -eq 1) {
    $startIndex = [int]$start[0].Index
    if ($start[0].Text -match 'request_id=(\d+)') {
        $observedRequestId = [int]$Matches[1]
    }
    if ($start[0].Text -match 'action_count=(\d+)') {
        $deploymentActionCount = [int]$Matches[1]
    }
    if ($start[0].Text -match 'max_actions=(\d+)') {
        $deploymentMaxActions = [int]$Matches[1]
    }
}

$startValid = $start.Count -eq 1 -and
    $deploymentActionCount -gt 0 -and
    $deploymentActionCount -le $deploymentMaxActions -and
    ($MaxActions -eq 0 -or $deploymentMaxActions -eq $MaxActions)
if ($start.Count -eq 1 -and $startValid) {
    Add-Check 'boundedDeploymentStart' PASS (Format-Evidence $start[0])
} elseif ($start.Count -eq 0) {
    Add-Check 'boundedDeploymentStart' UNVERIFIED '' 'No bounded Safe Execute deployment start was observed.'
} elseif ($start.Count -gt 1) {
    Add-Check 'boundedDeploymentStart' FAIL (Join-Evidence $start) 'The selected interference session must contain exactly one bounded deployment.'
} else {
    Add-Check 'boundedDeploymentStart' FAIL (Format-Evidence $start[0]) 'The deployment action count and finite ceiling are inconsistent.'
}

$sessionRecords = @()
if ($start.Count -eq 1) {
    $nextStart = @($records | Where-Object {
            $_.Index -gt $startIndex -and $_.Text -match $startPattern
        } | Sort-Object Index | Select-Object -First 1)
    $sessionEndIndex = if ($nextStart.Count -eq 1) {
        [int]$nextStart[0].Index - 1
    } else {
        [int]::MaxValue
    }
    $sessionRecords = @($records | Where-Object {
            $_.Index -ge $startIndex -and $_.Index -le $sessionEndIndex
        })
}

$abort = @($sessionRecords | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_REMOTE_DELTA_ABORT\b'
    })
$abortRequestMatches = @($abort | Where-Object {
        $null -ne $observedRequestId -and
        $_.Text -match ('request_id=' + [regex]::Escape([string]$observedRequestId) + '\b')
    })
$completedActions = -1
if ($abort.Count -eq 1 -and $abort[0].Text -match 'completed_actions=(\d+)') {
    $completedActions = [int]$Matches[1]
}
$abortValid = $abort.Count -eq 1 -and
    $abortRequestMatches.Count -eq 1 -and
    $completedActions -ge $MinCompletedActions -and
    $completedActions -lt $deploymentActionCount
if ($abort.Count -eq 1 -and $abortValid) {
    Add-Check 'remoteAbort' PASS (Format-Evidence $abort[0])
} elseif ($abort.Count -eq 0) {
    Add-Check 'remoteAbort' UNVERIFIED '' 'No MP2B_REMOTE_DELTA_ABORT was observed.'
} else {
    Add-Check 'remoteAbort' FAIL (Join-Evidence $abort) (
            "The abort must belong to the deployment request, complete at least $MinCompletedActions action(s), and stop before the next planned action.")
}

$native = @($sessionRecords | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] NATIVE_ACTION_CAPTURED\b'
    })
$invalidNative = @($native | Where-Object {
        $_.Text -notmatch 'type=PlayCardAction\b' -or
        $_.Text -notmatch 'custom_network_api_used=false\b'
    })
$nativeIndicesValid = Test-ContiguousActionIndices $native $completedActions
if ($abortValid -and
    $native.Count -eq $completedActions -and
    $invalidNative.Count -eq 0 -and
    $nativeIndicesValid) {
    Add-Check 'completedNativeActions' PASS (Join-Evidence $native)
} elseif ($native.Count -eq 0) {
    Add-Check 'completedNativeActions' UNVERIFIED '' 'No native completed action was observed before the remote abort.'
} elseif ($abort.Count -eq 0) {
    Add-Check 'completedNativeActions' UNVERIFIED '' 'A remote abort is required before completed-action count can be validated.'
} else {
    Add-Check 'completedNativeActions' FAIL (Join-Evidence $native) (
            "The abort must have exactly $completedActions contiguous native PlayCardAction capture(s), with no custom network API.")
}

$reconciled = @($sessionRecords | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_ACTION_RECONCILED\b'
    })
$reconciliationValid = Test-ContiguousActionIndices $reconciled $completedActions
if ($reconciliationValid) {
    foreach ($record in $reconciled) {
        if ($record.Text -notmatch 'decision=(?:SafeToContinue|RemoteOrUnknownChange)\b') {
            $reconciliationValid = $false
            break
        }
    }
}
if ($abortValid -and $reconciled.Count -eq $completedActions -and $reconciliationValid) {
    Add-Check 'completedActionRevalidation' PASS (Join-Evidence $reconciled)
} elseif ($reconciled.Count -eq 0) {
    Add-Check 'completedActionRevalidation' UNVERIFIED '' 'No completed-action revalidation was observed before the remote abort.'
} elseif ($abort.Count -eq 0) {
    Add-Check 'completedActionRevalidation' UNVERIFIED '' 'A remote abort is required before completed-action count can be validated.'
} else {
    Add-Check 'completedActionRevalidation' FAIL (Join-Evidence $reconciled) 'Every completed action must have a contiguous revalidation before the remote abort.'
}

$abortIndex = if ($abort.Count -eq 1) { [int]$abort[0].Index } else { [int]::MaxValue }
$deploymentRecords = @($sessionRecords | Where-Object {
        $_.Index -ge $startIndex -and $_.Index -le $abortIndex
    })
$forbidden = @($deploymentRecords | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_END\b' -or
        $_.Text -match 'custom_network_api_used=true\b' -or
        $_.Text -match 'EndPlayerTurnAction' -or
        $_.Text -match 'DEPLOY_END_TURN' -or
        $_.Text -match '\[CombatSolver/Test\] DEPLOY_ACTION .*potion=' -or
        $_.Text -match 'DEPLOY_CHOICE_PLAN' -or
        $_.Text -match 'Replay' -or
        $_.Text -match 'type=UsePotionAction\b'
    })
if ($completedActions -ge 0) {
    $forbidden += @($sessionRecords | Where-Object {
            $_.Text -match ('NATIVE_ACTION_CAPTURED\b.*action_index=' + $completedActions + '\b')
        })
}
if ($abortValid -and $forbidden.Count -eq 0) {
    Add-Check 'noNextOrForbiddenAction' PASS
} elseif ($forbidden.Count -eq 0 -and $abort.Count -eq 0) {
    Add-Check 'noNextOrForbiddenAction' UNVERIFIED '' 'Cannot prove the no-next-action boundary without a remote abort.'
} else {
    Add-Check 'noNextOrForbiddenAction' FAIL (Join-Evidence $forbidden) 'The remote abort path must not complete the deployment, end the turn, use a forbidden action, or capture the next action.'
}

$abortIndex = if ($abort.Count -eq 1) { [int]$abort[0].Index } else { -1 }
$research = $null
if ($abortIndex -ge 0) {
    foreach ($record in $records) {
        if ($record.Index -le $abortIndex) { continue }
        if ($record.Text -match '\[CombatSolver/MultiplayerAdvisor\] SEARCH_DEBOUNCED_START .*world_version=(\d+)') {
            $research = $record
            break
        }
    }
}
if ($null -ne $research) {
    Add-Check 'postAbortResearch' PASS (Format-Evidence $research)
} elseif ($abort.Count -eq 0) {
    Add-Check 'postAbortResearch' UNVERIFIED '' 'Cannot verify a fresh search before the remote abort is observed.'
} else {
    Add-Check 'postAbortResearch' UNVERIFIED '' 'No fresh debounced search was observed after the remote abort.'
}

if ($null -ne $observedRequestId) {
    $sessionLines = @($sessionRecords | Where-Object {
            ($_.Text -match 'request_id=(\d+)') -and
            ($_.Text -match 'MP2B_(?:DEPLOY_START|NATIVE_ACTION_CAPTURED|ACTION_RECONCILED|DEPLOY_END|REMOTE_DELTA_ABORT|DEPLOY_ABORT)')
        })
    $wrongRequest = @($sessionLines | Where-Object {
            $_.Text -notmatch ('request_id=' + [regex]::Escape([string]$observedRequestId) + '\b')
        })
    if ($wrongRequest.Count -eq 0) {
        Add-Check 'sessionIdentity' PASS (Format-Evidence $start[0])
    } else {
        Add-Check 'sessionIdentity' FAIL (Join-Evidence $wrongRequest) 'All interference markers must belong to the same explicit execution session.'
    }
} else {
    Add-Check 'sessionIdentity' UNVERIFIED '' 'The bounded deployment request id was not observed.'
}

$status = if (@($checks | Where-Object status -eq 'FAIL').Count -gt 0) {
    'FAIL'
} elseif (@($checks | Where-Object status -eq 'UNVERIFIED').Count -gt 0) {
    'UNVERIFIED'
} else {
    'PASS'
}

$phase = if ($completedActions -ge 2 -or $MinCompletedActions -ge 2) {
    'MP-2C-remote-interference'
} else {
    'MP-2B-remote-interference'
}
$result = [ordered]@{
    schemaVersion = 2
    phase = $phase
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    requestId = if ($null -ne $observedRequestId) { $observedRequestId } else { $null }
    minCompletedActions = $MinCompletedActions
    maxActions = if ($MaxActions -gt 0) { $MaxActions } else { $null }
    plannedActionCount = if ($start.Count -eq 1) { $deploymentActionCount } else { $null }
    completedActions = if ($completedActions -ge 0) { $completedActions } else { $null }
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'This validates a user-controlled remote-interference scenario from owned Lab client logs.',
        'The remote action itself is not independently packet-captured; MP2B_REMOTE_DELTA_ABORT is the runtime attribution boundary.',
        'A normal bounded N-action smoke must be validated separately with validate-mp2b-results.ps1.'
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
    Write-Output "MULTIPLAYER_$phase`_$status logs=$($resolvedLogs.Count) request_id=$($observedRequestId ?? '-') completed_actions=$($completedActions)"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
