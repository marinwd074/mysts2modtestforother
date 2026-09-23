#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [ValidateSet('Matrix', 'Timeout', 'All')]
    [string]$Phase = 'All',

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
        throw "U3 scenario log path is not a file: $path"
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

function Get-IntToken {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )
    $value = Get-Token $Text $Name
    if ($null -eq $value -or $value -notmatch '^-?\d+$') { return $null }
    return [int]$value
}

function Get-BoolToken {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )
    $value = Get-Token $Text $Name
    if ($value -eq 'true') { return $true }
    if ($value -eq 'false') { return $false }
    return $null
}

function Parse-Statuses {
    param([Parameter(Mandatory)][string]$Text)
    $raw = Get-Token $Text 'statuses'
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }

    $map = [ordered]@{}
    foreach ($item in $raw.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
        $parts = $item.Split(':', 2)
        if ($parts.Count -ne 2 -or [string]::IsNullOrWhiteSpace($parts[0])) {
            return $null
        }
        if ($map.Contains($parts[0])) {
            return $null
        }
        $map[$parts[0]] = $parts[1]
    }
    return $map
}

$budgets = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_SCENARIO_BUDGET\b'
    })
$coverages = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_SCENARIO_COVERAGE\b'
    })
$reranks = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_SCENARIO_RERANK\b'
    })
$selections = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/U0\] FINAL_SELECTION\b'
    })

$requiredScenarioIds = @('aggressive', 'defensive', 'conserve', 'no_action')
$matrixSessions = [Collections.Generic.List[object]]::new()
$matrixFailures = [Collections.Generic.List[string]]::new()
$matrixEvidence = [Collections.Generic.List[object]]::new()

foreach ($budget in $budgets) {
    $nextBudget = @($budgets | Where-Object { $_.Index -gt $budget.Index } | Select-Object -First 1)
    $sessionEnd = if ($nextBudget.Count -eq 1) { [int]$nextBudget[0].Index } else { [int]::MaxValue }

    $sessionReranks = @($reranks | Where-Object {
            $_.Index -gt $budget.Index -and $_.Index -lt $sessionEnd
        })
    $rerank = @($sessionReranks | Select-Object -First 1)
    if ($rerank.Count -eq 1) {
        $sessionEnd = [int]$rerank[0].Index
    }

    $sessionCoverages = @($coverages | Where-Object {
            $_.Index -gt $budget.Index -and $_.Index -lt $sessionEnd
        })
    $selection = @($selections | Where-Object {
            $_.Index -gt $(if ($rerank.Count -eq 1) { $rerank[0].Index } else { $budget.Index })
        } | Select-Object -First 1)

    $total = Get-IntToken $budget.Text 'total_node_budget'
    $main = Get-IntToken $budget.Text 'main_node_budget'
    $reserved = Get-IntToken $budget.Text 'reserved'
    $decisions = Get-IntToken $budget.Text 'decisions'
    $scenariosPerDecision = Get-IntToken $budget.Text 'scenarios_per_decision'
    $decisionBudget = Get-IntToken $budget.Text 'decision_budget'
    $replayExpanded = Get-IntToken $budget.Text 'replay_expanded'
    $replayTransitions = Get-IntToken $budget.Text 'replay_transitions'

    $sessionProblems = [Collections.Generic.List[string]]::new()
    if ($null -in @($total, $main, $reserved, $decisions, $scenariosPerDecision, $decisionBudget, $replayExpanded, $replayTransitions)) {
        $sessionProblems.Add('MP_SCENARIO_BUDGET is missing required numeric tokens')
    } else {
        if ($main + $reserved -ne $total) {
            $sessionProblems.Add("main_node_budget + reserved != total_node_budget ($main + $reserved != $total)")
        }
        if ($reserved -le 0) {
            $sessionProblems.Add("reserved must be positive for a real U3 matrix, got $reserved")
        }
        if ($decisions -lt 2 -or $decisions -gt 4) {
            $sessionProblems.Add("decisions must be 2..4, got $decisions")
        }
        if ($scenariosPerDecision -ne 4) {
            $sessionProblems.Add("scenarios_per_decision must be 4, got $scenariosPerDecision")
        }
        if ($decisionBudget -lt 1 -or $decisionBudget * $decisions -gt $reserved) {
            $sessionProblems.Add("per-decision budget exceeds reserve: $decisionBudget * $decisions > $reserved")
        }
        if ($replayExpanded -lt 0 -or $replayExpanded -gt $reserved) {
            $sessionProblems.Add("replay_expanded must be within reserve, got $replayExpanded/$reserved")
        }
        if ($replayTransitions -lt $replayExpanded) {
            $sessionProblems.Add("replay_transitions must include at least expanded work, got $replayTransitions < $replayExpanded")
        }
    }

    if ($null -ne $decisions -and $sessionCoverages.Count -ne $decisions) {
        $sessionProblems.Add("coverage row count $($sessionCoverages.Count) does not match decisions=$decisions")
    }

    $coverageExpanded = 0
    $anyUnknown = $false
    $allComplete = $sessionCoverages.Count -gt 0
    foreach ($coverage in $sessionCoverages) {
        $complete = Get-BoolToken $coverage.Text 'complete'
        $scenarioCount = Get-IntToken $coverage.Text 'scenario_count'
        $expanded = Get-IntToken $coverage.Text 'replay_expanded'
        $statuses = Parse-Statuses $coverage.Text
        if ($null -eq $complete -or $null -eq $scenarioCount -or $null -eq $expanded -or $null -eq $statuses) {
            $sessionProblems.Add("malformed MP_SCENARIO_COVERAGE at $($coverage.Path):$($coverage.LineNumber)")
            continue
        }
        $coverageExpanded += $expanded

        foreach ($scenarioId in $requiredScenarioIds) {
            if (-not $statuses.Contains($scenarioId)) {
                $sessionProblems.Add("coverage missing required scenario '$scenarioId' at line $($coverage.LineNumber)")
                continue
            }
            $statusValue = [string]$statuses[$scenarioId]
            if ($statusValue -notin @('Completed', 'Terminal', 'Unknown')) {
                $sessionProblems.Add("invalid status '$statusValue' for '$scenarioId' at line $($coverage.LineNumber)")
            }
            if ($statusValue -eq 'Unknown') {
                $anyUnknown = $true
                $allComplete = $false
            }
        }
        if ($statuses.Count -ne 4) {
            $sessionProblems.Add("coverage must contain exactly four ScenarioSpec statuses at line $($coverage.LineNumber)")
        }

        $unknownCount = @($statuses.Values | Where-Object { $_ -eq 'Unknown' }).Count
        if ($complete) {
            if ($scenarioCount -ne 4 -or $unknownCount -gt 0) {
                $sessionProblems.Add("complete=true requires four non-Unknown scenarios at line $($coverage.LineNumber)")
            }
        } else {
            $allComplete = $false
            if ($unknownCount -eq 0) {
                $sessionProblems.Add("complete=false must expose at least one Unknown scenario at line $($coverage.LineNumber)")
            }
        }
    }

    if ($null -ne $replayExpanded -and $coverageExpanded -ne $replayExpanded) {
        $sessionProblems.Add("coverage replay_expanded sum $coverageExpanded does not match budget replay_expanded=$replayExpanded")
    }

    if ($rerank.Count -ne 1) {
        $sessionProblems.Add('matrix session is missing MP_SCENARIO_RERANK')
    } else {
        $enabled = Get-BoolToken $rerank[0].Text 'enabled'
        if ($null -eq $enabled) {
            $sessionProblems.Add('MP_SCENARIO_RERANK is missing enabled=true/false')
        } elseif ($allComplete -and -not $anyUnknown) {
            if (-not $enabled) {
                $sessionProblems.Add('complete shared coverage must enable robust rerank')
            }
            if ((Get-BoolToken $rerank[0].Text 'complete') -ne $true -or
                (Get-IntToken $rerank[0].Text 'scenario_count') -ne 4) {
                $sessionProblems.Add('enabled rerank must report complete=true scenario_count=4')
            }
        } else {
            $reason = Get-Token $rerank[0].Text 'reason'
            if ($enabled -or $reason -ne 'shared_scenario_coverage_incomplete_or_single_current_decision') {
                $sessionProblems.Add('Unknown/incomplete matrix must fail closed to shared baseline fallback')
            }
        }
    }

    if ($selection.Count -ne 1) {
        $sessionProblems.Add('matrix session is missing FINAL_SELECTION')
    } else {
        $selectionRerank = Get-BoolToken $selection[0].Text 'scenario_rerank'
        $expectedSelectionRerank = $allComplete -and -not $anyUnknown
        if ($null -eq $selectionRerank -or $selectionRerank -ne $expectedSelectionRerank) {
            $sessionProblems.Add("FINAL_SELECTION scenario_rerank does not match matrix result; expected=$expectedSelectionRerank")
        }
    }

    $matrixSessions.Add([pscustomobject]@{
            Budget = $budget
            Coverages = $sessionCoverages
            Rerank = if ($rerank.Count -eq 1) { $rerank[0] } else { $null }
            Selection = if ($selection.Count -eq 1) { $selection[0] } else { $null }
            Problems = @($sessionProblems)
            AnyUnknown = $anyUnknown
            Complete = $allComplete -and -not $anyUnknown
        })
    $matrixEvidence.Add($budget)
    foreach ($item in $sessionCoverages) { $matrixEvidence.Add($item) }
    if ($rerank.Count -eq 1) { $matrixEvidence.Add($rerank[0]) }
    if ($selection.Count -eq 1) { $matrixEvidence.Add($selection[0]) }
    foreach ($problem in $sessionProblems) { $matrixFailures.Add($problem) }
}

if ($Phase -in @('Matrix', 'All')) {
    if ($matrixFailures.Count -gt 0) {
        Add-Check 'fairScenarioMatrix' FAIL (Join-Evidence @($matrixEvidence | Select-Object -First 20)) ($matrixFailures -join '; ')
    } elseif ($matrixSessions.Count -gt 0) {
        Add-Check 'fairScenarioMatrix' PASS (Join-Evidence @($matrixEvidence | Select-Object -First 20)) 'Observed production U3 budget accounting, four fixed ScenarioSpec lanes per current decision, and correct robust-rerank/fallback behavior.'
    } else {
        Add-Check 'fairScenarioMatrix' UNVERIFIED '' 'No MP_SCENARIO_BUDGET marker was observed. Use a real multiplayer root with at least two distinct current decisions.'
    }
}

$timeoutMarkers = @($reranks | Where-Object {
        $_.Text -match '\benabled=false\b' -and
        $_.Text -match '\breason=reevaluation_budget_unavailable\b'
    })
$timeoutProblems = [Collections.Generic.List[string]]::new()
$timeoutEvidence = [Collections.Generic.List[object]]::new()
foreach ($marker in $timeoutMarkers) {
    $previousSelection = @($selections | Where-Object { $_.Index -lt $marker.Index } | Select-Object -Last 1)
    $windowStart = if ($previousSelection.Count -eq 1) { [int]$previousSelection[0].Index } else { -1 }
    $unexpectedBudgets = @($budgets | Where-Object {
            $_.Index -gt $windowStart -and $_.Index -lt $marker.Index
        })
    if ($unexpectedBudgets.Count -gt 0) {
        $timeoutProblems.Add('reevaluation_budget_unavailable session still emitted MP_SCENARIO_BUDGET before rerank')
    }

    $selection = @($selections | Where-Object { $_.Index -gt $marker.Index } | Select-Object -First 1)
    if ($selection.Count -ne 1) {
        $timeoutProblems.Add('timeout fail-closed session is missing FINAL_SELECTION')
    } elseif ((Get-BoolToken $selection[0].Text 'scenario_rerank') -ne $false) {
        $timeoutProblems.Add('timeout fail-closed FINAL_SELECTION must report scenario_rerank=false')
    }

    $timeoutEvidence.Add($marker)
    foreach ($item in $unexpectedBudgets) { $timeoutEvidence.Add($item) }
    if ($selection.Count -eq 1) { $timeoutEvidence.Add($selection[0]) }
}

if ($Phase -in @('Timeout', 'All')) {
    if ($timeoutProblems.Count -gt 0) {
        Add-Check 'timeoutFailClosed' FAIL (Join-Evidence @($timeoutEvidence | Select-Object -First 12)) ($timeoutProblems -join '; ')
    } elseif ($timeoutMarkers.Count -gt 0) {
        Add-Check 'timeoutFailClosed' PASS (Join-Evidence @($timeoutEvidence | Select-Object -First 12)) 'Main-search timeout disabled U3 reevaluation and final selection remained on the baseline ordering.'
    } else {
        Add-Check 'timeoutFailClosed' UNVERIFIED '' 'No reevaluation_budget_unavailable marker was observed. A normal non-timeout U3 run does not prove this path.'
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
    phase = $Phase
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($resolvedLogs)
    budgetCount = $budgets.Count
    coverageCount = $coverages.Count
    rerankCount = $reranks.Count
    checks = @($checks)
    limitations = @(
        'Matrix PASS requires production MP_SCENARIO_BUDGET and therefore cannot be satisfied by the single-player P0/P1 pinned fixture.',
        'This validator checks fair scenario coverage, budget accounting, Unknown fallback, and timeout fail-closed behavior. Non-clairvoyant CurrentTurnDecisionKey identity is covered by production contract tests; the current runtime log does not expose the raw decision key.',
        'A validator PASS does not replace confirmation that the supplied log came from the intended Host/Client battle.'
    )
}

$jsonText = $result | ConvertTo-Json -Depth 9
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
    Write-Output ("MULTIPLAYER_U3_SCENARIO_{0}_{1} logs={2} budgets={3} coverage={4} reranks={5}" -f
        $Phase, $status, $resolvedLogs.Count, $budgets.Count, $coverages.Count, $reranks.Count)
    foreach ($check in $checks) {
        $suffix = if ($null -eq $check.detail) { '' } else { " detail=$($check.detail)" }
        Write-Output ("{0} {1}{2}" -f $check.status, $check.name, $suffix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
