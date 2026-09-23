#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [ValidateRange(3, 10)]
    [int]$MinLocalTurns = 3,

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
        throw "Safe Auto log path is not a file: $path"
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

function Get-IntToken {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )
    $pattern = '(?:^|\s)' + [regex]::Escape($Name) + '=(-?\d+)\b'
    if ($Text -match $pattern) { return [int]$Matches[1] }
    return $null
}

$enabled = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP_SAFE_AUTO enabled=true\b'
    })
if ($enabled.Count -eq 0) {
    Add-Check 'safeAutoEnabledOnce' UNVERIFIED '' 'No Safe Auto enable marker was observed.'
} else {
    $firstEnable = $enabled[0]
    $acceptedAfterEnable = @($records | Where-Object {
            $_.Index -gt $firstEnable.Index -and
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP2B_SAFE_END_TURN_ACCEPTED\b'
        })

    $selected = [Collections.Generic.List[object]]::new()
    $seenTurns = [Collections.Generic.HashSet[int]]::new()
    $seenRequests = [Collections.Generic.HashSet[int]]::new()
    foreach ($item in $acceptedAfterEnable) {
        $turn = Get-IntToken $item.Text 'turn'
        $request = Get-IntToken $item.Text 'request_id'
        if ($null -eq $turn -or $null -eq $request) { continue }
        if ($seenTurns.Add($turn) -and $seenRequests.Add($request)) {
            $selected.Add($item)
            if ($selected.Count -ge $MinLocalTurns) { break }
        }
    }

    $lastSelectedIndex = if ($selected.Count -gt 0) {
        [int]$selected[$selected.Count - 1].Index
    } else {
        [int]::MaxValue
    }
    $enableInWindow = @($enabled | Where-Object {
            $_.Index -le $lastSelectedIndex
        })
    if ($enableInWindow.Count -eq 1) {
        Add-Check 'safeAutoEnabledOnce' PASS (Format-Evidence $firstEnable)
    } else {
        Add-Check 'safeAutoEnabledOnce' FAIL (Join-Evidence $enableInWindow) 'Safe Auto must be enabled once for the measured carry window.'
    }

    if ($selected.Count -ge $MinLocalTurns) {
        Add-Check 'distinctAutoTurns' PASS (Join-Evidence @($selected)) "$MinLocalTurns distinct request/turn identities observed."
    } else {
        Add-Check 'distinctAutoTurns' UNVERIFIED (Join-Evidence @($acceptedAfterEnable)) "Need at least $MinLocalTurns distinct accepted local turns after Safe Auto enable."
    }

    $armedEvidence = [Collections.Generic.List[object]]::new()
    $boundaryEvidence = [Collections.Generic.List[object]]::new()
    $missing = [Collections.Generic.List[string]]::new()
    foreach ($end in $selected) {
        $turn = Get-IntToken $end.Text 'turn'
        $request = Get-IntToken $end.Text 'request_id'

        $armed = @($records | Where-Object {
                $_.Index -gt $firstEnable.Index -and
                $_.Index -lt $end.Index -and
                $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP_SAFE_AUTO_ARMED\b' -and
                $_.Text -match ('turn=' + [regex]::Escape([string]$turn) + '\b')
            } | Select-Object -Last 1)
        if ($armed.Count -eq 1) {
            $armedEvidence.Add($armed[0])
        } else {
            $missing.Add("turn=$turn missing MP_SAFE_AUTO_ARMED")
        }

        $revalidated = @($records | Where-Object {
                $_.Index -lt $end.Index -and
                $_.Text -match ('request_id=' + [regex]::Escape([string]$request) + '\b') -and
                $_.Text -match 'MP2B_END_TURN_REVALIDATED\b.*decision=Safe\b'
            } | Select-Object -Last 1)
        $nativeEnd = @($records | Where-Object {
                $_.Index -lt $end.Index -and
                $_.Text -match ('request_id=' + [regex]::Escape([string]$request) + '\b') -and
                $_.Text -match 'NATIVE_ACTION_CAPTURED\b.*type=EndPlayerTurnAction\b'
            } | Select-Object -Last 1)
        $freshBoundary = @($records | Where-Object {
                $_.Index -gt $end.Index -and
                $_.Text -match 'MP_REACTIVE_TURN_BOUNDARY\b.*fresh_probe=true.*fresh_capture=true'
            } | Select-Object -First 1)
        $freshSearch = @($records | Where-Object {
                $_.Index -gt $end.Index -and
                $_.Text -match 'MP_REACTIVE_FRESH_SEARCH\b.*after_safe_end_turn=true' -and
                $_.Text -match ('previous_end_turn_request_id=' + [regex]::Escape([string]$request) + '\b')
            } | Select-Object -First 1)
        $nextTurn = $turn + 1
        $searchReuse = @($records | Where-Object {
                $_.Index -gt $end.Index -and
                $_.Text -match '\[CombatSolver/Test\] SEARCH_REUSED\b' -and
                $_.Text -match ('turn=' + [regex]::Escape([string]$nextTurn) + '\b') -and
                $_.Text -match '\bold_authorization_dead=true\b' -and
                $_.Text -match '\bnew_authorization_pending=true\b'
            } | Select-Object -First 1)
        $continuationReuse = @($records | Where-Object {
                $_.Index -gt $end.Index -and
                $_.Text -match '\[CombatSolver/Test\] MP_LOCAL_XTURN_CONTINUATION_REUSED\b' -and
                $_.Text -match ('turn=' + [regex]::Escape([string]$nextTurn) + '\b') -and
                $_.Text -match '\blocal_state_exact=true\b' -and
                $_.Text -match '\breason=exact\b'
            } | Select-Object -First 1)

        if ($revalidated.Count -ne 1) { $missing.Add("request=$request missing safe end-turn revalidation") }
        if ($nativeEnd.Count -ne 1) { $missing.Add("request=$request missing native EndPlayerTurnAction") }
        if ($freshBoundary.Count -ne 1) { $missing.Add("request=$request missing fresh Probe/capture boundary") }

        $hasFresh = $freshSearch.Count -eq 1
        $hasExactReuse = $searchReuse.Count -eq 1 -and $continuationReuse.Count -eq 1
        if ($hasFresh -and $hasExactReuse) {
            $missing.Add("request=$request has both fresh search and exact continuation reuse")
        } elseif (-not $hasFresh -and -not $hasExactReuse) {
            $missing.Add("request=$request missing safe next-plan path")
        }

        if ($freshSearch.Count -eq 1) {
            $oldReuse = @($records | Where-Object {
                    $_.Index -gt $freshSearch[0].Index -and
                    $_.Text -match ('request_id=' + [regex]::Escape([string]$request) + '\b') -and
                    $_.Text -match 'NATIVE_ACTION_CAPTURED\b'
                } | Select-Object -First 1)
            if ($oldReuse.Count -gt 0) {
                $missing.Add("request=$request reused after fresh search")
            }
        }

        if ($freshBoundary.Count -eq 1) { $boundaryEvidence.Add($freshBoundary[0]) }
        if ($freshSearch.Count -eq 1) {
            $boundaryEvidence.Add($freshSearch[0])
        } elseif ($hasExactReuse) {
            $boundaryEvidence.Add($continuationReuse[0])
        }
    }

    if ($selected.Count -ge $MinLocalTurns -and $armedEvidence.Count -eq $MinLocalTurns) {
        Add-Check 'autoArmedEveryTurn' PASS (Join-Evidence @($armedEvidence))
    } elseif ($selected.Count -eq 0) {
        Add-Check 'autoArmedEveryTurn' UNVERIFIED '' 'No accepted Safe Auto turns were observed.'
    } else {
        Add-Check 'autoArmedEveryTurn' FAIL (Join-Evidence @($armedEvidence)) ($missing -join '; ')
    }

    $selectedAcceptedSafe = @($selected | Where-Object {
            $_.Text -match 'session_cleared=true\b' -and
            $_.Text -match 'authorization_cleared=true\b' -and
            $_.Text -match 'automatic_end_turn=true\b' -and
            $_.Text -match 'custom_network_api_used=false\b'
        })
    if ($selected.Count -ge $MinLocalTurns -and
        $selectedAcceptedSafe.Count -eq $MinLocalTurns -and
        $boundaryEvidence.Count -eq ($MinLocalTurns * 2) -and
        $missing.Count -eq 0) {
        Add-Check 'freshAuthorizationEachTurn' PASS (Join-Evidence @($boundaryEvidence))
    } elseif ($selected.Count -eq 0) {
        Add-Check 'freshAuthorizationEachTurn' UNVERIFIED '' 'No Safe Auto sequence is available for boundary validation.'
    } else {
        Add-Check 'freshAuthorizationEachTurn' FAIL (Join-Evidence @($boundaryEvidence)) ($missing -join '; ')
    }

    $windowEnd = if ($selected.Count -ge $MinLocalTurns) { $lastSelectedIndex } else { [int]::MaxValue }
    $manualDeploy = @($records | Where-Object {
            $_.Index -gt $firstEnable.Index -and $_.Index -lt $windowEnd -and
            $_.Text -match '\[CombatSolver/Test\] UI_ACTION action=deploy\b'
        })
    $safeAutoStop = @($records | Where-Object {
            $_.Index -gt $firstEnable.Index -and $_.Index -lt $windowEnd -and
            $_.Text -match '\[CombatSolver/MultiplayerSafeExecute\] MP_SAFE_AUTO_STOP\b'
        })
    if ($manualDeploy.Count -eq 0 -and $safeAutoStop.Count -eq 0) {
        Add-Check 'noManualRearmOrStop' PASS
    } else {
        Add-Check 'noManualRearmOrStop' FAIL (Join-Evidence @($manualDeploy + $safeAutoStop)) 'The measured three-turn window must not require another Execute click or hit a Safe Auto stop.'
    }

    $forbidden = @($records | Where-Object {
            $_.Index -gt $firstEnable.Index -and $_.Index -lt $windowEnd -and
            ($_.Text -match 'custom_network_api_used=true\b' -or
             $_.Text -match 'MP2B_DEPLOY_ABORT\b' -or
             $_.Text -match 'MP2B_REMOTE_DELTA_ABORT\b')
        })
    if ($forbidden.Count -eq 0) {
        Add-Check 'noUnsafeOrCustomPath' PASS
    } else {
        Add-Check 'noUnsafeOrCustomPath' FAIL (Join-Evidence $forbidden)
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
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    requiredLocalTurns = $MinLocalTurns
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'This validator proves journal ordering and Safe Auto authorization; it does not replace human confirmation of Host/Client identity and visible game behavior.',
        'Exact Joint continuation reuse is accepted only when SEARCH_REUSED creates a new authorization and MP_LOCAL_XTURN_CONTINUATION_REUSED reports local_state_exact=true reason=exact; otherwise Safe Auto must fresh-search.'
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
    Write-Output "MULTIPLAYER_SAFE_AUTO_$status logs=$($resolvedLogs.Count) required_turns=$MinLocalTurns"
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
