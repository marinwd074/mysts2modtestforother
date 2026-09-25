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
$resolvedLogs = [Collections.Generic.List[string]]::new()
$globalIndex = 0
foreach ($pathValue in $LogPath) {
    $path = (Resolve-Path -LiteralPath $pathValue -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "U6 runtime log path is not a file: $path"
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

function Get-IntToken {
    param([string]$Text, [string]$Name)
    if ($Text -match ([regex]::Escape($Name) + '=(\d+)\b')) {
        return [long]$Matches[1]
    }
    return $null
}

function Format-Evidence {
    param($Record)
    if ($null -eq $Record) { return '' }
    return '{0}:{1}: {2}' -f $Record.Path, $Record.LineNumber, $Record.Text
}

$checks = [Collections.Generic.List[object]]::new()
function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'UNVERIFIED')][string]$Status,
        $Evidence = $null,
        [string]$Detail = ''
    )
    $checks.Add([ordered]@{
            name = $Name
            status = $Status
            evidence = if ($null -eq $Evidence) { $null } else { Format-Evidence $Evidence }
            detail = if ([string]::IsNullOrWhiteSpace($Detail)) { $null } else { $Detail }
        })
}

$observe = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_U5_OBSERVE\b' -and
        $_.Text -match 'boundary=teammate_forecast\b' -and
        $_.Text -match 'replan=true\b'
    } | Select-Object -First 1)

$deployEnd = @()
$requestId = $null
$acceptedWorldVersion = $null
if ($observe.Count -eq 1) {
    $deployEnd = @($records | Where-Object {
            $_.Index -lt $observe[0].Index -and
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_END\b' -and
            $_.Text -match 'stop_reason=kind_teammateforecast\b'
        } | Sort-Object Index -Descending | Select-Object -First 1)
}
if ($deployEnd.Count -eq 1) {
    $requestId = Get-IntToken $deployEnd[0].Text 'request_id'
    $acceptedWorldVersion = Get-IntToken $deployEnd[0].Text 'last_accepted_world_version'
}

if ($observe.Count -eq 1 -and $deployEnd.Count -eq 1 -and $null -ne $requestId) {
    Add-Check 'forecastBoundary' PASS $deployEnd[0]
} elseif ($observe.Count -eq 0) {
    Add-Check 'forecastBoundary' UNVERIFIED $null 'No U5 teammate forecast observation was recorded.'
} else {
    Add-Check 'forecastBoundary' FAIL ($observe | Select-Object -First 1) 'The observation is not paired with a route-bounded deployment end.'
}

$remoteDelta = @()
if ($observe.Count -eq 1 -and $null -ne $acceptedWorldVersion) {
    $remoteDelta = @($records | Where-Object {
            $_.Index -gt $observe[0].Index -and
            $_.Text -match '\[CombatSolver/MultiplayerProbe\] OBSERVED\b'
        } | Where-Object {
            $version = Get-IntToken $_.Text 'world_version'
            $null -ne $version -and $version -gt $acceptedWorldVersion
        } | Select-Object -First 1)
}
if ($remoteDelta.Count -eq 1) {
    Add-Check 'remoteWorldAdvance' PASS $remoteDelta[0]
} elseif ($observe.Count -eq 1) {
    Add-Check 'remoteWorldAdvance' UNVERIFIED $observe[0] 'No later compact WorldVersion advance was observed.'
} else {
    Add-Check 'remoteWorldAdvance' UNVERIFIED
}

$oldRequestReuse = @()
if ($null -ne $requestId -and $deployEnd.Count -eq 1) {
    $oldRequestReuse = @($records | Where-Object {
            $_.Index -gt $deployEnd[0].Index -and
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] NATIVE_ACTION_CAPTURED\b' -and
            $_.Text -match ('request_id=' + [regex]::Escape([string]$requestId) + '\b')
        })
}
if ($null -eq $requestId) {
    Add-Check 'oldAuthorizationDormant' UNVERIFIED
} elseif ($oldRequestReuse.Count -eq 0) {
    Add-Check 'oldAuthorizationDormant' PASS $deployEnd[0]
} else {
    Add-Check 'oldAuthorizationDormant' FAIL $oldRequestReuse[0] 'The old request submitted another native action after the forecast boundary.'
}

$freshSearch = @()
$deltaWorldVersion = $null
if ($remoteDelta.Count -eq 1) {
    $deltaWorldVersion = Get-IntToken $remoteDelta[0].Text 'world_version'
    $freshSearch = @($records | Where-Object {
            $_.Index -gt $remoteDelta[0].Index -and
            $_.Text -match '\[CombatSolver/MultiplayerAdvisor\] SEARCH_DEBOUNCED_START\b'
        } | Where-Object {
            $version = Get-IntToken $_.Text 'world_version'
            $null -ne $version -and $version -ge $deltaWorldVersion
        } | Select-Object -First 1)
}
if ($freshSearch.Count -eq 1) {
    Add-Check 'freshSearchAfterRemoteObservation' PASS $freshSearch[0]
} elseif ($remoteDelta.Count -eq 1) {
    Add-Check 'freshSearchAfterRemoteObservation' UNVERIFIED $remoteDelta[0] 'WorldVersion advanced, but no fresh debounced search was captured afterward.'
} else {
    Add-Check 'freshSearchAfterRemoteObservation' UNVERIFIED
}

$newAuthorization = @()
if ($freshSearch.Count -eq 1 -and $null -ne $requestId -and $null -ne $deltaWorldVersion) {
    $newAuthorization = @($records | Where-Object {
            $_.Index -gt $freshSearch[0].Index -and
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_DEPLOY_START\b'
        } | Select-Object -First 1)
}
if ($newAuthorization.Count -eq 1) {
    $newRequestId = Get-IntToken $newAuthorization[0].Text 'request_id'
    $newWorldVersion = Get-IntToken $newAuthorization[0].Text 'search_world_version'
    if ($newRequestId -ne $requestId -and
        $null -ne $newWorldVersion -and
        $newWorldVersion -ge $deltaWorldVersion) {
        Add-Check 'nextAuthorizationConsistency' PASS $newAuthorization[0]
    } else {
        Add-Check 'nextAuthorizationConsistency' FAIL $newAuthorization[0] 'The next deployment reused the old request or a stale WorldVersion.'
    }
} elseif ($freshSearch.Count -eq 1) {
    Add-Check 'nextAuthorizationConsistency' PASS $freshSearch[0] 'No later deployment was captured; a second deployment is not required for U6 closure.'
} else {
    Add-Check 'nextAuthorizationConsistency' UNVERIFIED
}

$failed = @($checks | Where-Object { $_.status -eq 'FAIL' })
$unverified = @($checks | Where-Object { $_.status -eq 'UNVERIFIED' })
$status = if ($failed.Count -gt 0) {
    'FAIL'
} elseif ($unverified.Count -gt 0) {
    'UNVERIFIED'
} else {
    'PASS'
}

$result = [ordered]@{
    schemaVersion = 1
    phase = 'U6-runtime-closure'
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    requestId = $requestId
    acceptedWorldVersion = $acceptedWorldVersion
    remoteWorldVersion = $deltaWorldVersion
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'This proves only the locally observable Host/Client chain around a real teammate state change.',
        'It does not packet-capture or control the remote player; the operator must deliberately perform the teammate action used for the smoke.',
        'Offline/pinned U5 order semantics remain separate evidence.'
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
    Write-Output "MULTIPLAYER_U6_$status logs=$($resolvedLogs.Count) request_id=$($requestId ?? '-')"
    foreach ($check in $checks) {
        $detail = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $detail)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
