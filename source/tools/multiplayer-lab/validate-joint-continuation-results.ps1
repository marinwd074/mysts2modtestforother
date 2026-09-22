#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Reuse', 'Mismatch')]
    [string]$Mode,

    [string]$ExpectedRejectReason = 'remote_public_mismatch',

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
        throw "Joint continuation log path is not a file: $path"
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

function Get-LongToken {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )
    $value = Get-Token $Text $Name
    if ($null -eq $value -or $value -notmatch '^-?\d+$') { return $null }
    return [long]$value
}

$validations = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Test\] MP_LOCAL_XTURN_CONTINUATION_VALIDATE\b'
    })

if ($validations.Count -eq 0) {
    Add-Check 'validationObserved' UNVERIFIED '' 'No Joint continuation validation marker was observed.'
} else {
    $validation = $validations[0]
    Add-Check 'validationObserved' PASS (Format-Evidence $validation)

    $turn = Get-Token $validation.Text 'turn'
    $route = Get-Token $validation.Text 'route_identity'
    $sourceWorld = Get-LongToken $validation.Text 'source_world_version'
    $minimumWorld = Get-LongToken $validation.Text 'minimum_world_version'
    $actualWorld = Get-LongToken $validation.Text 'actual_world_version'

    if ($null -ne $sourceWorld -and $null -ne $minimumWorld -and $null -ne $actualWorld -and
        $actualWorld -gt [Math]::Max($sourceWorld, $minimumWorld)) {
        Add-Check 'worldVersionAdvanced' PASS (Format-Evidence $validation)
    } else {
        Add-Check 'worldVersionAdvanced' FAIL (Format-Evidence $validation) 'Continuation reuse requires actual_world_version to be strictly newer than both source and minimum world versions.'
    }

    $sameIdentity = {
        param($Record)
        $recordTurn = Get-Token $Record.Text 'turn'
        $recordRoute = Get-Token $Record.Text 'route_identity'
        return $recordTurn -eq $turn -and $recordRoute -eq $route
    }

    if ($Mode -eq 'Reuse') {
        $rejected = @($records | Where-Object {
                $_.Index -gt $validation.Index -and
                $_.Text -match '\[CombatSolver/Test\] MP_LOCAL_XTURN_CONTINUATION_REJECTED\b' -and
                (& $sameIdentity $_)
            } | Select-Object -First 1)
        $reused = @($records | Where-Object {
                $_.Index -gt $validation.Index -and
                $_.Text -match '\[CombatSolver/Test\] MP_LOCAL_XTURN_CONTINUATION_REUSED\b' -and
                (& $sameIdentity $_)
            } | Select-Object -First 1)
        $searchReused = @($records | Where-Object {
                $_.Index -gt $validation.Index -and
                $_.Text -match '\[CombatSolver/Test\] SEARCH_REUSED\b' -and
                (Get-Token $_.Text 'turn') -eq $turn
            } | Select-Object -First 1)

        if ($reused.Count -eq 1 -and
            $reused[0].Text -match '\blocal_state_exact=true\b' -and
            $reused[0].Text -match '\breason=exact\b' -and
            $rejected.Count -eq 0) {
            Add-Check 'exactContinuationReused' PASS (Format-Evidence $reused[0])
        } elseif ($reused.Count -eq 0) {
            Add-Check 'exactContinuationReused' UNVERIFIED '' 'No exact Joint continuation reuse marker was observed.'
        } else {
            Add-Check 'exactContinuationReused' FAIL (Join-Evidence @($reused + $rejected)) 'Reuse smoke requires exact state reuse without a rejection for the same route/turn.'
        }

        if ($searchReused.Count -eq 1) {
            Add-Check 'searchReuseCommitted' PASS (Format-Evidence $searchReused[0])
        } elseif ($reused.Count -eq 0) {
            Add-Check 'searchReuseCommitted' UNVERIFIED '' 'No reusable continuation was observed.'
        } else {
            Add-Check 'searchReuseCommitted' FAIL '' 'Continuation was marked reused but SEARCH_REUSED was not emitted.'
        }
    } else {
        $rejected = @($records | Where-Object {
                $_.Index -gt $validation.Index -and
                $_.Text -match '\[CombatSolver/Test\] MP_LOCAL_XTURN_CONTINUATION_REJECTED\b' -and
                (& $sameIdentity $_)
            } | Select-Object -First 1)

        if ($rejected.Count -eq 1 -and
            (Get-Token $rejected[0].Text 'reason') -eq $ExpectedRejectReason) {
            Add-Check 'mismatchRejected' PASS (Format-Evidence $rejected[0])
        } elseif ($rejected.Count -eq 0) {
            Add-Check 'mismatchRejected' UNVERIFIED '' 'No Joint continuation rejection marker was observed.'
        } else {
            Add-Check 'mismatchRejected' FAIL (Format-Evidence $rejected[0]) "Expected reject reason $ExpectedRejectReason."
        }

        $miss = @($records | Where-Object {
                $_.Index -gt $validation.Index -and
                $_.Text -match '\[CombatSolver/Test\] SEARCH_REUSE_MISS\b' -and
                (Get-Token $_.Text 'turn') -eq $turn
            } | Select-Object -First 1)
        if ($miss.Count -eq 1 -and
            (Get-Token $miss[0].Text 'continuation_reject_reason') -eq $ExpectedRejectReason) {
            Add-Check 'reuseMissRecorded' PASS (Format-Evidence $miss[0])
        } elseif ($rejected.Count -eq 0) {
            Add-Check 'reuseMissRecorded' UNVERIFIED '' 'No rejected continuation is available for reuse-miss validation.'
        } else {
            Add-Check 'reuseMissRecorded' FAIL (Join-Evidence $miss) 'Rejected continuation must emit SEARCH_REUSE_MISS with the same reason.'
        }

        $fresh = @($records | Where-Object {
                $_.Index -gt $validation.Index -and
                (
                    $_.Text -match '\[CombatSolver/MultiplayerAdvisor\] MP_ADVISOR_SEARCH_START\b' -or
                    $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP_REACTIVE_FRESH_SEARCH\b'
                ) -and
                (Get-Token $_.Text 'turn') -eq $turn
            } | Select-Object -First 1)
        $unexpectedReuse = @($records | Where-Object {
                $_.Index -gt $validation.Index -and
                $_.Text -match '\[CombatSolver/Test\] MP_LOCAL_XTURN_CONTINUATION_REUSED\b' -and
                (& $sameIdentity $_)
            } | Select-Object -First 1)

        if ($fresh.Count -eq 1 -and $unexpectedReuse.Count -eq 0) {
            Add-Check 'freshSearchStarted' PASS (Format-Evidence $fresh[0])
        } elseif ($rejected.Count -eq 0) {
            Add-Check 'freshSearchStarted' UNVERIFIED '' 'No rejected continuation is available for fresh-search validation.'
        } else {
            Add-Check 'freshSearchStarted' FAIL (Join-Evidence @($fresh + $unexpectedReuse)) 'Mismatch smoke must fresh-search and must not reuse the rejected worldline.'
        }
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
    mode = $Mode
    status = $status
    expectedRejectReason = if ($Mode -eq 'Mismatch') { $ExpectedRejectReason } else { $null }
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'This validator proves CombatSolver journal ordering and continuation identity; it does not replace visual Host/Client confirmation.',
        'Exact continuation correctness also depends on the runtime live/predicted stamp implementation, which includes local combat state and combat RNG streams.'
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
    Write-Output "MULTIPLAYER_JOINT_CONTINUATION_$($Mode.ToUpperInvariant())_$status logs=$($resolvedLogs.Count)"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
