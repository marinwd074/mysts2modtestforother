#requires -Version 7.0

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$auditPath = Join-Path $sourceRoot 'docs/compat/0.107.1/MONSTER_TARGET_FANOUT_AUDIT.md'
$runtimePath = Join-Path $sourceRoot 'src/Prediction/MonsterMoveEffects.cs'
$runtimeTargetsPath = Join-Path $sourceRoot 'src/Prediction/MonsterMoveEffects.MultiplayerTargets.cs'
$staticValuesPath = Join-Path $sourceRoot 'src/Prediction/MonsterMoveEffects.StaticValues.cs'
$knowledgeChoicePath = Join-Path $sourceRoot 'src/Prediction/KnowledgeDemonChoiceSupport.cs'
$knowledgeStatePath = Join-Path $sourceRoot 'src/Search/SimulatedCombatState.KnowledgeDemon.cs'
$stateEvaluationPath = Join-Path $sourceRoot 'src/Search/CombatBeamSolver.StateEvaluation.cs'

$auditLines = [IO.File]::ReadAllLines($auditPath)

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

$expectedMoveCount = 64
if ($rows.Count -ne $expectedMoveCount) {
    throw "Expected $expectedMoveCount monster target audit rows, found $($rows.Count)."
}

$pending = @($rows.Values | Where-Object Native -eq 'PENDING_PINNED_IL').Count
$resolved = $rows.Count - $pending
$perTargetRng = @($rows.Values | Where-Object Action -eq 'NeedsPerTargetRng').Count
if ($perTargetRng -ne 0) {
    throw "Pinned per-target RNG audit still has unresolved rows: $perTargetRng"
}

$fanOutSafeMoves = @(
    $rows.GetEnumerator() |
        Where-Object { $_.Value.Action -eq 'FanOutSafe' } |
        ForEach-Object { $_.Key }
)
if ($fanOutSafeMoves.Count -ne 63) {
    throw "Expected 63 pinned FanOutSafe rows, found $($fanOutSafeMoves.Count)."
}

$runtimeMainText = [IO.File]::ReadAllText($runtimePath)
$runtimeTargetsText = [IO.File]::ReadAllText($runtimeTargetsPath)
# Put the dispatcher first so the allow-list regex can still terminate at ApplyBeforeAttack
# from the main implementation without depending on one giant source file.
$runtimeText = $runtimeTargetsText + [Environment]::NewLine + $runtimeMainText
$runtimePairs = @{}
$runtimeGroups = @(
    [pscustomobject]@{ Name = 'simple'; Pattern = '(?s)private static bool IsPinnedSimpleFanOutSafe\(.*?(?=\r?\n\s*private static bool IsPinnedSplitFanOutSafe)' },
    [pscustomobject]@{ Name = 'split'; Pattern = '(?s)private static bool IsPinnedSplitFanOutSafe\(.*?(?=\r?\n\s*private static bool IsPinnedSpecialRngFanOutSafe)' },
    [pscustomobject]@{ Name = 'special-rng'; Pattern = '(?s)private static bool IsPinnedSpecialRngFanOutSafe\(.*?(?=\r?\n\s*private enum MultiplayerTargetMode)' }
)
foreach ($group in $runtimeGroups) {
    $allowListMatch = [regex]::Match($runtimeText, $group.Pattern)
    if (-not $allowListMatch.Success) {
        throw "MonsterMoveEffects is missing the pinned $($group.Name) FanOutSafe runtime allow-list."
    }
    foreach ($match in [regex]::Matches($allowListMatch.Value, '\("(?<monster>[^"]+)", "(?<move>[^"]+)"\)')) {
        $key = "$($match.Groups['monster'].Value).$($match.Groups['move'].Value)"
        if ($runtimePairs.ContainsKey($key)) {
            throw "Duplicate runtime FanOutSafe pair across runtime groups: $key"
        }
        $runtimePairs[$key] = $group.Name
    }
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
if (($runtimeMainText.Contains('private static bool IsPinnedSimpleFanOutSafe')) -or
    ($runtimeMainText.Contains('private enum MultiplayerTargetMode'))) {
    throw 'Multiplayer monster target routing leaked back into MonsterMoveEffects.cs.'
}
if ((-not $runtimeTargetsText.Contains('private enum MultiplayerTargetMode')) -or
    (-not $runtimeTargetsText.Contains('ApplyPerPlayerTargets(')) -or
    (-not $runtimeTargetsText.Contains('ResolveMultiplayerTargetMode('))) {
    throw 'Dedicated multiplayer monster target dispatcher is incomplete.'
}

if (-not $runtimeText.Contains('foreach (Creature target in simulator.State.PlayerCreatures)')) {
    throw 'Pinned runtime fanout no longer enumerates the captured player roster.'
}
if (-not $runtimeText.Contains('bool applySharedPreamble = true;')) {
    throw 'Pinned simple fanout no longer protects one-per-move shared preamble state.'
}
$splitExpected = @(
    'Aeonglass.INCREASING_INTENSITY_MOVE',
    'TestSubject.BURNING_GROWL_MOVE',
    'LagavulinMatriarch.SOUL_SIPHON_MOVE',
    'Wriggler.WRIGGLE_MOVE',
    'TheLost.DEBILITATING_SMOG',
    'SlimedBerserker.LEECHING_HUG_MOVE',
    'TheForgotten.MIASMA',
    'WaterfallGiant.STOMP_MOVE',
    'GremlinMerc.DOUBLE_SMASH_MOVE'
)
foreach ($move in $splitExpected) {
    if (-not $runtimePairs.ContainsKey($move) -or $runtimePairs[$move] -ne 'split') {
        throw "Move requiring owner-once ordering is not in the split fanout path: $move"
    }
}
if (-not $runtimeText.Contains('ApplyPerPlayerThenOwnerOnce(simulator, combat, move, out killedOwner)')) {
    throw 'Pinned owner-once moves are no longer routed through ApplyPerPlayerThenOwnerOnce.'
}
if ((-not $runtimeText.Contains('ApplyPerPlayerTargetEffect(')) -or
    (-not $runtimeText.Contains('ApplyOwnerEffectOnce('))) {
    throw 'Pinned owner-once path is not split into per-player target and owner-once effects.'
}
if (-not $runtimeText.Contains('combat.RecordThievery(simulator, move.Owner);')) {
    throw 'Gremlin Merc split fanout lost its pre-target thievery side effect.'
}
$rngExpected = @(
    'ThievingHopper.THIEVERY_MOVE',
    'TheInsatiable.LIQUIFY_GROUND_MOVE'
)
foreach ($move in $rngExpected) {
    if (-not $runtimePairs.ContainsKey($move) -or $runtimePairs[$move] -ne 'special-rng') {
        throw "Phase-sensitive RNG move is not in the special RNG fanout path: $move"
    }
}
if (-not $runtimePairs.ContainsKey('Noisebot.NOISE_MOVE') -or $runtimePairs['Noisebot.NOISE_MOVE'] -ne 'simple') {
    throw 'Noisebot.NOISE_MOVE must use sequential simple fanout.'
}
if (-not $runtimePairs.ContainsKey('SoulFysh.BECKON_MOVE') -or $runtimePairs['SoulFysh.BECKON_MOVE'] -ne 'simple') {
    throw 'SoulFysh.BECKON_MOVE must use sequential simple fanout.'
}
if (-not $runtimeText.Contains('List<(Creature Target, PredictedCard Card)> stolenCards = [];')) {
    throw 'Thieving Hopper no longer separates card removal from Swipe creation.'
}
if (-not $runtimeText.Contains('combat.SetMonsterBool(move.Owner, "HasLiquified", true);')) {
    throw 'Liquify Ground no longer records the native HasLiquified owner state.'
}

$staticValuesText = [IO.File]::ReadAllText($staticValuesPath)
if (-not $staticValuesText.Contains('["Axebot"] = ["BootUpBlock", "BootUpStrGain", "StockAmount"]')) {
    throw 'Axebot static capture must use pinned 0.107.1 StockAmount.'
}
if ($staticValuesText.Contains('"RespawnCount"') -or $runtimeText.Contains('"RespawnCount"')) {
    throw 'Axebot 0.107.1 path must not reference the nonexistent RespawnCount member.'
}
if (-not $runtimeText.Contains('(2 - combat.GetMonsterStaticInt(move.Owner, "StockAmount"))')) {
    throw 'Axebot BOOT_UP_MOVE lost the pinned 0.107.1 BootUpStrGain * (2 - StockAmount) formula.'
}

$remoteChoiceRows = @($rows.GetEnumerator() | Where-Object { $_.Value.Action -eq 'NeedsRemoteChoiceFailClosed' })
if ($remoteChoiceRows.Count -ne 1 -or $remoteChoiceRows[0].Key -ne 'KnowledgeDemon.CURSE_OF_KNOWLEDGE_MOVE') {
    throw 'Knowledge Demon must remain the only pinned remote-choice fail-closed row.'
}

$knowledgeChoiceText = [IO.File]::ReadAllText($knowledgeChoicePath)
$knowledgeStateText = [IO.File]::ReadAllText($knowledgeStatePath)
$stateEvaluationText = [IO.File]::ReadAllText($stateEvaluationPath)
if ((-not $runtimeText.Contains('KnowledgeDemonChoiceSupport.BlockOnUncontrolledMultiplayerChoice(')) -or (-not $runtimeText.Contains('simulator.State.PlayerCreatures.Count > 1'))) {
    throw 'Multiplayer Knowledge Demon no longer stops before local-only curse resolution.'
}
if ((-not $knowledgeChoiceText.Contains('IsUncontrolledRemoteChoice: true')) -or (-not $knowledgeChoiceText.Contains('远端 Knowledge Demon 玩家选择不能作为本地求解器可优化分支'))) {
    throw 'Knowledge Demon remote choice is no longer explicitly non-optimizable.'
}
if (-not $knowledgeStateText.Contains('HasUnsupportedKnowledgeDemonMultiplayerChoice')) {
    throw 'Knowledge Demon multiplayer choice lost its explicit unsupported-state marker.'
}
if ((-not $stateEvaluationText.Contains('if (combat.HasUnsupportedKnowledgeDemonMultiplayerChoice)')) -or (-not $stateEvaluationText.Contains('boundary = SearchBoundaryReason.UnsupportedEffect;'))) {
    throw 'Knowledge Demon multiplayer choice is no longer mapped to UnsupportedEffect.'
}

Write-Output "MONSTER_TARGET_FANOUT_AUDIT_CHECKS_PASS rows=$($rows.Count) resolved=$resolved pending=$pending runtimeFanOut=$($runtimePairs.Count)"
exit 0
