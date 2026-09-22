#requires -Version 7.0

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskPath = Join-Path $sourceRoot 'docs/multiplayer/NEXT_LOCAL_01071_MONSTER_TARGET_AUDIT.md'
$auditPath = Join-Path $sourceRoot 'docs/compat/0.107.1/MONSTER_TARGET_FANOUT_AUDIT.md'
$runtimePath = Join-Path $sourceRoot 'src/Prediction/MonsterMoveEffects.cs'

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

$fanOutSafeMoves = @(
    $rows.GetEnumerator() |
        Where-Object { $_.Value.Action -eq 'FanOutSafe' } |
        ForEach-Object { $_.Key }
)
if ($fanOutSafeMoves.Count -ne 50) {
    throw "Expected 50 pinned FanOutSafe rows, found $($fanOutSafeMoves.Count)."
}

$runtimeText = [IO.File]::ReadAllText($runtimePath)
$allowListMatch = [regex]::Match(
    $runtimeText,
    '(?s)private static bool IsPinnedFanOutSafe\(.*?(?=\r?\n\s*public static void ApplyBeforeAttack)')
if (-not $allowListMatch.Success) {
    throw 'MonsterMoveEffects is missing the pinned FanOutSafe runtime allow-list.'
}
$runtimePairs = @{}
foreach ($match in [regex]::Matches($allowListMatch.Value, '\("(?<monster>[^"]+)", "(?<move>[^"]+)"\)')) {
    $key = "$($match.Groups['monster'].Value).$($match.Groups['move'].Value)"
    if ($runtimePairs.ContainsKey($key)) {
        throw "Duplicate runtime FanOutSafe pair: $key"
    }
    $runtimePairs[$key] = $true
}
if ($runtimePairs.Count -ne $fanOutSafeMoves.Count) {
    throw "Runtime FanOutSafe allow-list count mismatch: audit=$($fanOutSafeMoves.Count) runtime=$($runtimePairs.Count)."
}
foreach ($move in $fanOutSafeMoves) {
    if (-not $runtimePairs.ContainsKey($move)) {
        throw "Pinned FanOutSafe move is not enabled in runtime fanout: $move"
    }
}
foreach ($move in $runtimePairs.Keys) {
    if (-not $rows.ContainsKey($move) -or $rows[$move].Action -ne 'FanOutSafe') {
        throw "Runtime fanout contains a move not classified FanOutSafe by pinned IL: $move"
    }
}
if (-not $runtimeText.Contains('foreach (Creature target in simulator.State.PlayerCreatures)')) {
    throw 'Pinned runtime fanout no longer enumerates the captured player roster.'
}
if (-not $runtimeText.Contains('bool applySharedPreamble = true;')) {
    throw 'Pinned runtime fanout no longer protects one-per-move shared preamble state.'
}

Write-Output "MONSTER_TARGET_FANOUT_AUDIT_CHECKS_PASS rows=$($rows.Count) resolved=$resolved pending=$pending runtimeFanOut=$($runtimePairs.Count)"
exit 0
