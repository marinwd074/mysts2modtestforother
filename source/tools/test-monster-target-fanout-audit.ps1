#requires -Version 7.0

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskPath = Join-Path $sourceRoot 'docs/multiplayer/NEXT_LOCAL_01071_MONSTER_TARGET_AUDIT.md'
$auditPath = Join-Path $sourceRoot 'docs/compat/0.107.1/MONSTER_TARGET_FANOUT_AUDIT.md'

$taskLines = [IO.File]::ReadAllLines($taskPath)
$auditLines = [IO.File]::ReadAllLines($auditPath)

$expectedMoves = [System.Collections.Generic.List[string]]::new()
foreach ($line in $taskLines) {
    if ($line -match '^- `(?<move>[^`]+\.[^`]+)`$') {
        $expectedMoves.Add($Matches.move)
    }
}
if ($expectedMoves.Count -ne 64) {
    throw "Expected 64 pinned monster target audit moves, found $($expectedMoves.Count)."
}
if (($expectedMoves | Select-Object -Unique).Count -ne $expectedMoves.Count) {
    throw 'Pinned monster target audit task contains duplicate moves.'
}

$allowedNative = @(
    'PENDING_PINNED_IL',
    'AllTargets',
    'PerTargetLoop',
    'SingleSelected',
    'SelfOnly',
    'PerPlayerChoice',
    'Unknown'
)
$allowedActions = @(
    'FanOutSafe',
    'NeedsPerTargetRng',
    'NeedsRemoteChoiceFailClosed',
    'NeedsMoreModeling',
    'NoChange'
)

$rows = @{}
foreach ($line in $auditLines) {
    if ($line -notmatch '^\| `(?<move>[^`]+)` \| (?<native>[^|]+) \| (?<dead>[^|]+) \| (?<dependency>[^|]+) \| (?<action>[^|]+) \|$') {
        continue
    }

    $move = $Matches.move.Trim()
    if ($rows.ContainsKey($move)) {
        throw "Duplicate monster target audit row: $move"
    }

    $native = $Matches.native.Trim()
    $dead = $Matches.dead.Trim()
    $dependency = $Matches.dependency.Trim()
    $action = $Matches.action.Trim()

    if ($native -notin $allowedNative) {
        throw "Invalid native target class for ${move}: $native"
    }
    if ($action -notin $allowedActions) {
        throw "Invalid solver action for ${move}: $action"
    }

    if ($native -eq 'PENDING_PINNED_IL') {
        if ($dead -ne 'PENDING' -or $dependency -ne 'PENDING' -or $action -ne 'NeedsMoreModeling') {
            throw "Pending pinned-IL row cannot be promoted or partially guessed: $move"
        }
    } elseif ($dead -eq 'PENDING' -or $dependency -eq 'PENDING') {
        throw "Resolved native target row still contains PENDING evidence fields: $move"
    }

    $rows[$move] = [pscustomobject]@{
        Native = $native
        Dead = $dead
        Dependency = $dependency
        Action = $action
    }
}

if ($rows.Count -ne $expectedMoves.Count) {
    throw "Expected $($expectedMoves.Count) monster target audit rows, found $($rows.Count)."
}

foreach ($move in $expectedMoves) {
    if (-not $rows.ContainsKey($move)) {
        throw "Missing monster target audit row: $move"
    }
}
foreach ($move in $rows.Keys) {
    if ($move -notin $expectedMoves) {
        throw "Unexpected monster target audit row: $move"
    }
}

$pending = @($rows.Values | Where-Object Native -eq 'PENDING_PINNED_IL').Count
$resolved = $rows.Count - $pending
Write-Output "MONSTER_TARGET_FANOUT_AUDIT_CHECKS_PASS rows=$($rows.Count) resolved=$resolved pending=$pending"
exit 0
