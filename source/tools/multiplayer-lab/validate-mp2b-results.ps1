#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

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
        throw "MP-2B log path is not a file: $path"
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

$capability = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_CAPABILITY .*enabled=true .*max_actions=2')
if ($capability.Count -eq 1) {
    Add-Check 'mp2bCapability' PASS (Format-Evidence $capability[0])
} elseif ($capability.Count -eq 0) {
    Add-Check 'mp2bCapability' UNVERIFIED '' 'No MP2B capability marker was observed.'
} else {
    Add-Check 'mp2bCapability' FAIL (Join-Evidence $capability) 'A single client run must expose one MP2B capability marker.'
}

$start = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_START .*action_count=2 .*max_actions=2')
if ($start.Count -eq 1) {
    Add-Check 'twoActionDeploymentStart' PASS (Format-Evidence $start[0])
} elseif ($start.Count -eq 0) {
    Add-Check 'twoActionDeploymentStart' UNVERIFIED '' 'No bounded two-action MP2B deployment start was observed.'
} else {
    Add-Check 'twoActionDeploymentStart' FAIL (Join-Evidence $start) 'A normal MP2B smoke must contain exactly one two-action deployment.'
}

$requestId = $null
$startIndex = -1
if ($start.Count -eq 1 -and $start[0].Text -match 'request_id=(\d+)') {
    $requestId = [int]$Matches[1]
    $startIndex = [int]$start[0].Index
}

$native = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] NATIVE_ACTION_CAPTURED')
$invalidNative = @($native | Where-Object {
    $_.Text -notmatch 'type=PlayCardAction\b' -or
    $_.Text -notmatch 'custom_network_api_used=false\b'
})
$nativeValid = $invalidNative.Count
if ($native.Count -eq 2 -and $nativeValid -eq 0) {
    Add-Check 'nativePlayCardActions' PASS (Join-Evidence $native)
} elseif ($native.Count -eq 0) {
    Add-Check 'nativePlayCardActions' UNVERIFIED '' 'No native MP2B action capture was observed.'
} else {
    Add-Check 'nativePlayCardActions' FAIL (Join-Evidence $native) 'MP2B must capture exactly two native PlayCardAction entries without a custom network API.'
}

$reconciled = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_ACTION_RECONCILED')
$firstReconciliation = @($reconciled | Where-Object Text -Match 'action_index=0 .*decision=SafeToContinue')
$finalReconciliation = @($reconciled | Where-Object Text -Match 'action_index=1 .*decision=ExpectedLocalChange')
if ($reconciled.Count -eq 2 -and $firstReconciliation.Count -eq 1 -and $finalReconciliation.Count -eq 1) {
    Add-Check 'actionRevalidation' PASS (Join-Evidence $reconciled)
} elseif ($reconciled.Count -eq 0) {
    Add-Check 'actionRevalidation' UNVERIFIED '' 'No MP2B action revalidation was observed.'
} else {
    Add-Check 'actionRevalidation' FAIL (Join-Evidence $reconciled) 'MP2B requires a SafeToContinue decision after action 1 and an ExpectedLocalChange decision after action 2.'
}

$end = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_END .*action_count=2 .*end_turn=false')
if ($end.Count -eq 1) {
    if (($end[0].Text -match 'automatic_end_turn=false\b') -and ($end[0].Text -match 'custom_network_api_used=false\b')) {
        Add-Check 'twoActionDeploymentEnd' PASS (Format-Evidence $end[0])
    } else {
        Add-Check 'twoActionDeploymentEnd' FAIL (Format-Evidence $end[0]) 'MP2B completion did not retain the no-EndTurn/native-network-path contract.'
    }
} elseif ($end.Count -eq 0) {
    Add-Check 'twoActionDeploymentEnd' UNVERIFIED '' 'No completed two-action MP2B deployment was observed.'
} else {
    Add-Check 'twoActionDeploymentEnd' FAIL (Join-Evidence $end) 'A normal MP2B smoke must contain exactly one bounded deployment completion.'
}

$deploymentStartIndex = if ($start.Count -eq 1) { [int]$start[0].Index } else { 0 }
$deploymentEndIndex = if ($end.Count -eq 1) { [int]$end[0].Index } else { [int]::MaxValue }
$deploymentRecords = @($records | Where-Object {
        $_.Index -ge $deploymentStartIndex -and $_.Index -le $deploymentEndIndex
    })
$forbidden = @($records | Where-Object {
        $_.Text -match 'EndPlayerTurnAction' -or
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_END .*end_turn=true' -or
        $_.Text -match 'MP2B_REMOTE_DELTA_ABORT'
    }) + @($deploymentRecords | Where-Object {
        $_.Text -match '\[CombatSolver/Test\] DEPLOY_ACTION .*potion=' -or
        $_.Text -match 'DEPLOY_CHOICE_PLAN' -or
        $_.Text -match 'DEPLOY_END_TURN' -or
        $_.Text -match 'Replay'
    })
if ($forbidden.Count -eq 0) {
    Add-Check 'forbiddenActionsAbsent' PASS
} else {
    Add-Check 'forbiddenActionsAbsent' FAIL (Join-Evidence $forbidden) 'Potion, Choice, Replay, EndTurn or remote-abort evidence was observed in the normal two-action smoke.'
}

$worldVersionIncreased = $true
if ($reconciled.Count -eq 2) {
    foreach ($record in $reconciled) {
        if (($record.Text -notmatch 'before_world_version=(\d+)') -or ($record.Text -notmatch 'after_world_version=(\d+)')) {
            $worldVersionIncreased = $false
            continue
        }
        [long]$beforeVersion = $record.Text -replace '^.*before_world_version=(\d+).*$','$1'
        [long]$afterVersion = $record.Text -replace '^.*after_world_version=(\d+).*$','$1'
        if ($afterVersion -le $beforeVersion) { $worldVersionIncreased = $false }
    }
}
if ($reconciled.Count -eq 2 -and $worldVersionIncreased) {
    Add-Check 'worldVersionAttribution' PASS (Join-Evidence $reconciled)
} elseif ($reconciled.Count -eq 0) {
    Add-Check 'worldVersionAttribution' UNVERIFIED '' 'No action-boundary WorldVersion evidence was observed.'
} else {
    Add-Check 'worldVersionAttribution' FAIL (Join-Evidence $reconciled) 'Each revalidated action must advance to a newer stable WorldVersion.'
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
} else {
    Add-Check 'postActionResearch' UNVERIFIED '' 'No fresh debounced search was observed after the bounded MP2B deployment.'
}

if ($null -ne $requestId) {
    $sessionLines = @($records | Where-Object {
            ($_.Text -match 'request_id=(\d+)') -and ($_.Text -match 'MP2B_(?:DEPLOY_START|ACTION_RECONCILED|DEPLOY_END|REMOTE_DELTA_ABORT|DEPLOY_ABORT)')
        })
    $wrongRequest = @($sessionLines | Where-Object {
            $_.Text -notmatch ("request_id=" + [regex]::Escape([string]$requestId) + '\b')
        })
    if ($wrongRequest.Count -eq 0) {
        Add-Check 'sessionIdentity' PASS (Format-Evidence $start[0])
    } else {
        Add-Check 'sessionIdentity' FAIL (Join-Evidence $wrongRequest) 'All MP2B deployment markers must belong to the same explicit execution session.'
    }
} else {
    Add-Check 'sessionIdentity' UNVERIFIED '' 'The MP2B deployment request id was not observed.'
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
    phase = 'MP-2B'
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'This validates CombatSolver runtime evidence from an owned Lab client process.',
        'The normal smoke proves the bounded two-action local path; remote-interference abort requires a separate scenario.',
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
    Write-Output "MULTIPLAYER_MP-2B_$status logs=$($resolvedLogs.Count)"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
