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
        throw "MP-2A log path is not a file: $path"
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

$lab = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] LAB_CAPABILITY .*enabled=true')
if ($lab.Count -gt 0) {
    Add-Check 'labCapability' PASS (Format-Evidence $lab[0])
} else {
    Add-Check 'labCapability' UNVERIFIED '' 'No Lab-only Safe Execute capability marker was observed.'
}

$start = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] MP2A_DEPLOY_START .*action_count=1')
if ($start.Count -eq 1) {
    Add-Check 'singleActionDeploymentStart' PASS (Format-Evidence $start[0])
} elseif ($start.Count -eq 0) {
    Add-Check 'singleActionDeploymentStart' UNVERIFIED '' 'No one-action MP-2A deployment start was observed.'
} else {
    Add-Check 'singleActionDeploymentStart' FAIL ($start | ForEach-Object { Format-Evidence $_ } | Join-String -Separator ' | ') 'A single-card smoke must contain exactly one MP-2A deployment.'
}

$native = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] NATIVE_ACTION_CAPTURED')
if ($native.Count -eq 1) {
    if ($native[0].Text -match 'type=PlayCardAction\b' -and
        $native[0].Text -match 'custom_network_api_used=false\b') {
        Add-Check 'nativePlayCardAction' PASS (Format-Evidence $native[0])
    } else {
        Add-Check 'nativePlayCardAction' FAIL (Format-Evidence $native[0]) 'Captured action was not an audited native PlayCardAction path.'
    }
} elseif ($native.Count -eq 0) {
    Add-Check 'nativePlayCardAction' UNVERIFIED '' 'No native action capture was observed.'
} else {
    Add-Check 'nativePlayCardAction' FAIL ($native | ForEach-Object { Format-Evidence $_ } | Join-String -Separator ' | ') 'More than one native action was captured.'
}

$end = @($records | Where-Object Text -Match '\[CombatSolver/MultiplayerSafeExecute\] DEPLOY_END .*action_count=1 .*end_turn=false')
if ($end.Count -eq 1) {
    if ($end[0].Text -match 'automatic_end_turn=false\b' -and
        $end[0].Text -match 'custom_network_api_used=false\b') {
        Add-Check 'singleActionDeploymentEnd' PASS (Format-Evidence $end[0])
    } else {
        Add-Check 'singleActionDeploymentEnd' FAIL (Format-Evidence $end[0]) 'MP-2A completion did not retain the no-EndTurn/native-network-path contract.'
    }
} elseif ($end.Count -eq 0) {
    Add-Check 'singleActionDeploymentEnd' UNVERIFIED '' 'No completed one-action MP-2A deployment was observed.'
} else {
    Add-Check 'singleActionDeploymentEnd' FAIL ($end | ForEach-Object { Format-Evidence $_ } | Join-String -Separator ' | ') 'A single-card smoke completed more than one MP-2A deployment.'
}

$forbidden = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Test\] DEPLOY_ACTION .*potion=' -or
        $_.Text -match 'UI_DEPLOYMENT_END_TURN' -or
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] DEPLOY_END .*end_turn=true'
    })
if ($forbidden.Count -eq 0) {
    Add-Check 'forbiddenActionsAbsent' PASS
} else {
    Add-Check 'forbiddenActionsAbsent' FAIL ($forbidden | ForEach-Object { Format-Evidence $_ } | Join-String -Separator ' | ') 'Potion or automatic EndTurn evidence was observed.'
}

$searchWorldVersion = $null
$deployEndIndex = -1
if ($end.Count -eq 1 -and $end[0].Text -match 'search_world_version=(\d+)') {
    $searchWorldVersion = [long]$Matches[1]
    $deployEndIndex = [int]$end[0].Index
}
$worldChange = $null
if ($null -ne $searchWorldVersion) {
    foreach ($record in $records) {
        if ($record.Index -le $deployEndIndex) { continue }
        if ($record.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2A_WORLD_CHANGED .*world_version=(\d+)') {
            [long]$version = $Matches[1]
            if ($version -gt $searchWorldVersion) {
                $worldChange = [pscustomobject]@{ Record = $record; Version = $version }
                break
            }
        }
    }
}
if ($null -ne $worldChange) {
    Add-Check 'postActionWorldInvalidation' PASS (Format-Evidence $worldChange.Record)
} else {
    Add-Check 'postActionWorldInvalidation' UNVERIFIED '' 'No later WorldVersion increase was observed after the native card completed.'
}

$research = $null
if ($null -ne $worldChange) {
    foreach ($record in $records) {
        if ($record.Index -le $worldChange.Record.Index) { continue }
        if ($record.Text -match '\[CombatSolver/MultiplayerAdvisor\] SEARCH_DEBOUNCED_START .*world_version=(\d+)') {
            [long]$version = $Matches[1]
            if ($version -ge $worldChange.Version) {
                $research = $record
                break
            }
        }
    }
}
if ($null -ne $research) {
    Add-Check 'postActionResearch' PASS (Format-Evidence $research)
} else {
    Add-Check 'postActionResearch' UNVERIFIED '' 'No fresh debounced search was observed after the post-action world invalidation.'
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
    phase = 'MP-2A'
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'This validates CombatSolver runtime evidence from an owned Lab client process.',
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
    Write-Output "MULTIPLAYER_MP-2A_$status logs=$($resolvedLogs.Count)"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
