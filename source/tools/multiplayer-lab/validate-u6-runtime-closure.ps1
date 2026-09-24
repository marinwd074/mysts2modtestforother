#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$LogPath,

    [string]$OutputPath = '',

    [int]$RequestId = 0,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$path = (Resolve-Path -LiteralPath $LogPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "U6 runtime-closure log path is not a file: $path"
}

$records = [Collections.Generic.List[object]]::new()
$lineNumber = 0
foreach ($line in Get-Content -LiteralPath $path) {
    $lineNumber++
    $records.Add([pscustomobject]@{
            Index = $records.Count
            LineNumber = $lineNumber
            Text = [string]$line
        })
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
    return '{0}:{1}: {2}' -f $path, $Record.LineNumber, $Record.Text
}

function Join-Evidence {
    param([object[]]$Items)
    return ($Items | ForEach-Object { Format-Evidence $_ } | Join-String -Separator ' | ')
}

function Get-RequestId {
    param($Record)
    if ($null -ne $Record -and $Record.Text -match 'request_id=(\d+)\b') {
        return [int]$Matches[1]
    }
    return 0
}

function Test-ContiguousIndices {
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
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_CAPABILITY\b' -and
        $_.Text -match 'enabled=true\b' -and
        $_.Text -match 'action_limit=route_bounded\b'
    })
if ($capability.Count -ge 1) {
    Add-Check 'routeBoundedCapability' PASS (Format-Evidence $capability[-1])
} else {
    Add-Check 'routeBoundedCapability' UNVERIFIED '' 'Current route-bounded capability marker was not observed.'
}

$abortCandidates = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_(?:REMOTE_DELTA_ABORT|DEPLOY_ABORT)\b' -and
        ($_.Text -match 'reason=remote_or_unknown_change\b' -or
         $_.Text -match 'reason=world_version_not_accepted\b')
    })
if ($RequestId -gt 0) {
    $abortCandidates = @($abortCandidates | Where-Object {
            $_.Text -match ('request_id=' + [regex]::Escape([string]$RequestId) + '\b')
        })
}
$abort = if ($abortCandidates.Count -gt 0) { $abortCandidates[-1] } else { $null }
$observedRequestId = Get-RequestId $abort
if ($null -eq $abort) {
    Add-Check 'remoteInvalidation' UNVERIFIED '' 'No current remote-change/world-version invalidation was observed.'
} else {
    Add-Check 'remoteInvalidation' PASS (Format-Evidence $abort)
}

$start = $null
if ($observedRequestId -gt 0 -and $null -ne $abort) {
    $starts = @($records | Where-Object {
            $_.Index -lt $abort.Index -and
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_START\b' -and
            $_.Text -match ('request_id=' + [regex]::Escape([string]$observedRequestId) + '\b')
        })
    if ($starts.Count -gt 0) { $start = $starts[-1] }
}

$plannedActionCount = 0
$maxActions = 0
if ($null -ne $start) {
    if ($start.Text -match 'action_count=(\d+)\b') { $plannedActionCount = [int]$Matches[1] }
    if ($start.Text -match 'max_actions=(\d+)\b') { $maxActions = [int]$Matches[1] }
}
if ($null -ne $start -and $plannedActionCount -ge 2 -and $maxActions -ge $plannedActionCount) {
    Add-Check 'finitePlannedSuffix' PASS (Format-Evidence $start)
} elseif ($null -eq $start) {
    Add-Check 'finitePlannedSuffix' UNVERIFIED '' 'No deployment start was found for the invalidated request.'
} else {
    Add-Check 'finitePlannedSuffix' FAIL (Format-Evidence $start) 'U6 requires a real stale suffix opportunity: at least two planned local actions.'
}

$session = @()
if ($null -ne $start -and $null -ne $abort) {
    $session = @($records | Where-Object { $_.Index -ge $start.Index -and $_.Index -le $abort.Index })
}

$completedActions = -1
if ($null -ne $abort -and $abort.Text -match 'completed_actions=(\d+)\b') {
    $completedActions = [int]$Matches[1]
}

$native = @($session | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] NATIVE_ACTION_CAPTURED\b'
    })
$nativeValid = $completedActions -ge 1 -and
    (Test-ContiguousIndices $native $completedActions) -and
    @($native | Where-Object { $_.Text -notmatch 'custom_network_api_used=false\b' }).Count -eq 0
$localIds = @($native | ForEach-Object {
        if ($_.Text -match 'local_net_id=(\d+)\b') { $Matches[1] }
    } | Select-Object -Unique)
if ($nativeValid -and $localIds.Count -eq 1) {
    Add-Check 'localNativeOwnership' PASS (Join-Evidence $native)
} elseif ($native.Count -eq 0) {
    Add-Check 'localNativeOwnership' UNVERIFIED '' 'No completed local native action was observed before invalidation.'
} else {
    Add-Check 'localNativeOwnership' FAIL (Join-Evidence $native) 'Completed actions must be contiguous local PlayCardAction captures from one local player and use no custom network API.'
}

$pre = @($session | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] U1_PRE_ACTION_PROBE\b'
    })
$expected = @($session | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] U1_EXPECTED_POST_STATE\b'
    })
$post = @($session | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] U1_POST_STATE_COMPARE\b'
    })
$reconciled = @($session | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_ACTION_RECONCILED\b'
    })

$u1CompletedValid = $completedActions -ge 1 -and
    @($pre | Where-Object {
            $_.Text -match 'action_index=(\d+)\b' -and [int]$Matches[1] -lt $completedActions
        }).Count -eq $completedActions -and
    $expected.Count -ge $completedActions -and
    (Test-ContiguousIndices $post $completedActions) -and
    (Test-ContiguousIndices $reconciled $completedActions)
if ($u1CompletedValid) {
    Add-Check 'u1CompletedActionChain' PASS (
        (Join-Evidence $pre) + ' | ' + (Join-Evidence $expected) + ' | ' +
        (Join-Evidence $post) + ' | ' + (Join-Evidence $reconciled))
} elseif ($completedActions -lt 1) {
    Add-Check 'u1CompletedActionChain' UNVERIFIED '' 'No completed action exists to validate the U1 semantic chain.'
} else {
    Add-Check 'u1CompletedActionChain' FAIL (Join-Evidence ($pre + $expected + $post + $reconciled)) 'Each completed action must have pre-probe, production expected post-state, live post-state compare, and reconciliation evidence.'
}

$remoteMismatchPath = $false
if ($null -ne $abort -and $abort.Text -match 'reason=remote_or_unknown_change\b') {
    $remoteMismatchPost = @($post | Where-Object { $_.Text -match 'remote_match=false\b' })
    $remoteMismatchReconcile = @($reconciled | Where-Object {
            $_.Text -match 'decision=RemoteOrUnknownChange\b' -and
            $_.Text -match 'semantic_remote_match=false\b'
        })
    $remoteMismatchPath = $remoteMismatchPost.Count -ge 1 -and $remoteMismatchReconcile.Count -ge 1
}

$preActionWorldChangePath = $false
if ($null -ne $abort -and $abort.Text -match 'reason=world_version_not_accepted\b' -and $completedActions -ge 1) {
    $nextProbe = @($session | Where-Object {
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] U1_PRE_ACTION_PROBE\b' -and
            $_.Text -match ('action_index=' + $completedActions + '\b') -and
            $_.Text -match 'changed=true\b'
        })
    $nextNative = @($session | Where-Object {
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] NATIVE_ACTION_CAPTURED\b' -and
            $_.Text -match ('action_index=' + $completedActions + '\b')
        })
    $preActionWorldChangePath = $nextProbe.Count -ge 1 -and $nextNative.Count -eq 0
}

if ($remoteMismatchPath -or $preActionWorldChangePath) {
    $pathName = if ($remoteMismatchPath) { 'post_action_remote_mismatch' } else { 'pre_action_world_version_change' }
    Add-Check 'remoteChangeDetectedBeforeStaleSubmit' PASS (Format-Evidence $abort) "path=$pathName"
} elseif ($null -eq $abort) {
    Add-Check 'remoteChangeDetectedBeforeStaleSubmit' UNVERIFIED '' 'No invalidation path is available to classify.'
} else {
    Add-Check 'remoteChangeDetectedBeforeStaleSubmit' FAIL (Format-Evidence $abort) 'Expected either semantic remote mismatch after a completed action or changed=true on the next pre-action probe before native submit.'
}

$staleNative = @()
if ($completedActions -ge 0) {
    $staleNative = @($records | Where-Object {
            $_.Index -gt $abort.Index -and
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] NATIVE_ACTION_CAPTURED\b' -and
            $_.Text -match ('request_id=' + [regex]::Escape([string]$observedRequestId) + '\b')
        })
}
if ($null -ne $abort -and $staleNative.Count -eq 0) {
    Add-Check 'oldSuffixRejected' PASS
} elseif ($null -eq $abort) {
    Add-Check 'oldSuffixRejected' UNVERIFIED '' 'Cannot prove stale-suffix rejection without an invalidation.'
} else {
    Add-Check 'oldSuffixRejected' FAIL (Join-Evidence $staleNative) 'The invalidated request submitted another native action.'
}

$freshSearch = $null
if ($null -ne $abort) {
    foreach ($record in $records) {
        if ($record.Index -le $abort.Index) { continue }
        if ($record.Text -match '\[CombatSolver/MultiplayerAdvisor\] SEARCH_DEBOUNCED_START\b') {
            $freshSearch = $record
            break
        }
    }
}
if ($null -ne $freshSearch) {
    Add-Check 'freshReplan' PASS (Format-Evidence $freshSearch)
} elseif ($null -eq $abort) {
    Add-Check 'freshReplan' UNVERIFIED '' 'Cannot verify a fresh search before invalidation occurs.'
} else {
    Add-Check 'freshReplan' UNVERIFIED '' 'No fresh debounced search was observed after invalidation.'
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
    phase = 'U6-runtime-closure'
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    requestId = if ($observedRequestId -gt 0) { $observedRequestId } else { $null }
    plannedActionCount = if ($plannedActionCount -gt 0) { $plannedActionCount } else { $null }
    completedActions = if ($completedActions -ge 0) { $completedActions } else { $null }
    invalidationReason = if ($null -ne $abort -and $abort.Text -match 'reason=([^ ]+)') { $Matches[1] } else { $null }
    logFile = $path
    checks = @($checks)
    limitations = @(
        'PASS proves the current client rejected an old local suffix after a real multiplayer observation and started a fresh search.',
        'Native ownership is inferred from CombatSolver native action captures and local_net_id; this is not a network packet capture.',
        'This does not replace separate card-specific Choice or draw-chain runtime smoke when those behaviors are under investigation.'
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
    Write-Output "U6_RUNTIME_CLOSURE_$status request_id=$($observedRequestId) completed_actions=$completedActions"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
