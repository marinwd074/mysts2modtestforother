#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Reuse', 'Mismatch', 'RouteReplay', 'TargetDeath')]
    [string]$Mode,

    [string]$ExpectedRejectReason = 'remote_public_mismatch',

    [ValidateRange(1, 20)]
    [int]$MinReplays = 1,

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
        $text = [string]$line
        if ($text.TrimStart().StartsWith('{')) {
            $entry = $text | ConvertFrom-Json -ErrorAction Stop
            if ($entry.PSObject.Properties['Message'] -and $entry.Message -is [string]) {
                $text = $entry.Message
            }
        }
        $records.Add([pscustomobject]@{
                Index = $globalIndex++
                Path = $path
                LineNumber = $lineNumber
                Text = $text
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

if ($Mode -in @('RouteReplay','TargetDeath')) {
    $replays = @($records | Where-Object {
        if ($Mode -eq 'RouteReplay') {
            $_.Text -match '\[CombatSolver/Test\] MP_LOCAL_XTURN_ROUTE_REPLAY\b' -and
            (Get-Token $_.Text 'status') -eq 'accepted'
        } else {
            $_.Text -match '\] R1_REROOT_RECOVERY\b' -and
            (Get-Token $_.Text 'status') -in @('partial_hint','bound_established','replayed_only')
        }
    })
    Add-Check $(if ($Mode -eq 'TargetDeath') {'recoveryObserved'} else {'acceptedReplayCount'}) $(if ($replays.Count -ge $MinReplays) {'PASS'} else {'UNVERIFIED'}) '' "Observed $($replays.Count); required $MinReplays. Missing boundaries remain unverified."
    foreach ($replay in $replays) {
        $localRecords = @($records | Where-Object Path -eq $replay.Path)
        $request = @($localRecords | Where-Object {
            $_.Index -lt $replay.Index -and $_.Text -match '\] SEARCH_REQUEST generation='
        } | Select-Object -Last 1)
        if ($request.Count -ne 1) {
            Add-Check 'replayRequestIdentity' FAIL (Format-Evidence $replay) 'Accepted replay has no preceding request in the same log.'
            continue
        }
        $generation = Get-Token $request[0].Text 'generation'
        $turn = Get-Token $request[0].Text 'turn'
        $next = @($localRecords | Where-Object {
            $_.Index -gt $request[0].Index -and $_.Text -match '\] SEARCH_REQUEST generation='
        } | Select-Object -First 1)
        $endIndex = if ($next.Count) { $next[0].Index } else { [int]::MaxValue }
        $window = @($localRecords | Where-Object { $_.Index -gt $request[0].Index -and $_.Index -lt $endIndex })
        $validation = @($validations | Where-Object {
            $_.Path -eq $replay.Path -and $_.Index -lt $request[0].Index -and
            (Get-Token $_.Text 'turn') -eq $turn
        } | Select-Object -Last 1)
        $fresh = @($localRecords | Where-Object {
            $_.Index -lt $request[0].Index -and $_.Text -match '\] MP_REACTIVE_FRESH_SEARCH\b' -and
            (Get-Token $_.Text 'generation') -eq $generation -and (Get-Token $_.Text 'turn') -eq $turn
        } | Select-Object -Last 1)
        # Search generation persists across combats; route generation resets per combat.
        $routeGeneration = if ($fresh.Count) { Get-LongToken $fresh[0].Text 'route_generation' } else { $null }
        $rootValid = $validation.Count -eq 1 -and $fresh.Count -eq 1
        if ($rootValid) {
            $oldRoute = Get-Token $validation[0].Text 'route_identity'
            $rejected = @($localRecords | Where-Object {
                $_.Index -gt $validation[0].Index -and $_.Index -lt $request[0].Index -and
                $_.Text -match '\] MP_LOCAL_XTURN_CONTINUATION_REJECTED\b' -and
                (Get-Token $_.Text 'turn') -eq $turn -and (Get-Token $_.Text 'route_identity') -eq $oldRoute -and
                (Get-Token $_.Text 'reason') -eq 'local_state_mismatch'
            })
            $actualWorld = Get-LongToken $validation[0].Text 'actual_world_version'
            $sourceWorld = Get-LongToken $validation[0].Text 'source_world_version'
            $minimumWorld = Get-LongToken $validation[0].Text 'minimum_world_version'
            $rootValid = $rejected.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($oldRoute) -and
                $null -ne $routeGeneration -and $routeGeneration -gt 0 -and
                $null -ne $actualWorld -and $null -ne $sourceWorld -and $null -ne $minimumWorld -and
                $actualWorld -gt [Math]::Max($sourceWorld, $minimumWorld) -and
                (Get-LongToken $fresh[0].Text 'world_version') -eq $actualWorld -and
                (Get-Token $fresh[0].Text 'fresh_probe') -eq 'true' -and
                (Get-Token $fresh[0].Text 'fresh_capture') -eq 'true' -and
                (Get-Token $fresh[0].Text 'after_safe_end_turn') -eq 'true' -and
                $null -ne (Get-LongToken $fresh[0].Text 'previous_end_turn_request_id') -and
                (Get-Token $fresh[0].Text 'cross_turn_reuse') -eq 'false'
        }
        if ($Mode -eq 'TargetDeath') {
            $miss = @($localRecords | Where-Object {
                $validation.Count -eq 1 -and $_.Index -gt $validation[0].Index -and $_.Index -lt $request[0].Index -and
                $_.Text -match '\] SEARCH_REUSE_MISS\b' -and (Get-Token $_.Text 'turn') -eq $turn
            } | Select-Object -Last 1)
            $survivors = @(); $rosterValid = $false
            if ($miss.Count -eq 1 -and $miss[0].Text -match 'field=enemies\s+expected=\{([^}]*)\}\s+actual=\{([^}]*)\}') {
                $expectedRoster = @($Matches[1].Split(',', [StringSplitOptions]::RemoveEmptyEntries))
                $actualRoster = @($Matches[2].Split(',', [StringSplitOptions]::RemoveEmptyEntries))
                $rosterValid = $actualRoster.Count -gt 0 -and $actualRoster.Count -lt $expectedRoster.Count -and
                    @($actualRoster | Where-Object { $_ -notin $expectedRoster }).Count -eq 0 -and
                    @($actualRoster | Sort-Object -Unique).Count -eq $actualRoster.Count
                if ($rosterValid) { $survivors = @($actualRoster | ForEach-Object { $_.Split(':')[0] }) }
            }
            $seedCapture = @($localRecords | Where-Object {
                $validation.Count -eq 1 -and $_.Index -gt $validation[0].Index -and $_.Index -lt $request[0].Index -and
                $_.Text -match '\] MP_LOCAL_XTURN_SEED_CAPTURE\b' -and (Get-Token $_.Text 'turn') -eq $turn
            } | Select-Object -Last 1)
            $rootValid = $rootValid -and $rosterValid -and $seedCapture.Count -eq 1 -and
                (Get-Token $seedCapture[0].Text 'admission_reason') -eq 'none'
            Add-Check "targetDeathFreshRoot:$generation" $(if ($rootValid) {'PASS'} else {'FAIL'}) (Join-Evidence @($validation + $miss + $seedCapture + $fresh)) 'Requires a nonempty strict roster subset and admitted seed from the newer captured root.'
        } else {
            $rootValid = $rootValid -and (Get-Token $validation[0].Text 'local_core_reject_reason') -eq 'non_shuffle_rng_changed:targets' -and
                (Get-Token $validation[0].Text 'allow_living_enemy_hp_decrease') -eq 'true'
            Add-Check "targetsFreshRoot:$generation" $(if ($rootValid) {'PASS'} else {'FAIL'}) (Join-Evidence @($validation + $fresh)) 'Requires Targets-only admission and a newer captured root; strict continuation remains rejected.'
        }

        $ledger = @($window | Where-Object { $_.Index -ge $replay.Index -and $_.Text -match '\] SEARCH_REQUEST_PHASE ' })
        if ($Mode -eq 'TargetDeath') {
            $seed = @($window | Where-Object { $_.Index -lt $replay.Index -and $_.Text -match '\] SEARCH_CONTINUATION_SEED\b' } | Select-Object -Last 1)
            $workValid = $rootValid -and $ledger.Count -eq 1 -and $seed.Count -eq 1 -and
                (Get-Token $seed[0].Text 'status') -eq 'partial' -and
                (Get-Token $seed[0].Text 'reason') -eq 'action_unavailable' -and
                (Get-LongToken $seed[0].Text 'replayed') -gt 0 -and
                (Get-LongToken $seed[0].Text 'replayed') -lt (Get-LongToken $seed[0].Text 'requested') -and
                (Get-LongToken $seed[0].Text 'requested') -eq (Get-LongToken $seedCapture[0].Text 'captured_actions') -and
                (Get-Token $replay.Text 'primary_budget_unchanged') -eq 'true' -and
                (Get-Token $replay.Text 'candidate_set_unchanged') -eq 'true'
            if ($workValid) {
                $metrics = $ledger[0].Text.Substring($ledger[0].Text.IndexOf('{')) | ConvertFrom-Json -ErrorAction Stop
                $seedMembers = @($metrics.Members | Where-Object { $_.Kind -eq 'ContinuationSeed' -and $_.Outcome -eq 'Completed' })
                $workValid = $seedMembers.Count -eq 1 -and $metrics.RecordedSolverCount -eq $metrics.Members.Count -and
                    $metrics.ExpandedNodes -eq ($metrics.Members.Work.ExpandedNodes | Measure-Object -Sum).Sum -and
                    $metrics.LogicalTransitions -eq ($metrics.Members.Work.TransitionCount | Measure-Object -Sum).Sum
            }
            Add-Check "partialSeedRecovered:$generation" $(if ($workValid) {'PASS'} else {'FAIL'}) (Join-Evidence @(@($replay) + $seed + $ledger)) 'Checks safe seed truncation and recovery, without claiming a skipped primary search or unchanged old score.'
        } else {
            $workValid = $ledger.Count -eq 1 -and (Get-Token $replay.Text 'fresh_root') -eq 'true' -and
                (Get-LongToken $replay.Text 'expanded') -eq 0 -and
                (Get-Token $replay.Text 'full_search_skipped') -eq 'true' -and
                (Get-Token $replay.Text 'quality') -in @('equivalent_partial','acceptable_victory','explicit_adoption_victory')
            if ($workValid) {
                $metrics = $ledger[0].Text.Substring($ledger[0].Text.IndexOf('{')) | ConvertFrom-Json -ErrorAction Stop
                $workValid = $metrics.RecordedSolverCount -eq 1 -and $metrics.Members.Count -eq 1 -and
                    $metrics.Members[0].Kind -eq 'RouteReplay' -and $metrics.Members[0].Outcome -eq 'Completed' -and
                    $metrics.ExpandedNodes -eq 0 -and $metrics.Members[0].Work.ExpandedNodes -eq 0 -and
                    $metrics.LogicalTransitions -eq (Get-LongToken $replay.Text 'actions') -and
                    $metrics.Members[0].Work.TransitionCount -eq $metrics.LogicalTransitions -and $metrics.LogicalTransitions -gt 0
            }
            Add-Check "fullSearchSkipped:$generation" $(if ($workValid) {'PASS'} else {'FAIL'}) (Join-Evidence @(@($replay) + $ledger)) 'The completed request ledger must contain only the zero-expansion RouteReplay member.'
        }
        $forbidden = @($window | Where-Object {
            $_.Text -match '\b(SEARCH_FAILURE|MP2B_DEPLOY_ABORTED|FAIL_CLOSED)\b|custom_network_api_used=true\b' -or
            ($_.Text -match 'MP_LOCAL_XTURN_CONTINUATION_REUSED\b' -and (Get-Token $_.Text 'turn') -eq $turn)
        })
        Add-Check "noStaleDeployment:$generation" $(if ($forbidden.Count -eq 0) {'PASS'} else {'FAIL'}) (Join-Evidence $forbidden)

        $capture = @($window | Where-Object {
            $_.Index -gt $replay.Index -and $_.Text -match '\] SEARCH_RESULT_ROUTE_CAPTURE\b' -and
            (Get-Token $_.Text 'generation') -eq $generation
        } | Select-Object -First 1)
        $deploy = @($window | Where-Object {
            $_.Index -gt $replay.Index -and $_.Text -match '\] MP2B_DEPLOY_START\b' -and
            (Get-Token $_.Text 'turn') -eq $turn -and (Get-LongToken $_.Text 'route_generation') -eq $routeGeneration
        } | Select-Object -First 1)
        if ($capture.Count -ne 1 -or $deploy.Count -ne 1) {
            Add-Check "replayDeployment:$generation" UNVERIFIED (Join-Evidence @($capture + $deploy)) 'Accepted replay still needs its captured result and actual deployment.'
            continue
        }
        $requestId = Get-LongToken $deploy[0].Text 'request_id'
        $route = Get-Token $capture[0].Text 'route_identity'
        $authorized = $rootValid -and $capture[0].Index -lt $deploy[0].Index -and
            (Get-Token $deploy[0].Text 'new_authorization') -eq 'true' -and $null -ne $requestId -and
            -not [string]::IsNullOrWhiteSpace($route) -and
            $requestId -gt (Get-LongToken $fresh[0].Text 'previous_end_turn_request_id') -and
            (Get-Token $deploy[0].Text 'route_identity') -eq $route -and
            $route -ne (Get-Token $validation[0].Text 'route_identity') -and
            (Get-LongToken $deploy[0].Text 'search_world_version') -ge $actualWorld
        if ((Get-Token $replay.Text 'quality') -eq 'equivalent_partial') {
            $authorized = $authorized -and (Get-Token $capture[0].Text 'deployment_scope') -eq 'SearchCompletion' -and
                (Get-Token $capture[0].Text 'route_scope') -eq 'PartialLocalCrossTurnProjection'
        }
        Add-Check "renewedAuthorization:$generation" $(if ($authorized) {'PASS'} else {'FAIL'}) (Join-Evidence @($capture + $deploy)) 'Deployment must use the new route identity and preserve the partial evaluation scope.'
        $deployment = @($window | Where-Object {
            $_.Index -gt $deploy[0].Index -and (Get-LongToken $_.Text 'request_id') -eq $requestId
        })
        $reconciled = @($deployment | Where-Object { $_.Text -match '\] MP2B_ACTION_RECONCILED\b' })
        $end = @($deployment | Where-Object { $_.Text -match '\] MP2B_SAFE_END_TURN_ACCEPTED\b' })
        $nativeEnd = @($deployment | Where-Object { $_.Text -match 'NATIVE_ACTION_CAPTURED\b.*type=EndPlayerTurnAction\b' })
        $safe = @($deployment | Where-Object {
            $_.Text -match '\] MP2B_END_TURN_REVALIDATED\b' -and (Get-Token $_.Text 'decision') -eq 'Safe'
        })
        $actionCount = Get-LongToken $deploy[0].Text 'action_count'
        if ($Mode -eq 'TargetDeath') {
            $targetActions = @($window | Where-Object {
                $_.Index -gt $deploy[0].Index -and $_.Text -match '\] DEPLOY_ACTION\b' -and
                (Get-Token $_.Text 'turn') -eq $turn -and ($end.Count -ne 1 -or $_.Index -lt $end[0].Index)
            })
            $targetsValid = $rootValid -and $targetActions.Count -eq $actionCount -and
                $reconciled.Count -eq $targetActions.Count
            for ($i = 0; $targetsValid -and $i -lt $targetActions.Count; $i++) {
                $target = Get-Token $targetActions[$i].Text 'target_combat_id'
                $targetsValid = ($target -eq '-' -or $target -in $survivors) -and
                    (Get-Token $targetActions[$i].Text 'card') -eq (Get-Token $reconciled[$i].Text 'card')
            }
            Add-Check "survivingTargetsOnly:$generation" $(if ($targetsValid) {'PASS'} else {'FAIL'}) (Join-Evidence $targetActions) 'Every deployed targeted action must use a surviving combat ID; untargeted cards use the native no-target marker.'
        }
        $complete = $null -ne $actionCount -and $actionCount -gt 0 -and $reconciled.Count -eq $actionCount -and
            $end.Count -eq 1 -and $nativeEnd.Count -eq 1 -and $safe.Count -eq 1
        if ($complete) {
            for ($i = 0; $i -lt $reconciled.Count; $i++) {
                $complete = $complete -and (Get-LongToken $reconciled[$i].Text 'action_index') -eq $i -and
                    (Get-Token $reconciled[$i].Text 'native_action_captured') -eq 'true' -and
                    (Get-Token $reconciled[$i].Text 'action_queue_idle') -eq 'true' -and
                    (Get-Token $reconciled[$i].Text 'decision') -in @('SafeToContinue','ExpectedLocalChange') -and
                    $reconciled[$i].Index -lt $safe[0].Index
            }
            $complete = $complete -and $safe[0].Index -lt $nativeEnd[0].Index -and $nativeEnd[0].Index -lt $end[0].Index -and
                (Get-Token $end[0].Text 'session_cleared') -eq 'true' -and
                (Get-Token $end[0].Text 'authorization_cleared') -eq 'true' -and
                (Get-Token $end[0].Text 'automatic_end_turn') -eq 'true' -and
                (Get-Token $end[0].Text 'custom_network_api_used') -eq 'false' -and
                (Get-LongToken $end[0].Text 'action_count') -eq $actionCount -and
                (Get-Token $end[0].Text 'turn') -eq $turn -and
                (Get-Token $end[0].Text 'route_identity') -eq $route
        }
        Add-Check "nativeDeploymentCompleted:$generation" $(if ($complete) {'PASS'} else {'FAIL'}) (Join-Evidence @($reconciled + $safe + $nativeEnd + $end)) 'Requires each native local action, Safe EndTurn, and cleared authorization for this request.'
    }
} elseif ($validations.Count -eq 0) {
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
    minimumReplays = if ($Mode -in @('RouteReplay','TargetDeath')) { $MinReplays } else { $null }
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($resolvedLogs)
    checks = @($checks)
    limitations = @(
        'This validator proves CombatSolver journal ordering and continuation identity; it does not replace visual Host/Client confirmation.',
        'Exact continuation correctness also depends on the runtime live/predicted stamp implementation, which includes local combat state and combat RNG streams.',
        'RouteReplay checks Targets-only acceptance, request work, evaluation scope and deployment; logged quality labels do not independently prove quality formulas or general speedup.',
        'TargetDeath covers the observed partial seed recovery and surviving-target deployment; it does not prove all death cases, complete victory or skipped primary search.'
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
