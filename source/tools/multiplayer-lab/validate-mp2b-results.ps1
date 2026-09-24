#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [string]$OutputPath = '',

    [ValidateRange(1, 2147483647)]
    [int]$MinActions = 2,

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
        throw "Bounded Safe Execute log path is not a file: $path"
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
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_CAPABILITY .*enabled=true .*max_actions=(\d+)'
    })
$capabilityValid = $false
if ($capability.Count -eq 1) {
    # U6 production advertises a route-bounded capability. Numeric max_actions remains
    # accepted only so archived MP-2B/MP-2C evidence can still be classified.
    $capabilityValid = $capability[0].Text -match 'action_limit=selected_route\b' -or
        $capability[0].Text -match 'max_actions=\d+\b'
}
if ($capability.Count -eq 1 -and $capabilityValid) {
    Add-Check 'boundedCapability' PASS (Format-Evidence $capability[0])
} elseif ($capability.Count -eq 0) {
    Add-Check 'boundedCapability' UNVERIFIED '' 'No bounded Safe Execute capability marker was observed.'
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
    $deploymentActionCount -ge $MinActions -and
    $deploymentActionCount -le $deploymentMaxActions -and
    ($MaxActions -eq 0 -or $deploymentMaxActions -eq $MaxActions)
if ($start.Count -eq 1 -and $startValid) {
    Add-Check 'boundedDeploymentStart' PASS (Format-Evidence $start[0])
} elseif ($start.Count -eq 0) {
    Add-Check 'boundedDeploymentStart' UNVERIFIED '' 'No bounded Safe Execute deployment start was observed.'
} elseif ($start.Count -gt 1) {
    Add-Check 'boundedDeploymentStart' FAIL (Join-Evidence $start) 'The selected client session must contain exactly one bounded deployment.'
} else {
    Add-Check 'boundedDeploymentStart' FAIL (Format-Evidence $start[0]) (
            "Deployment action_count=$deploymentActionCount, max_actions=$deploymentMaxActions does not satisfy " +
            "MinActions=$MinActions and MaxActions=$MaxActions.")
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

$native = @($sessionRecords | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] NATIVE_ACTION_CAPTURED\b'
    })
$invalidNative = @($native | Where-Object {
        $_.Text -notmatch 'type=PlayCardAction\b' -or
        $_.Text -notmatch 'custom_network_api_used=false\b'
    })
$nativeIndicesValid = Test-ContiguousActionIndices $native $deploymentActionCount
if ($start.Count -eq 1 -and
    $native.Count -eq $deploymentActionCount -and
    $invalidNative.Count -eq 0 -and
    $nativeIndicesValid) {
    Add-Check 'nativePlayCardActions' PASS (Join-Evidence $native)
} elseif ($native.Count -eq 0) {
    Add-Check 'nativePlayCardActions' UNVERIFIED '' 'No native bounded PlayCardAction capture was observed.'
} else {
    Add-Check 'nativePlayCardActions' FAIL (Join-Evidence $native) (
            "Expected exactly $deploymentActionCount contiguous native PlayCardAction entries without a custom network API.")
}

$reconciled = @($sessionRecords | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_ACTION_RECONCILED\b'
    })
$reconciliationValid = Test-ContiguousActionIndices $reconciled $deploymentActionCount
if ($reconciliationValid) {
    for ($index = 0; $index -lt $deploymentActionCount; $index++) {
        $expectedDecision = if ($index -eq $deploymentActionCount - 1) {
            'ExpectedLocalChange'
        } else {
            'SafeToContinue'
        }
        $matchingDecision = @($reconciled | Where-Object {
                $_.Text -match ('action_index=' + $index + '\b') -and
                $_.Text -match ('decision=' + $expectedDecision + '\b')
            })
        if ($matchingDecision.Count -ne 1) {
            $reconciliationValid = $false
            break
        }
    }
}
if ($start.Count -eq 1 -and $reconciled.Count -eq $deploymentActionCount -and $reconciliationValid) {
    Add-Check 'actionRevalidation' PASS (Join-Evidence $reconciled)
} elseif ($reconciled.Count -eq 0) {
    Add-Check 'actionRevalidation' UNVERIFIED '' 'No per-action WorldVersion revalidation was observed.'
} else {
    Add-Check 'actionRevalidation' FAIL (Join-Evidence $reconciled) (
            'Expected one revalidation for every action, with SafeToContinue before the final action and ExpectedLocalChange on the final action.')
}

$end = @($sessionRecords | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_END\b'
    })
$endValid = $false
if ($end.Count -eq 1) {
    $endActionCount = -1
    if ($end[0].Text -match 'action_count=(\d+)') {
        $endActionCount = [int]$Matches[1]
    }
    $endValid = $endActionCount -eq $deploymentActionCount -and
        $end[0].Text -match 'end_turn=false\b' -and
        $end[0].Text -match 'automatic_end_turn=false\b' -and
        $end[0].Text -match 'custom_network_api_used=false\b'
}
if ($start.Count -eq 1 -and $end.Count -eq 1 -and $endValid) {
    Add-Check 'boundedDeploymentEnd' PASS (Format-Evidence $end[0])
} elseif ($end.Count -eq 0) {
    Add-Check 'boundedDeploymentEnd' UNVERIFIED '' 'No completed bounded Safe Execute deployment was observed.'
} elseif ($end.Count -gt 1) {
    Add-Check 'boundedDeploymentEnd' FAIL (Join-Evidence $end) 'The selected client session must contain exactly one bounded deployment completion.'
} else {
    Add-Check 'boundedDeploymentEnd' FAIL (Format-Evidence $end[0]) 'Completion must report the selected action count and retain end_turn=false with native execution.'
}

$forbidden = @()
if ($start.Count -eq 1) {
    $forbidden = @($sessionRecords | Where-Object {
            $_.Text -match 'EndPlayerTurnAction' -or
            $_.Text -match 'custom_network_api_used=true\b' -or
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_END\b.*end_turn=true' -or
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_REMOTE_DELTA_ABORT\b' -or
            $_.Text -match '\[CombatSolver/Test\] DEPLOY_ACTION .*potion=' -or
            $_.Text -match 'DEPLOY_CHOICE_PLAN' -or
            $_.Text -match 'DEPLOY_END_TURN' -or
            $_.Text -match 'Replay' -or
            $_.Text -match 'type=UsePotionAction\b'
        })
}
if ($forbidden.Count -eq 0 -and $start.Count -eq 1) {
    Add-Check 'forbiddenActionsAbsent' PASS
} elseif ($start.Count -eq 0) {
    Add-Check 'forbiddenActionsAbsent' UNVERIFIED '' 'Cannot scope forbidden-action checks without a deployment start.'
} else {
    Add-Check 'forbiddenActionsAbsent' FAIL (Join-Evidence $forbidden) 'Potion, Choice, Replay, EndTurn, remote-abort or custom-network evidence was observed in the normal bounded smoke.'
}

$worldVersionIncreased = $reconciled.Count -eq $deploymentActionCount -and $deploymentActionCount -gt 0
[long]$previousAfterVersion = -1
if ($worldVersionIncreased) {
    foreach ($record in $reconciled) {
        if (($record.Text -notmatch 'before_world_version=(\d+)') -or
            ($record.Text -notmatch 'after_world_version=(\d+)')) {
            $worldVersionIncreased = $false
            break
        }
        [long]$beforeVersion = $record.Text -replace '^.*before_world_version=(\d+).*$','$1'
        [long]$afterVersion = $record.Text -replace '^.*after_world_version=(\d+).*$','$1'
        if ($afterVersion -le $beforeVersion -or $afterVersion -le $previousAfterVersion) {
            $worldVersionIncreased = $false
            break
        }
        $previousAfterVersion = $afterVersion
    }
}
if ($worldVersionIncreased) {
    Add-Check 'worldVersionAttribution' PASS (Join-Evidence $reconciled)
} elseif ($reconciled.Count -eq 0) {
    Add-Check 'worldVersionAttribution' UNVERIFIED '' 'No action-boundary WorldVersion evidence was observed.'
} else {
    Add-Check 'worldVersionAttribution' FAIL (Join-Evidence $reconciled) 'Every revalidated action must advance to a newer stable WorldVersion in log order.'
}

$research = $null
$endIndex = -1
$lastAcceptedWorldVersion = $null
if ($end.Count -eq 1) {
    $endIndex = [int]$end[0].Index
    if ($end[0].Text -match 'last_accepted_world_version=(\d+)') {
        $lastAcceptedWorldVersion = [long]$Matches[1]
    }
}
if ($endIndex -ge 0) {
    foreach ($record in $records) {
        if ($record.Index -le $endIndex) { continue }
        if ($record.Text -match '\[CombatSolver/MultiplayerAdvisor\] SEARCH_DEBOUNCED_START .*world_version=(\d+)') {
            [long]$version = $Matches[1]
            if ($null -eq $lastAcceptedWorldVersion -or $version -ge $lastAcceptedWorldVersion) {
                $research = $record
                break
            }
        }
    }
}
if ($null -ne $research) {
    Add-Check 'postActionResearch' PASS (Format-Evidence $research)
} elseif ($end.Count -eq 0) {
    Add-Check 'postActionResearch' UNVERIFIED '' 'No completed deployment boundary was observed before fresh-search validation.'
} else {
    Add-Check 'postActionResearch' UNVERIFIED '' 'No fresh debounced search was observed after the bounded deployment.'
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
        Add-Check 'sessionIdentity' FAIL (Join-Evidence $wrongRequest) 'All bounded deployment markers must belong to the same explicit execution session.'
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

$phase = if ($deploymentActionCount -ge 3 -or $MinActions -ge 3) { 'MP-2C' } else { 'MP-2B' }
$result = [ordered]@{
    schemaVersion = 2
    phase = $phase
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    requestId = if ($null -ne $observedRequestId) { $observedRequestId } else { $null }
    minActions = $MinActions
    maxActions = if ($MaxActions -gt 0) { $MaxActions } else { $null }
    actionCount = if ($start.Count -eq 1) { $deploymentActionCount } else { $null }
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'This validates CombatSolver runtime evidence from an owned Lab client process.',
        'The normal smoke proves a bounded safe local PlayCard prefix; remote-interference abort requires a separate scenario.',
        'custom_network_api_used=false proves this CombatSolver path used only the native action path; it is not an independent packet capture.'
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
    Write-Output "MULTIPLAYER_$phase`_$status logs=$($resolvedLogs.Count) request_id=$($observedRequestId ?? '-') action_count=$($deploymentActionCount)"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
