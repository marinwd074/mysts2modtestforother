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
foreach ($pathValue in $LogPath) {
    $path = (Resolve-Path -LiteralPath $pathValue -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "MP-2B interference log path is not a file: $path"
    }
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
    Add-Check 'twoActionDeploymentStart' FAIL (Join-Evidence $start) 'The interference run must contain exactly one bounded deployment.'
}

$requestId = $null
if ($start.Count -eq 1 -and $start[0].Text -match 'request_id=(\d+)') {
    $requestId = [int]$Matches[1]
}

$native = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] NATIVE_ACTION_CAPTURED')
$nativeAction0 = @($native | Where-Object Text -Match 'action_index=0\b')
$nativeAction1 = @($native | Where-Object Text -Match 'action_index=1\b')
$invalidNative = @($native | Where-Object {
        $_.Text -notmatch 'type=PlayCardAction\b' -or
        $_.Text -notmatch 'custom_network_api_used=false\b'
    })
if ($native.Count -eq 1 -and $nativeAction0.Count -eq 1 -and $nativeAction1.Count -eq 0 -and $invalidNative.Count -eq 0) {
    Add-Check 'singleNativeActionBeforeAbort' PASS (Format-Evidence $native[0])
} elseif ($native.Count -eq 0) {
    Add-Check 'singleNativeActionBeforeAbort' UNVERIFIED '' 'No native first action was observed.'
} else {
    Add-Check 'singleNativeActionBeforeAbort' FAIL (Join-Evidence $native) 'Remote interference must stop before a second native PlayCardAction.'
}

$reconciled = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_ACTION_RECONCILED')
$firstReconciliation = @($reconciled | Where-Object {
        $_.Text -match 'action_index=0\b' -and
        $_.Text -match 'decision=(?:SafeToContinue|RemoteOrUnknownChange)\b'
    })
$unexpectedReconciliation = @($reconciled | Where-Object Text -NotMatch 'action_index=0\b')
if ($reconciled.Count -eq 1 -and $firstReconciliation.Count -eq 1 -and $unexpectedReconciliation.Count -eq 0) {
    Add-Check 'firstActionRevalidation' PASS (Format-Evidence $reconciled[0])
} elseif ($reconciled.Count -eq 0) {
    Add-Check 'firstActionRevalidation' UNVERIFIED '' 'No first-action revalidation was observed.'
} else {
    Add-Check 'firstActionRevalidation' FAIL (Join-Evidence $reconciled) 'The interference run must revalidate action 1 and must not revalidate a second action.'
}

$abort = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_REMOTE_DELTA_ABORT')
$abortRequestMatches = @($abort | Where-Object {
        $null -ne $requestId -and $_.Text -match ("request_id=" + [regex]::Escape([string]$requestId) + '\b')
    })
$abortCompletedOne = @($abort | Where-Object Text -Match 'completed_actions=1\b')
if ($abort.Count -eq 1 -and $abortRequestMatches.Count -eq 1 -and $abortCompletedOne.Count -eq 1) {
    Add-Check 'remoteAbort' PASS (Format-Evidence $abort[0])
} elseif ($abort.Count -eq 0) {
    Add-Check 'remoteAbort' UNVERIFIED '' 'No MP2B_REMOTE_DELTA_ABORT was observed.'
} else {
    Add-Check 'remoteAbort' FAIL (Join-Evidence $abort) 'The abort must belong to the deployment request and report one completed action.'
}

$forbidden = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_END' -or
        $_.Text -match 'DEPLOY_END_TURN' -or
        $_.Text -match '\[CombatSolver/Test\] DEPLOY_ACTION .*potion=' -or
        $_.Text -match 'DEPLOY_CHOICE_PLAN' -or
        $_.Text -match 'Replay' -or
        $_.Text -match 'custom_network_api_used=true\b' -or
        $_.Text -match 'NATIVE_ACTION_CAPTURED .*action_index=1\b'
    })
if ($forbidden.Count -eq 0) {
    Add-Check 'noSecondOrForbiddenAction' PASS
} else {
    Add-Check 'noSecondOrForbiddenAction' FAIL (Join-Evidence $forbidden) 'The abort path must not complete, end the turn, use a potion/choice/replay, use a custom network API, or capture action 2.'
}

$abortIndex = -1
if ($abort.Count -eq 1) {
    $abortIndex = [int]$abort[0].Index
}
$research = @($records | Where-Object {
        $abortIndex -ge 0 -and
        $_.Index -gt $abortIndex -and
        $_.Text -match '\[CombatSolver/MultiplayerAdvisor\] SEARCH_DEBOUNCED_START .*world_version=\d+'
    })
if ($research.Count -gt 0) {
    Add-Check 'postAbortResearch' PASS (Format-Evidence $research[0])
} elseif ($abort.Count -eq 0) {
    Add-Check 'postAbortResearch' UNVERIFIED '' 'Cannot verify a fresh search before the remote abort is observed.'
} else {
    Add-Check 'postAbortResearch' UNVERIFIED '' 'No fresh debounced search was observed after the remote abort.'
}

if ($null -ne $requestId) {
    $sessionLines = @($records | Where-Object {
            ($_.Text -match 'request_id=(\d+)') -and
            ($_.Text -match 'MP2B_(?:DEPLOY_START|NATIVE_ACTION_CAPTURED|ACTION_RECONCILED|REMOTE_DELTA_ABORT|DEPLOY_ABORT)')
        })
    $wrongRequest = @($sessionLines | Where-Object {
            $_.Text -notmatch ("request_id=" + [regex]::Escape([string]$requestId) + '\b')
        })
    if ($wrongRequest.Count -eq 0) {
        Add-Check 'sessionIdentity' PASS (Format-Evidence $start[0])
    } else {
        Add-Check 'sessionIdentity' FAIL (Join-Evidence $wrongRequest) 'All interference markers must belong to the same execution session.'
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
    phase = 'MP-2B-remote-interference'
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($LogPath | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
    checks = @($checks)
    limitations = @(
        'This validates a user-controlled remote-interference scenario from owned Lab client logs.',
        'The remote action itself is not independently packet-captured; MP2B_REMOTE_DELTA_ABORT is the runtime attribution boundary.',
        'A normal two-action smoke must be validated separately with validate-mp2b-results.ps1.'
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
    Write-Output "MULTIPLAYER_MP-2B_REMOTE_ABORT_$status logs=$($LogPath.Count)"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
