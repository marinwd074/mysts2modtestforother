#requires -Version 7.0

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$searchRoot = Join-Path $repositoryRoot "src\Search"

$forbiddenSearchReferences = @(
    "SolverSearchPhase",
    "ShortProfile",
    "DeepProfile",
    "shortCheckpointMilliseconds",
    "SolveWithNarrowBeamRecovery",
    "BuildNarrowBeamRecoveryProfile",
    "RecoverDeferredTurnFrontier",
    "DeferredTurnFrontier",
    "SolverSettings.Current",
    "Entry.Logger",
    "SolverController",
    "SolverOverlay",
    "SolverText",
    "SolverRelicEffectText",
    "SolverUiModelNames",
    "SolverActionTextIdentity",
    "SolverLocaleRefresh",
    "SolvedRouteCache",
    "RunStatistics",
    "UnattendedTestRunner"
)

$violations = [System.Collections.Generic.List[string]]::new()
$crossTurnTranspositionPath = Join-Path $repositoryRoot 'src/Search/CombatBeamSolver.Expansion.cs'
$crossTurnTranspositionText = [IO.File]::ReadAllText($crossTurnTranspositionPath)
foreach ($crossTurnTranspositionRule in @(
    'private static bool HasCrossTurnTranspositionLease(SearchNode candidate)',
    '=> candidate.CrossTurnProbe != null;',
    '|| HasCrossTurnTranspositionLease(candidate)',
    '|| HasCrossTurnTranspositionLease(node)')) {
    if (-not $crossTurnTranspositionText.Contains($crossTurnTranspositionRule)) {
        $violations.Add("${crossTurnTranspositionPath}: cross-turn scheduling lease lost transposition protection '$crossTurnTranspositionRule'")
    }
}

$crossTurnTranspositionModelPath = Join-Path $repositoryRoot 'src/Search/CombatBeamSolver.Models.cs'
$crossTurnTranspositionModelText = [IO.File]::ReadAllText($crossTurnTranspositionModelPath)
if (-not $crossTurnTranspositionModelText.Contains('if (node.CrossTurnProbe != null)')) {
    $violations.Add("${crossTurnTranspositionModelPath}: cache rebuild must exclude active cross-turn probes")
}

$transpositionPath = Join-Path $repositoryRoot 'src/Search/CombatBeamSolver.Transpositions.cs'
$transpositionText = [IO.File]::ReadAllText($transpositionPath)
foreach ($transpositionRule in @(
    'SearchRouteTraits Traits',
    'bool HasNonPotionAction',
    'SearchBoundaryReason BoundaryReason',
    'bool PlayerDead',
    'bool AllEnemiesDead',
    'IReadOnlyList<PredictionGap> PredictionGaps',
    'CombatProgressState CombatProgress',
    '(left.Traits & right.Traits) == right.Traits',
    '(!left.HasNonPotionAction || right.HasNonPotionAction)',
    'left.BoundaryReason == right.BoundaryReason',
    'left.PlayerDead == right.PlayerDead',
    'left.AllEnemiesDead == right.AllEnemiesDead',
    'left.PredictionGaps.SequenceEqual(right.PredictionGaps)',
    'left.CombatProgress == right.CombatProgress')) {
    if (-not $transpositionText.Contains($transpositionRule)) {
        $violations.Add("${transpositionPath}: path-sensitive transposition dominance drifted '$transpositionRule'")
    }
}

$transpositionModelPath = Join-Path $repositoryRoot 'src/Search/CombatBeamSolver.Models.cs'
$transpositionModelText = [IO.File]::ReadAllText($transpositionModelPath)
foreach ($transpositionRebuildRule in @(
    'node.Traits,',
    'node.HasNonPotionAction,',
    'node.BoundaryReason,',
    'node.Snapshot.PlayerDead,',
    'node.Snapshot.AllEnemiesDead,',
    'node.Snapshot.PredictionGaps,',
    'node.CombatProgress);',
    'Transpositions.TryGetValue(node.StateKey, out TranspositionFrontier? existing)',
    '_ = existing.TryAccept(label);')) {
    if (-not $transpositionModelText.Contains($transpositionRebuildRule)) {
        $violations.Add("${transpositionModelPath}: transposition frontier rebuild drifted '$transpositionRebuildRule'")
    }
}

$retentionOrderPath = Join-Path $repositoryRoot 'src/Search/CombatBeamSolver.Retention.cs'
$retentionOrderText = [IO.File]::ReadAllText($retentionOrderPath)
foreach ($retentionOrderRule in @(
    '=> selected.Sort(CompareRetainedOrder);',
    'private static int CompareRetainedOrder(SearchNode left, SearchNode right)',
    'CompareCycleCandidateDeterministicFingerprints(left, right)')) {
    if (-not $retentionOrderText.Contains($retentionOrderRule)) {
        $violations.Add("${retentionOrderPath}: retained frontier deterministic tie-break drifted '$retentionOrderRule'")
    }
}

$monsterValueReaderPath = Join-Path $repositoryRoot 'src/Prediction/MonsterValueReader.cs'
$monsterValueReaderText = [IO.File]::ReadAllText($monsterValueReaderPath)
foreach ($monsterReaderRule in @(
    'BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic',
    'getter.IsStatic ? null : typed',
    'field.IsStatic ? null : typed')) {
    if (-not $monsterValueReaderText.Contains($monsterReaderRule)) {
        $violations.Add("${monsterValueReaderPath}: monster static member capture drifted '$monsterReaderRule'")
    }
}

$monsterStaticValuesPath = Join-Path $repositoryRoot 'src/Prediction/MonsterMoveEffects.StaticValues.cs'
$monsterStaticValuesText = [IO.File]::ReadAllText($monsterStaticValuesPath)
if (-not $monsterStaticValuesText.Contains('["LouseProgenitor"] = ["CurlBlock", "_growStrength"]')) {
    $violations.Add("${monsterStaticValuesPath}: LouseProgenitor 0.107.1 grow strength member must be _growStrength")
}
if ($monsterStaticValuesText.Contains('["LouseProgenitor"] = ["CurlBlock", "GrowStrength"]')) {
    $violations.Add("${monsterStaticValuesPath}: nonexistent LouseProgenitor.GrowStrength leaked into root capture")
}

$monsterMoveSemanticsPath = Join-Path $repositoryRoot 'src/Prediction/MonsterMoveSemantics.cs'
$monsterMoveSemanticsText = [IO.File]::ReadAllText($monsterMoveSemanticsPath)
foreach ($multiplayerMonsterAttackRule in @(
    'simulator.State.PlayerCreatures,',
    'public static IReadOnlyList<DamageResult> DamagePlayers(',
    'return simulator.Damage(players, baseDamage, ValueProp.Move, attacker);',
    'if (result.WasFullyBlocked)',
    'bool anyPlayerAlive = false;')) {
    if (-not $monsterMoveSemanticsText.Contains($multiplayerMonsterAttackRule)) {
        $violations.Add("${monsterMoveSemanticsPath}: multiplayer monster attack targeting drifted '$multiplayerMonsterAttackRule'")
    }
}

$predictionDamagePath = Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.Damage.cs'
$predictionDamageText = [IO.File]::ReadAllText($predictionDamagePath)
if (-not $predictionDamageText.Contains('hookCombat.NotifyPlayerHooksDeactivated(player);')) {
    $violations.Add("${predictionDamagePath}: simulated player death must deactivate that player's later hooks")
}

$simulatedCombatStatePath = Join-Path $repositoryRoot 'src/Search/SimulatedCombatState.cs'
$simulatedCombatStateText = [IO.File]::ReadAllText($simulatedCombatStatePath)
foreach ($deadPlayerHookRule in @(
    'private bool HasInactiveCapturedPlayer()',
    'private bool IsOwnedByInactivePlayer(AbstractModel listener)',
    'internal void NotifyPlayerHooksDeactivated(Player player)',
    'if (IsOwnedByInactivePlayer(listener))')) {
    if (-not $simulatedCombatStateText.Contains($deadPlayerHookRule)) {
        $violations.Add("${simulatedCombatStatePath}: multiplayer dead-player hook boundary drifted '$deadPlayerHookRule'")
    }
}

$monsterMoveEffectsPath = Join-Path $repositoryRoot 'src/Prediction/MonsterMoveEffects.cs'
$monsterMoveEffectsText = [IO.File]::ReadAllText($monsterMoveEffectsPath)
if (-not $monsterMoveEffectsText.Contains('combat.GetMonsterStaticInt(move.Owner, "_growStrength")')) {
    $violations.Add("${monsterMoveEffectsPath}: LouseProgenitor CURL_AND_GROW must consume captured _growStrength")
}

$multiplayerCardSearchPaths = @(
    'src/Search/CombatBeamSolver.Expansion.cs',
    'src/Search/CombatBeamSolver.ParallelExpansion.cs'
)
foreach ($relativePath in $multiplayerCardSearchPaths) {
    $path = Join-Path $repositoryRoot $relativePath
    if (-not (Select-String -LiteralPath $path -SimpleMatch 'CanConsiderCardAction(card)' -Quiet)) {
        $violations.Add("$($path): multiplayer-only card search exclusion is missing")
    }
}

$contractRunnerPath = Join-Path $repositoryRoot 'tools/run-contract-tests.ps1'
$contractRunnerText = [IO.File]::ReadAllText($contractRunnerPath)
if (-not $contractRunnerText.Contains("Invoke-DotnetContract 'BfwsResearchChecks' 'tools/BfwsResearchChecks/BfwsResearchChecks.csproj'")) {
    $violations.Add("${contractRunnerPath}: BFWS novelty/cap contracts must remain in the L1 contract suite")
}

$cardPlayCompatibilityPath = Join-Path $repositoryRoot 'src/Compatibility/Sts2CardPlayCompatibility.cs'
$cardPlayContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardExecutionContinuation.cs'
$cardPlayCompatibilityText = [IO.File]::ReadAllText($cardPlayCompatibilityPath)
$cardPlayContinuationText = [IO.File]::ReadAllText($cardPlayContinuationPath)
if (-not $cardPlayCompatibilityText.Contains('#if STS2_01071') -or
    -not $cardPlayCompatibilityText.Contains('return false;') -or
    -not $cardPlayCompatibilityText.Contains('return isOverOrEnding;')) {
    $violations.Add("${cardPlayCompatibilityPath}: Replay end-of-combat behavior must stay version-gated")
}
if (-not $cardPlayContinuationText.Contains('Sts2CardPlayCompatibility.ShouldStopRepeatedPlayWhenCombatEnding(IsOverOrEnding)')) {
    $violations.Add("${cardPlayContinuationPath}: repeated card plays must use the versioned Replay end-of-combat gate")
}
if ($cardPlayContinuationText.Contains('if (IsOverOrEnding) break;')) {
    $violations.Add("${cardPlayContinuationPath}: unconditional v0.108+ Replay stop behavior leaked into the 0.107.1 path")
}

$enchantmentOnPlayPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Enchantments/OnPlay/EnchantmentOnPlayMirrors.cs'
$enchantmentOnPlayText = [IO.File]::ReadAllText($enchantmentOnPlayPath)
if (-not $cardPlayCompatibilityText.Contains('ShouldDisableSwiftBeforeDraw')) {
    $violations.Add("${cardPlayCompatibilityPath}: Swift draw/disable order must stay version-gated")
}
foreach ($swiftContinuationRule in @(
    'Sts2CardPlayCompatibility.ShouldDisableSwiftBeforeDraw()',
    'context.Simulator.Draw(context.PreviewCard.Owner, enchantment.Amount);',
    'AppendExecutionContinuation(new SwiftDisableExecutionFrame(context.Card))',
    'private sealed record SwiftDisableExecutionFrame')) {
    if (-not $enchantmentOnPlayText.Contains($swiftContinuationRule)) {
        $violations.Add("${enchantmentOnPlayPath}: 0.107.1 Swift continuation rule is missing '$swiftContinuationRule'")
    }
}

$generationPoolPath = Join-Path $repositoryRoot 'src/Search/RootCombatCardGenerationPoolSnapshot.cs'
$generationPoolText = [IO.File]::ReadAllText($generationPoolPath)
foreach ($laterGenerationExclusion in @('Nightmare', 'Transfigure')) {
    if ($generationPoolText.Contains($laterGenerationExclusion)) {
        $violations.Add("${generationPoolPath}: $laterGenerationExclusion must remain model-driven; v0.108 generation exclusion must not be hard-coded")
    }
}
$transformationPoolPath = Join-Path $repositoryRoot 'src/Search/RootCombatTransformationPoolSnapshot.cs'
$transformationPoolText = [IO.File]::ReadAllText($transformationPoolPath)
if ($transformationPoolText.Contains('AscendersBane')) {
    $violations.Add("${transformationPoolPath}: v0.108 Ascender's Bane transform exclusion must not be hard-coded into the 0.107.1 pool")
}
$afterBlockBrokenPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Block/AfterBlockBrokenMirrors.cs'
$afterBlockBrokenText = [IO.File]::ReadAllText($afterBlockBrokenPath)
if (-not $afterBlockBrokenText.Contains('#if !STS2_01071') -or
    -not $afterBlockBrokenText.Contains('registry.Register<HandDrill>(HandleHandDrill);')) {
    $violations.Add("${afterBlockBrokenPath}: Hand Drill AfterBlockBroken behavior must remain excluded from 0.107.1")
}

$cardGenerationMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardGenerationCardMirrors.cs'
$cardGenerationMirrorText = [IO.File]::ReadAllText($cardGenerationMirrorPath)
$abundanceStart = $cardGenerationMirrorText.IndexOf('public static void AbundanceOnPlay')
if ($abundanceStart -ge 0) {
    $abundancePrefixStart = [Math]::Max(0, $abundanceStart - 120)
    $abundancePrefix = $cardGenerationMirrorText.Substring($abundancePrefixStart, $abundanceStart - $abundancePrefixStart)
    if (-not $abundancePrefix.Contains('#if !STS2_01071')) {
        $violations.Add("${cardGenerationMirrorPath}: post-0.107.1 Abundance must stay outside the STS2_01071 build")
    }
}

$post01071GuardRules = @(
    @{ Path = (Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardDrawnMirrors.cs'); Marker = 'registry.Register<CacophonyPower>(HandleCacophonyPower);' },
    @{ Path = (Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardExhaustedMirrors.cs'); Marker = 'registry.Register<Midnight>(HandleMidnight);' },
    @{ Path = (Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardGeneratedForCombatMirrors.cs'); Marker = 'registry.Register<SoulboundPower>(HandleSoulboundPower);' },
    @{ Path = (Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardPlayedMirrors.cs'); Marker = 'registry.Register<ImitationLearningPower>(HandleImitationLearningPower);' },
    @{ Path = (Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/BeforeCardPlayedMirrors.cs'); Marker = 'registry.Register<ImitationLearningPower>(HandleImitationLearningPower);' },
    @{ Path = (Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Damage/AfterDamageGivenMirrors.cs'); Marker = 'registry.Register<ConcoctPower>(HandleConcoctPower);' },
    @{ Path = (Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Damage/AfterDamageGivenMirrors.cs'); Marker = 'registry.Register<UnderworldPower>(HandleUnderworldPower);' },
    @{ Path = (Join-Path $repositoryRoot 'src/Prediction/TurnStartPowerSupport.cs'); Marker = 'case HibernatePower:' },
    @{ Path = (Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs'); Marker = 'registry.Register<Constellation>(CardDrawCardMirrors.ConstellationOnPlay);' },
    @{ Path = (Join-Path $repositoryRoot 'src/Prediction/CardOnPlayCompensationCatalog.cs'); Marker = 'typeof(TheBall),' }
)
foreach ($rule in $post01071GuardRules) {
    $text = [IO.File]::ReadAllText($rule.Path)
    $markerIndex = $text.IndexOf($rule.Marker)
    if ($markerIndex -lt 0) {
        $violations.Add("$($rule.Path): expected post-0.107.1 compatibility marker missing '$($rule.Marker)'")
        continue
    }
    $prefixStart = [Math]::Max(0, $markerIndex - 180)
    $prefix = $text.Substring($prefixStart, $markerIndex - $prefixStart)
    if (-not $prefix.Contains('#if !STS2_01071')) {
        $violations.Add("$($rule.Path): post-0.107.1 behavior escaped STS2_01071 guard '$($rule.Marker)'")
    }
}

$cardEffectSpecPath = Join-Path $repositoryRoot 'src/Prediction/CardEffectSpecRegistry.cs'
$cardEffectSpecText = [IO.File]::ReadAllText($cardEffectSpecPath)
foreach ($modelDrivenRule in @(
    'combat.RecordBrightestFlameMaxHpLoss(card.DynamicVars.MaxHp.IntValue)',
    'ownerState.MaxHp - card.DynamicVars.MaxHp.IntValue',
    'decimal increase = rampage.DynamicVars["Increase"].BaseValue',
    'mutableRampage.DynamicVars.Damage.BaseValue += increase')) {
    if (-not $cardEffectSpecText.Contains($modelDrivenRule)) {
        $violations.Add("${cardEffectSpecPath}: later-patch numeric trap must remain model-driven '$modelDrivenRule'")
    }
}

$corePowerSupportPath = Join-Path $repositoryRoot 'src/Prediction/CorePowerSupport.cs'
$corePowerSupportText = [IO.File]::ReadAllText($corePowerSupportPath)
foreach ($modelDrivenRule in @(
    'PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue)',
    'combat.AddEnergyNextTurn(card.Owner, card.DynamicVars.Energy.IntValue)',
    'combat.Apply<FocusPower>(owner, card.DynamicVars["FocusPower"].IntValue, owner)')) {
    if (-not $corePowerSupportText.Contains($modelDrivenRule)) {
        $violations.Add("${corePowerSupportPath}: later-patch card value must remain sourced from the pinned CardModel '$modelDrivenRule'")
    }
}

$powerPredictionStatePath = Join-Path $repositoryRoot 'src/Prediction/PowerPredictionStateSupport.cs'
$powerPredictionStateText = [IO.File]::ReadAllText($powerPredictionStatePath)
if (($powerPredictionStateText.Contains('IPredictionStateForkable') -or
     $powerPredictionStateText.Contains('PredictionForkContext')) -and
    -not $powerPredictionStateText.Contains('using CombatSolver.Engine.Common;')) {
    $violations.Add("${powerPredictionStatePath}: prediction fork types require CombatSolver.Engine.Common import")
}

$cardPowerLatePath = Join-Path $repositoryRoot 'src/Prediction/CardPowerOnPlaySupport.SToZ.cs'
$cardPowerLateText = [IO.File]::ReadAllText($cardPowerLatePath)
foreach ($modelDrivenRule in @(
    'combat.Apply<ShroudPower>(owner, card.DynamicVars.Block.IntValue, owner)',
    'combat.Apply<ThunderPower>(owner, card.DynamicVars["ThunderPower"].IntValue, owner)')) {
    if (-not $cardPowerLateText.Contains($modelDrivenRule)) {
        $violations.Add("${cardPowerLatePath}: later-patch card value must remain sourced from the pinned CardModel '$modelDrivenRule'")
    }
}

$cardOnPlaySupportPath = Join-Path $repositoryRoot 'src/Prediction/CardOnPlaySupport.cs'
$cardOnPlaySupportText = [IO.File]::ReadAllText($cardOnPlaySupportPath)
if (-not $cardOnPlaySupportText.Contains('case Alignment:') -or
    -not $cardOnPlaySupportText.Contains('simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);')) {
    $violations.Add("${cardOnPlaySupportPath}: Alignment effect must stay model-driven; star cost belongs to the pinned CardModel")
}

$cardOnPlayMirrorsPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs'
$cardOnPlayMirrorsText = [IO.File]::ReadAllText($cardOnPlayMirrorsPath)
foreach ($genericCard in @('Rend', 'TimesUp')) {
    if ($cardOnPlayMirrorsText.Contains("registry.Register<$genericCard>")) {
        $violations.Add("${cardOnPlayMirrorsPath}: $genericCard must not gain a hard-coded post-0.107.1 OnPlay override")
    }
}

$generalCardMirrorsPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/GeneralCardMirrors.cs'
$generalCardMirrorsText = [IO.File]::ReadAllText($generalCardMirrorsPath)
foreach ($modelDrivenRule in @(
    'registry.Register<HowlFromBeyond>(GeneralCardMirrors.GeneralAttackOnPlay)',
    'TryGetDynamicVar(card, ["CalculatedDamage", "Damage", "OstyDamage"], out var damage)')) {
    $sourceText = if ($modelDrivenRule.StartsWith('registry.')) { $cardOnPlayMirrorsText } else { $generalCardMirrorsText }
    if (-not $sourceText.Contains($modelDrivenRule)) {
        $violations.Add("0.107.1 attack value must remain model-driven '$modelDrivenRule'")
    }
}

foreach ($modelDrivenRule in @(
    '[typeof(Colossus)] = [Owner<ColossusPower>("Colossus")]',
    '[typeof(SetupStrike)] = [Owner<SetupStrikePower>(card => card.DynamicVars.Strength.IntValue)]',
    'int count = card.DynamicVars.Cards.IntValue')) {
    if (-not $cardEffectSpecText.Contains($modelDrivenRule)) {
        $violations.Add("${cardEffectSpecPath}: v0.108 card delta must remain model-driven '$modelDrivenRule'")
    }
}

foreach ($modelDrivenRule in @(
    'combat.IncrementCrimsonMantle(owner, card.DynamicVars["CrimsonMantlePower"].IntValue)',
    'combat.ApplyAnticipate(owner, card.DynamicVars.Dexterity.IntValue, owner)',
    'combat.Apply<StrengthPower>(owner, card.DynamicVars["StrengthPower"].IntValue, owner)')) {
    if (-not $corePowerSupportText.Contains($modelDrivenRule)) {
        $violations.Add("${corePowerSupportPath}: v0.108 card delta must remain model-driven '$modelDrivenRule'")
    }
}

$cardPowerSupportPath = Join-Path $repositoryRoot 'src/Prediction/CardPowerOnPlaySupport.cs'
$cardPowerSupportText = [IO.File]::ReadAllText($cardPowerSupportPath)

foreach ($fixedPowerUnitRule in @(
    'combat.Apply<ConquerorPower>(target, 1, owner);',
    'combat.Apply<RetainHandPower>(owner, 1, owner);',
    'combat.Apply<ShadowStepPower>(owner, 1, owner);')) {
    if (-not $cardOnPlaySupportText.Contains($fixedPowerUnitRule)) {
        $violations.Add("${cardOnPlaySupportPath}: audited 0.107.1 fixed Power unit drifted '$fixedPowerUnitRule'")
    }
}
foreach ($fixedPowerUnitRule in @(
    'combat.Apply<AggressionPower>(owner, 1, owner);',
    'combat.Apply<DarkEmbracePower>(owner, 1, owner);')) {
    if (-not $corePowerSupportText.Contains($fixedPowerUnitRule)) {
        $violations.Add("${corePowerSupportPath}: audited 0.107.1 fixed Power unit drifted '$fixedPowerUnitRule'")
    }
}
foreach ($fixedPowerUnitRule in @(
    'combat.Apply<CalamityPower>(owner, 1, owner);',
    'combat.Apply<FanOfKnivesPower>(owner, 1, owner);',
    'combat.Apply<HelloWorldPower>(owner, 1, owner);',
    'combat.Apply<InfiniteBladesPower>(owner, 1, owner);')) {
    if (-not $cardPowerSupportText.Contains($fixedPowerUnitRule)) {
        $violations.Add("${cardPowerSupportPath}: audited 0.107.1 fixed Power unit drifted '$fixedPowerUnitRule'")
    }
}
if (-not $cardPowerLateText.Contains('combat.Apply<UnmovablePower>(owner, 1, owner);')) {
    $violations.Add("${cardPowerLatePath}: audited 0.107.1 Unmovable Power unit must remain one stack per card")
}

foreach ($fixedPowerUnitRule in @(
    'combat.Apply<ForbiddenGrimoirePower>(owner, 1, owner);',
    'combat.Apply<HellraiserPower>(owner, 1, owner);',
    'combat.Apply<MasterPlannerPower>(owner, 1, owner);',
    'combat.Apply<MayhemPower>(owner, 1, owner);',
    'combat.Apply<NostalgiaPower>(owner, 1, owner);',
    'combat.Apply<ReaperFormPower>(owner, 1, owner);')) {
    if (-not $cardPowerSupportText.Contains($fixedPowerUnitRule)) {
        $violations.Add("${cardPowerSupportPath}: audited 0.107.1 fixed Power unit drifted '$fixedPowerUnitRule'")
    }
}
foreach ($fixedPowerUnitRule in @(
    'combat.Apply<SeekingEdgePower>(owner, 1, owner);',
    'combat.Apply<StratagemPower>(owner, 1, owner);',
    'combat.Apply<SubroutinePower>(owner, 1, owner);',
    'combat.Apply<TheSealedThronePower>(owner, 1, owner);',
    'combat.Apply<ToolsOfTheTradePower>(owner, 1, owner);',
    'combat.Apply<TrashToTreasurePower>(owner, 1, owner);',
    'combat.Apply<TyrannyPower>(owner, 1, owner);')) {
    if (-not $cardPowerLateText.Contains($fixedPowerUnitRule)) {
        $violations.Add("${cardPowerLatePath}: audited 0.107.1 fixed Power unit drifted '$fixedPowerUnitRule'")
    }
}

foreach ($fixedCounterRule in @(
    '[typeof(ExpectAFight)] = [Owner<NoEnergyGainPower>(_ => 1)]',
    '[typeof(Pounce)] = [Owner<FreeSkillPower>(_ => 1)]',
    '[typeof(Predator)] = [Owner<DrawCardsNextTurnPower>(_ => 2)]',
    '[typeof(Rebound)] = [Owner<ReboundPower>(_ => 1)]',
    '[typeof(Reflect)] = [Owner<ReflectPower>(_ => 1)]',
    '[typeof(Synthesis)] = [Owner<FreePowerPower>(_ => 1)]',
    '[typeof(TagTeam)] = [Target<TagTeamPower>(_ => 1)]',
    '[typeof(TheGambit)] = [Owner<TheGambitPower>(_ => 1)]',
    '[typeof(Unrelenting)] = [Owner<FreeAttackPower>(_ => 1)]',
    '[typeof(Veilpiercer)] = [Owner<VeilpiercerPower>(_ => 1)]')) {
    if (-not $cardEffectSpecText.Contains($fixedCounterRule)) {
        $violations.Add("${cardEffectSpecPath}: audited 0.107.1 fixed counter/duration drifted '$fixedCounterRule'")
    }
}

$specialCountChecks = @(
    @{
        Path = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/ShouldPlayMirrors.cs'
        Rules = @(
            'return combat.GetCardPlayStartsThisTurn(normality.Owner.Creature) < 3;'
        )
    },
    @{
        Path = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardDrawnMirrors.cs'
        Rules = @(
            'context.MutablePreviewCard.EnergyCost.SetThisCombat(context.Rng.CombatEnergyCosts.NextInt(4));',
            'CountStatusCardsDrawnThisTurn(context.Simulator, player) <= 1'
        )
    },
    @{
        Path = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/BeforeCardPlayedMirrors.cs'
        Rules = @(
            'state.AttacksPlayed = (state.AttacksPlayed + 1) % 10;'
        )
    },
    @{
        Path = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Damage/ModifyDamageMirrors.cs'
        Rules = @(
            'SurroundedPower.Direction.Right when context.Dealer.HasPower<BackAttackLeftPower>() => 1.5m,',
            'SurroundedPower.Direction.Left when context.Dealer.HasPower<BackAttackRightPower>() => 1.5m,',
            'return state.AttackToDouble == context.CardSource.Original ? 2 : 1;',
            'state.AttacksPlayed == 9'
        )
    },
    @{
        Path = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardPlayedMirrors.cs'
        Rules = @(
            'if (state.AttacksPlayedThisTurn != 3)'
        )
    }
)
foreach ($specialCountCheck in $specialCountChecks) {
    $specialCountText = [IO.File]::ReadAllText($specialCountCheck.Path)
    foreach ($specialCountRule in $specialCountCheck.Rules) {
        if (-not $specialCountText.Contains($specialCountRule)) {
            $violations.Add("$($specialCountCheck.Path): audited 0.107.1 hard-coded count/multiplier drifted '$specialCountRule'")
        }
    }
}

$relicCounterCatalogPath = Join-Path $repositoryRoot 'src/Prediction/RelicCounterCatalog.cs'
$relicCounterCatalogText = [IO.File]::ReadAllText($relicCounterCatalogPath)
foreach ($relicCounterRule in @(
    'new(RelicCounterId.HappyFlower, () => ModelDb.Relic<HappyFlower>(), 3)',
    'new(RelicCounterId.FakeHappyFlower, () => ModelDb.Relic<FakeHappyFlower>(), 5)',
    'new(RelicCounterId.Pendulum, () => ModelDb.Relic<Pendulum>(), 3)',
    'new(RelicCounterId.PollinousCore, () => ModelDb.Relic<PollinousCore>(), 4)',
    'new(RelicCounterId.PenNib, () => ModelDb.Relic<PenNib>(), 10)',
    'new(RelicCounterId.Nunchaku, () => ModelDb.Relic<Nunchaku>(), 10)',
    'new(RelicCounterId.TuningFork, () => ModelDb.Relic<TuningFork>(), 10)',
    'new(RelicCounterId.JossPaper, () => ModelDb.Relic<JossPaper>(), 5)',
    'new(RelicCounterId.IronClub, () => ModelDb.Relic<IronClub>(), 4)',
    'new(RelicCounterId.GalacticDust, () => ModelDb.Relic<GalacticDust>(), 10)',
    'new(RelicCounterId.MeatOnTheBone, () => ModelDb.Relic<MeatOnTheBone>(), 2)',
    'All.Count > RelicCounterEvaluation.MaxPackedCounterCount',
    'entry.Period > RelicCounterEvaluation.MaxPackedPeriod')) {
    if (-not $relicCounterCatalogText.Contains($relicCounterRule)) {
        $violations.Add("${relicCounterCatalogPath}: audited relic counter period/packing guard drifted '$relicCounterRule'")
    }
}

$relicCounterPolicyPath = Join-Path $repositoryRoot 'src/Search/RelicCounterPolicy.cs'
$relicCounterPolicyText = [IO.File]::ReadAllText($relicCounterPolicyPath)
foreach ($packingRule in @(
    'internal const int PackedValueBits = 4;',
    'internal const int PackedValueMask = (1 << PackedValueBits) - 1;',
    'internal const int PackedSlotCapacity = sizeof(ulong) * 8 / PackedValueBits;',
    'internal const int MaxPackedCounterCount = PackedValueMask;',
    'internal const int MaxPackedPeriod = 1 << PackedValueBits;')) {
    if (-not $relicCounterPolicyText.Contains($packingRule)) {
        $violations.Add("${relicCounterPolicyPath}: relic counter packed representation drifted '$packingRule'")
    }
}

foreach ($turnSpecificRelicRule in @(
    @{ Path = 'src/Search/SimulatedCombatState.RelicTurnStart.cs'; Rule = 'case Candelabra when turn == 2:' },
    @{ Path = 'src/Search/SimulatedCombatState.RelicTurnStart.cs'; Rule = 'case Chandelier when turn == 3:' },
    @{ Path = 'src/Search/SimulatedCombatState.ReactiveRelics.cs'; Rule = 'case CaptainsWheel when turn == 3:' },
    @{ Path = 'src/Search/SimulatedCombatState.ReactiveRelics.cs'; Rule = 'case HornCleat when turn == 2:' },
    @{ Path = 'src/Search/SimulatedCombatState.ReactiveRelics.cs'; Rule = 'case SparklingRouge when turn == 3:' })) {
    $turnSpecificRelicPath = Join-Path $repositoryRoot $turnSpecificRelicRule.Path
    $turnSpecificRelicText = [IO.File]::ReadAllText($turnSpecificRelicPath)
    if (-not $turnSpecificRelicText.Contains($turnSpecificRelicRule.Rule)) {
        $violations.Add("${turnSpecificRelicPath}: audited 0.107.1 turn-specific relic trigger drifted '$($turnSpecificRelicRule.Rule)'")
    }
}

$actEndingBossPolicyPath = Join-Path $repositoryRoot 'src/Search/ActEndingBossPolicy.cs'
$actEndingBossPolicyText = [IO.File]::ReadAllText($actEndingBossPolicyPath)
foreach ($actBossRule in @(
    'BossHpRelief.ActClearFullHeal',
    'runState.AscensionLevel >= (int)AscensionLevel.WearyTraveler',
    'BossHpRelief.ActClearHeal => persistentHpValue * 5',
    'BossHpRelief.ActClearFullHeal or BossHpRelief.RunEnding => 0')) {
    if (-not $actEndingBossPolicyText.Contains($actBossRule)) {
        $violations.Add("${actEndingBossPolicyPath}: 0.107.1 act-transition HP relief drifted '$actBossRule'")
    }
}

$searchPolicySnapshotPath = Join-Path $repositoryRoot 'src/Search/SearchPolicySnapshot.cs'
$searchPolicySnapshotText = [IO.File]::ReadAllText($searchPolicySnapshotPath)
foreach ($act3BossRule in @(
    'actIndex == 2',
    '"TEST_SUBJECT_BOSS"',
    '"AEONGLASS_BOSS"',
    '"QUEEN_BOSS"')) {
    if (-not $searchPolicySnapshotText.Contains($act3BossRule)) {
        $violations.Add("${searchPolicySnapshotPath}: 0.107.1 Act 3 boss identity drifted '$act3BossRule'")
    }
}

$growthPolicyPath = Join-Path $repositoryRoot 'src/Search/GrowthPolicy.cs'
$growthPolicyText = [IO.File]::ReadAllText($growthPolicyPath)
if (-not $growthPolicyText.Contains('internal const int MaximumBudgetHp = 1000;')) {
    $violations.Add("${growthPolicyPath}: growth budget safety cap lost its single source of truth")
}
if ($growthPolicyText.Contains('Get(source) is < 0 or > 1000')) {
    $violations.Add("${growthPolicyPath}: built-in growth budget duplicated the hard-coded 1000 cap")
}

if (-not $cardPowerSupportText.Contains('combat.Apply<HauntPower>(owner, card.DynamicVars.HpLoss.IntValue, owner)')) {
    $violations.Add("${cardPowerSupportPath}: Haunt 0.107.1 HP-loss amount must come from the pinned card model")
}
$afterCardPlayedPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardPlayedMirrors.cs'
$afterCardPlayedText = [IO.File]::ReadAllText($afterCardPlayedPath)
foreach ($hauntRule in @(
    'registry.Register<HauntPower>(HandleHauntPower)',
    'context.Simulator.Damage(target, power.Amount, DamageProps.nonCardHpLoss, dealer: null)')) {
    if (-not $afterCardPlayedText.Contains($hauntRule)) {
        $violations.Add("${afterCardPlayedPath}: Haunt trigger must preserve the Power amount '$hauntRule'")
    }
}

foreach ($genericCard in @('FlickFlack', 'Devastate', 'Haunt', 'Reave')) {
    if ($cardOnPlayMirrorsText.Contains("registry.Register<$genericCard>")) {
        $violations.Add("${cardOnPlayMirrorsPath}: $genericCard must not duplicate v0.108 numeric values in a bespoke OnPlay mirror")
    }
}

$batch042Path = Join-Path $repositoryRoot 'src/Prediction/CardOnPlaySupport.Batch042.cs'
$batch042Text = [IO.File]::ReadAllText($batch042Path)
$calculatedVarSpecPath = Join-Path $repositoryRoot 'src/Prediction/CalculatedVarSpecRegistry.cs'
$calculatedVarSpecText = [IO.File]::ReadAllText($calculatedVarSpecPath)
$bespokeCardMirrorsPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/BespokeCardMirrors.cs'
$bespokeCardMirrorsText = [IO.File]::ReadAllText($bespokeCardMirrorsPath)

foreach ($finalPatchTrapRule in @(
    'SoulStorm => playerState.ExhaustPile.Cards.Count(candidate => candidate.Preview is Soul)',
    'CanonicalModels.Card<GiantRock>()',
    'card.DynamicVars.Damage.BaseValue')) {
    $sourceText = if ($finalPatchTrapRule.StartsWith('SoulStorm')) { $calculatedVarSpecText } else { $batch042Text }
    if (-not $sourceText.Contains($finalPatchTrapRule)) {
        $violations.Add("Final post-0.107.1 patch trap must remain model-driven '$finalPatchTrapRule'")
    }
}

foreach ($finalPatchTrapRule in @(
    'playedCard.MutablePreview.EnergyCost.SetThisCombat(0)',
    'AddFixed<Debris>(simulator, card, PileType.Hand, 1)',
    'combat.ForceStunnedMove(target',
    '[typeof(Relax)] =',
    '[typeof(Mangle)] = [Target<ManglePower>("StrengthLoss")]',
    '[typeof(CrushUnder)] = [AllEnemies<CrushUnderPower>("StrengthLoss")]',
    '[typeof(Salvo)] = [Owner<RetainHandPower>(_ => 1)]')) {
    if (-not $cardEffectSpecText.Contains($finalPatchTrapRule)) {
        $violations.Add("${cardEffectSpecPath}: final version-delta rule missing '$finalPatchTrapRule'")
    }
}

foreach ($finalPatchTrapRule in @(
    'combat.Apply<DemonFormPower>(owner, card.DynamicVars["StrengthPower"].IntValue, owner)',
    'simulator.GainBlock(owner, card.DynamicVars.Block, playedCard, cardPlay)',
    'card.DynamicVars["VulnerablePower"].IntValue',
    'combat.Apply<AccelerantPower>(owner, card.DynamicVars["Accelerant"].IntValue, owner)',
    'simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue)')) {
    if (-not $corePowerSupportText.Contains($finalPatchTrapRule)) {
        $violations.Add("${corePowerSupportPath}: final version-delta rule must remain model-driven '$finalPatchTrapRule'")
    }
}

foreach ($finalPatchTrapRule in @(
    'card.DynamicVars.HpLoss.IntValue',
    'simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue)')) {
    if (-not $cardOnPlaySupportText.Contains($finalPatchTrapRule)) {
        $violations.Add("${cardOnPlaySupportPath}: Bloodletting must remain model-driven '$finalPatchTrapRule'")
    }
}

if (-not $cardPowerSupportText.Contains('combat.Apply<CrueltyPower>(owner, card.DynamicVars["CrueltyPower"].IntValue, owner)')) {
    $violations.Add("${cardPowerSupportPath}: Cruelty Power amount must remain model-driven")
}
if (-not $cardPowerLateText.Contains('combat.Apply<VigorPower>(owner, card.DynamicVars["VigorPower"].IntValue, owner)')) {
    $violations.Add("${cardPowerLatePath}: Terraforming Vigor must remain model-driven")
}
foreach ($finalPatchTrapRule in @(
    'if (context.OwnerState.ExhaustPile.Cards.Count >= card.DynamicVars.Cards.IntValue)',
    'context.AttackAllOpponents();')) {
    if (-not $bespokeCardMirrorsText.Contains($finalPatchTrapRule)) {
        $violations.Add("${bespokeCardMirrorsPath}: Pact's End 0.107.1 path missing '$finalPatchTrapRule'")
    }
}
foreach ($genericCard in @('SoulStorm', 'MomentumStrike', 'DemonForm', 'Taunt', 'Bloodletting', 'Cruelty',
                           'Dominate', 'Accelerant', 'CollisionCourse', 'Sunder', 'Relax', 'Whistle',
                           'EchoingSlash', 'Terraforming', 'CrushUnder', 'Salvo')) {
    if ($cardOnPlayMirrorsText.Contains("registry.Register<$genericCard>")) {
        $violations.Add("${cardOnPlayMirrorsPath}: $genericCard must not gain a hard-coded later-patch numeric OnPlay mirror")
    }
}
if (-not $cardOnPlayMirrorsText.Contains('registry.Register<Mangle>(GeneralCardMirrors.GeneralAttackOnPlay)')) {
    $violations.Add("${cardOnPlayMirrorsPath}: Mangle damage must stay on the model-driven general attack mirror")
}
if (-not $cardOnPlayMirrorsText.Contains('registry.Register<PactsEnd>(BespokeCardMirrors.PactsEndOnPlay)')) {
    $violations.Add("${cardOnPlayMirrorsPath}: Pact's End must keep its conditional 0.107.1 bespoke path")
}
if (-not $cardOnPlayMirrorsText.Contains('registry.Register<Splash>(CardGenerationCardMirrors.SplashOnPlay)')) {
    $violations.Add("${cardOnPlayMirrorsPath}: Splash generation behavior must remain independent of its later rarity swap")
}

$poolLifetime = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Runtime/NodePoolSignalLifetimePatch.cs'))
foreach ($required in @('using ((Godot.Collections.Array)signals)', 'using var ownedArray', 'using (connection)', 'using (callable.Method)', 'using (signal.Name)')) {
    if (-not $poolLifetime.Contains($required)) { $violations.Add("Node pool wrapper ownership missing: $required") }
}
if ($poolLifetime.Contains('GC.Collect') -or $poolLifetime.Contains('QueueFree')) {
    $violations.Add('Node pool signal cleanup owns temporary wrappers, not nodes or process GC.')
}
$normalityMirror = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/ShouldPlayMirrors.cs'))
if (-not $normalityMirror.Contains('registry.Register<Normality>(HandleNormality)')) {
    $violations.Add('Normality must use the shared ShouldPlay mirror for manual and automatic cards.')
}
$playerTurnEndCallers = @(
    "src/Search/CombatBeamSolver.Expansion.cs",
    "src/Runtime/LiveEndTurnRiskEvaluator.cs",
    "src/Testing/UnattendedTestRunner.cs",
    "src/Testing/UnattendedTestRunner.Potions.cs"
)
foreach ($relativePath in $playerTurnEndCallers) {
    $callerPath = Join-Path $repositoryRoot $relativePath
    foreach ($reference in @(
        "CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(",
        "TurnStartRelicSupport.TriggerAfterSideTurnEnd(",
        "HookMirrors.AfterSideTurnEndLate(")) {
        foreach ($match in Select-String -LiteralPath $callerPath -SimpleMatch $reference) {
            $violations.Add("$($match.Path):$($match.LineNumber): player phase two must use PlayerTurnEndLifecycle")
        }
    }
}
$searchFiles = Get-ChildItem -LiteralPath $searchRoot -Filter *.cs -File -Recurse
$beamFiles = Get-ChildItem -LiteralPath $searchRoot -Filter "CombatBeamSolver*.cs" -File
$beamPaths = @($beamFiles.FullName)
$cyclePolicyPaths = @(
    (Join-Path $searchRoot "CombatBeamSolver.CyclePlanning.cs"),
    (Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.Cycle.cs"),
    (Join-Path $searchRoot "CombatBeamSolver.CycleRegionRetention.cs"),
    (Join-Path $searchRoot "CombatBeamSolver.OrderedMutationRetention.cs")
)
$legacyLoopGuardPaths = @(
    (Join-Path $searchRoot "CombatBeamSolver.Expansion.cs"),
    (Join-Path $searchRoot "CombatBeamSolver.ParallelExpansion.cs"),
    (Join-Path $searchRoot "SolverWeights.cs")
)
foreach ($file in $searchFiles) {
    foreach ($reference in $forbiddenSearchReferences) {
        foreach ($match in Select-String -LiteralPath $file.FullName -SimpleMatch $reference) {
            $violations.Add("$($file.FullName):$($match.LineNumber): forbidden Search reference '$reference'")
        }
    }
}

$hookCompatibilityPath = Join-Path $repositoryRoot "src/Compatibility/Sts2HookCompatibility.cs"
$afterBlockBrokenMirrorPath = Join-Path $repositoryRoot "src/Engine/InCombat/Mirrors/Hooks/Block/AfterBlockBrokenMirrors.cs"
foreach ($compatibilityBoundary in @(
    @{ Path = $hookCompatibilityPath; Text = "internal static class Sts2HookCompatibility" },
    @{ Path = $hookCompatibilityPath; Text = "AfterBlockBrokenParameterTypes" },
    @{ Path = $afterBlockBrokenMirrorPath; Text = "Sts2HookCompatibility.AfterBlockBrokenParameterTypes" })) {
    if (-not (Select-String -LiteralPath $compatibilityBoundary.Path -SimpleMatch $compatibilityBoundary.Text -Quiet)) {
        $violations.Add("$($compatibilityBoundary.Path): missing compatibility hook boundary '$($compatibilityBoundary.Text)'")
    }
}
if (Select-String -LiteralPath $afterBlockBrokenMirrorPath -SimpleMatch "#if STS2_01071" -Quiet) {
    $violations.Add("${afterBlockBrokenMirrorPath}: native AfterBlockBroken parameter shape returned outside Compatibility")
}

$turnSetupCompatibilityPath = Join-Path $repositoryRoot "src/Compatibility/Sts2TurnSetupCompatibility.cs"
$turnSetupPatchPath = Join-Path $repositoryRoot "src/Runtime/PlayerTurnSetupPatches.cs"
foreach ($turnSetupBoundary in @(
    @{ Path = $turnSetupCompatibilityPath; Text = "SetupPlayerTurnParameterTypes" },
    @{ Path = $turnSetupCompatibilityPath; Text = "RunAutoPrePlayPhaseParameterTypes" },
    @{ Path = $turnSetupPatchPath; Text = "Sts2TurnSetupCompatibility.SetupPlayerTurnParameterTypes" },
    @{ Path = $turnSetupPatchPath; Text = "Sts2TurnSetupCompatibility.RunAutoPrePlayPhaseParameterTypes" })) {
    if (-not (Select-String -LiteralPath $turnSetupBoundary.Path -SimpleMatch $turnSetupBoundary.Text -Quiet)) {
        $violations.Add("$($turnSetupBoundary.Path): missing turn-setup compatibility boundary '$($turnSetupBoundary.Text)'")
    }
}
if (Select-String -LiteralPath $turnSetupPatchPath -SimpleMatch "CombatTurnStateType" -Quiet) {
    $violations.Add("${turnSetupPatchPath}: native turn-state type returned outside Compatibility")
}

$patchRegistrationPath = Join-Path $repositoryRoot "src/Runtime/PatchRegistration.cs"
$entryPath = Join-Path $repositoryRoot "src/Runtime/Entry.cs"
foreach ($patchRegistrationBoundary in @(
    @{ Path = $patchRegistrationPath; Text = "internal static class PatchRegistration" },
    @{ Path = $patchRegistrationPath; Text = "ApplyRequiredPatches" },
    @{ Path = $patchRegistrationPath; Text = "patcher.RegisterPatch<PlayerTurnSetupPatch>();" },
    @{ Path = $patchRegistrationPath; Text = "RitsuLibFramework.ApplyRequiredPatcher(patcher, onFailure);" },
    @{ Path = $entryPath; Text = "PatchRegistration.ApplyRequiredPatches(ModId, DisableMod);" })) {
    if (-not (Select-String -LiteralPath $patchRegistrationBoundary.Path -SimpleMatch $patchRegistrationBoundary.Text -Quiet)) {
        $violations.Add("$($patchRegistrationBoundary.Path): missing patch-registration boundary '$($patchRegistrationBoundary.Text)'")
    }
}
foreach ($retiredPatchRegistrationCall in @(
    "RitsuLibFramework.CreatePatcher(",
    "patcher.RegisterPatch<",
    "RitsuLibFramework.ApplyRequiredPatcher(")) {
    if (Select-String -LiteralPath $entryPath -SimpleMatch $retiredPatchRegistrationCall -Quiet) {
        $violations.Add("${entryPath}: patch registration returned to Entry '$retiredPatchRegistrationCall'")
    }
}

$blockPotionInsertionPath = Join-Path $searchRoot "CombatBeamSolver.BlockPotionInsertion.cs"
foreach ($requiredBlockPotionRule in @(
    'HpLostByTurn',
    'SolverWeights.PotionMinimumHpSaved',
    'ReplayInsertedRoute(',
    'ProjectedDeathSaveUseCount',
    'expanded_nodes_added=0')) {
    if (-not (Select-String -LiteralPath $blockPotionInsertionPath -SimpleMatch $requiredBlockPotionRule -Quiet)) {
        $violations.Add("${blockPotionInsertionPath}: deterministic block-potion route rule is missing '$requiredBlockPotionRule'")
    }
}
$searchCoordinatorPath = Join-Path $searchRoot "CombatSearchCoordinator.cs"
if (-not (Select-String -LiteralPath $searchCoordinatorPath -SimpleMatch 'passResult.DeterministicBlockPotionInserted' -Quiet)) {
    $violations.Add("${searchCoordinatorPath}: deterministic block-potion result must settle before supplemental potion audits")
}

# Cycle planning must infer recurrence and payoff from generic simulated-state deltas. Keeping
# scenario names out of this policy file prevents a regression to card/power/relic/enemy allowlists.
$scenarioSpecificCycleModelPattern = '\b(?:Body[\s_.-]*Slam|Lunar[\s_.-]*Blast|Gold[\s_.-]*Axe|Slow[\s_.-]*Power|Hellraiser|Pillage|Bloodletting|Particle[\s_.-]*Wall|Pale[\s_.-]*Blue[\s_.-]*Dot|Flash[\s_.-]*Of[\s_.-]*Steel|Finesse|Speedster|Black[\s_.-]*Hole|Glow|Alignment|Spoils[\s_.-]*Of[\s_.-]*Battle)\b'
foreach ($cyclePolicyPath in $cyclePolicyPaths) {
    foreach ($match in Select-String -LiteralPath $cyclePolicyPath -Pattern $scenarioSpecificCycleModelPattern) {
        $violations.Add("$($match.Path):$($match.LineNumber): generic cycle planning contains a scenario-specific model name or ID")
    }
    foreach ($directModelLookupPattern in @(
        '\bModelDb\.(?:Card|Power|Relic|Monster)\b',
        '\bGetAmount<[A-Za-z_][A-Za-z0-9_]*(?:Power|Relic|Monster)>',
        '\btypeof\([A-Za-z_][A-Za-z0-9_]*(?:Card|Power|Relic|Monster)\)')) {
        foreach ($match in Select-String -LiteralPath $cyclePolicyPath -Pattern $directModelLookupPattern) {
            $violations.Add("$($match.Path):$($match.LineNumber): generic cycle planning performs a direct concrete-model lookup")
        }
    }
}

$cycleRegionRetentionPath = Join-Path $searchRoot "CombatBeamSolver.CycleRegionRetention.cs"
foreach ($cycleTransactionRule in @(
    'CycleRegionRetentionTransaction',
    'CloneCycleRegionLedger(',
    'ObservationBaseline',
    'FindBestCycleRegionProgressWitness(',
    'lanePriority: -1',
    'SelectCycleRegionAdmissionKind(',
    'normalAdmissionSucceeded',
    'HasActiveOrderedMutationCycleRegionAdmission(',
    'node.CycleExitRetentionRank != int.MaxValue')) {
    if (-not (Select-String -LiteralPath $cycleRegionRetentionPath -SimpleMatch $cycleTransactionRule -Quiet)) {
        $violations.Add("${cycleRegionRetentionPath}: cycle-region final-survivor transaction invariant is missing '$cycleTransactionRule'")
    }
}
foreach ($retiredCycleOrderedCoupling in @(
    'CycleRegionOrderedProgressTail',
    'OrderCycleRegionOrderedMutationLane(',
    'TryStageCycleRegionOrderedProgressTailAdmission(')) {
    foreach ($match in Select-String -LiteralPath $cycleRegionRetentionPath -SimpleMatch $retiredCycleOrderedCoupling) {
        $violations.Add("$($match.Path):$($match.LineNumber): retired cycle-region/ordered joint ledger returned '$retiredCycleOrderedCoupling'")
    }
}
if (-not (Select-String -LiteralPath (Join-Path $searchRoot "CombatBeamSolver.Retention.cs") -SimpleMatch 'FinalizeCycleRegionRetention(cycleRegionTransaction, finalized);' -Quiet)) {
    $violations.Add("${searchRoot}/CombatBeamSolver.Retention.cs: cycle-region provisional admissions are no longer reconciled after final arbitration")
}
$orderedRetentionPath = Join-Path $searchRoot "CombatBeamSolver.OrderedMutationRetention.cs"
foreach ($orderedTransactionRule in @(
    'MaximumOrderedMutationRunAdmissions = 2048',
    'HasFullyPendingAtomicOrderedMutationPair(',
    'ExpireOrderedMutationSchedulingLeaseForOrdinaryFallback(node);',
    'PendingOrderedMutationOrdinaryFallbackNodes',
    'ValidateOrderedMutationAdmissionLedger(',
    'typeof(OrderedMutationRetentionLease).IsValueType')) {
    if (-not (Select-String -LiteralPath $orderedRetentionPath -SimpleMatch $orderedTransactionRule -Quiet)) {
        $violations.Add("${orderedRetentionPath}: ordered-mutation atomic accounting invariant is missing '$orderedTransactionRule'")
    }
}
$orderedCoordinatorPaths = @{
    'BuildOrderedMutationContinuationAdmissionLease(candidate);' = Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs"
    'Every independent retention channel must finish before the ordered coordinator.' = Join-Path $searchRoot "CombatBeamSolver.Retention.cs"
    'Any inherited lane left outside this prune' = Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs"
    'HasOrdinaryAnchor' = Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs"
}
foreach ($entry in $orderedCoordinatorPaths.GetEnumerator()) {
    if (-not (Select-String -LiteralPath $entry.Value -SimpleMatch $entry.Key -Quiet)) {
        $violations.Add("$($entry.Value): unified ordered-mutation coordinator invariant is missing '$($entry.Key)'")
    }
}
$mutationPolicyPath = Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs"
foreach ($requiredMutationMember in @(
    'ArmOrderedMutationObservationBridges(',
    'BuildOrderedMutationContinuationAdmissionLease(',
    'VerifyOrderedMutationKeyPolicyForTesting(')) {
    if (-not (Select-String -LiteralPath $mutationPolicyPath -SimpleMatch $requiredMutationMember -Quiet)) {
        $violations.Add("${mutationPolicyPath}: Mutation partial is missing '$requiredMutationMember'")
    }
}
foreach ($retiredMutationMember in @(
    'AddOrderedMutationPortfolio(',
    'ArmOrderedMutationObservationBridges(',
    'BuildOrderedMutationContinuationAdmissionLease(',
    'VerifyOrderedMutationKeyPolicyForTesting(')) {
    if (Select-String -LiteralPath (Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs") -SimpleMatch $retiredMutationMember -Quiet) {
        $violations.Add("${searchRoot}/CombatBeamSolver.BeamRetentionPolicy.cs: Mutation member returned to facade '$retiredMutationMember'")
    }
}
$solverDiagnosticsPath = Join-Path $repositoryRoot "src\Runtime\SolverDiagnostics.cs"
foreach ($orderedMetric in @(
    'ordered_admitted=',
    'ordered_lease_expired_budget=',
    'ordered_ordinary_fallback=',
    'cold_atomic_committed=',
    'cold_atomic_rejected=')) {
    if (-not (Select-String -LiteralPath $solverDiagnosticsPath -SimpleMatch $orderedMetric -Quiet)) {
        $violations.Add("${solverDiagnosticsPath}: ordered-mutation acceptance metric is missing '$orderedMetric'")
    }
}
$retentionPath = Join-Path $searchRoot "CombatBeamSolver.Retention.cs"
$openingChannelMatch = Select-String -LiteralPath $retentionPath -SimpleMatch 'List<List<SearchNode>> openingChannels = pool' | Select-Object -First 1
$orderedCoordinatorMatch = Select-String -LiteralPath $retentionPath -SimpleMatch 'Retention.AddOrderedMutationPortfolio(pool, selected, selectedSet);' | Select-Object -First 1
$cycleRegionMatch = Select-String -LiteralPath $retentionPath -SimpleMatch 'cycleRegionTransaction = ApplyCycleRegionRetention(' | Select-Object -First 1
if ($null -eq $openingChannelMatch `
    -or $null -eq $orderedCoordinatorMatch `
    -or $null -eq $cycleRegionMatch `
    -or $openingChannelMatch.LineNumber -ge $orderedCoordinatorMatch.LineNumber `
    -or $orderedCoordinatorMatch.LineNumber -ge $cycleRegionMatch.LineNumber) {
    $violations.Add("${retentionPath}: opening/independent channels must settle before ordered admission, which must settle before CycleRegion")
}
foreach ($match in Select-String -LiteralPath $cycleRegionRetentionPath -SimpleMatch 'selectedSet.Add(node);') {
    $violations.Add("$($match.Path):$($match.LineNumber): CycleRegion rebuilt an O(pool) selected-set shadow")
}

# PR #28's fixed repeat count and named payoff exceptions are retired. These checks intentionally
# stay scoped to expansion and policy files so unrelated combat-semantic mirrors remain legal.
foreach ($legacyLoopGuardPath in $legacyLoopGuardPaths) {
    foreach ($retiredLoopGuard in @(
        'MaxRepeatableNoProgressPlays',
        'IsRepeatableNoProgressStep',
        'ShouldPruneRepeatableNoProgress',
        'RepeatableNoProgressCardId',
        'RepeatableNoProgressCount')) {
        foreach ($match in Select-String -LiteralPath $legacyLoopGuardPath -SimpleMatch $retiredLoopGuard) {
            $violations.Add("$($match.Path):$($match.LineNumber): retired fixed repeatable-no-progress guard '$retiredLoopGuard' returned")
        }
    }
    foreach ($match in Select-String -LiteralPath $legacyLoopGuardPath -Pattern '\b(?:Body[\s_.-]*Slam|Lunar[\s_.-]*Blast|Gold[\s_.-]*Axe|Slow[\s_.-]*Power)\b') {
        $violations.Add("$($match.Path):$($match.LineNumber): retired named loop-payoff exception returned")
    }
}

$semanticFiles = @($beamPaths) + @(
    (Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionDynamicVarExtensions.cs")
)
foreach ($file in $semanticFiles) {
    foreach ($match in Select-String -LiteralPath $file -Pattern 'catch\s*\(Exception') {
        $violations.Add("${file}:$($match.LineNumber): broad semantic catch is not allowed")
    }
}

$removedFallbacks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionDynamicVarExtensions.cs"
        Text = "return 0m;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Cards\OnPlay\CardOnPlayInferrer.cs"
        Text = "Inferred card mirror failed"
    },
    @{
        Path = $beamPaths
        Text = "跳过无法回放"
    }
)
foreach ($fallback in $removedFallbacks) {
    foreach ($match in Select-String -LiteralPath $fallback.Path -SimpleMatch $fallback.Text) {
        $violations.Add("$($fallback.Path):$($match.LineNumber): removed fallback '$($fallback.Text)' returned")
    }
}

$controllerPath = Join-Path $repositoryRoot "src\Runtime\SolverController.cs"
$removedControllerFields = @(
    "_searchCancellation",
    "_deploymentCancellation",
    "_generation",
    "_searching",
    "_deployAfterSearch",
    "_searchStamp",
    "_searchProgress",
    "_renderedProgress",
    "_lastProgressRenderAt",
    "_searchFrameCount",
    "_searchFramesOver33Ms",
    "_searchFramesOver50Ms",
    "_searchFramesOver100Ms",
    "_maxSearchFrameGapMs"
)
foreach ($field in $removedControllerFields) {
    foreach ($match in Select-String -LiteralPath $controllerPath -SimpleMatch $field) {
        $violations.Add("${controllerPath}:$($match.LineNumber): retired controller field '$field' returned")
    }
}

$onPlayFacade = Join-Path $repositoryRoot "src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs"
if (Select-String -LiteralPath $onPlayFacade -SimpleMatch "Harmony.GetPatchInfo" -Quiet) {
    $violations.Add("${onPlayFacade}: worker must not query Harmony")
}
$onPlayAdapter = Join-Path $repositoryRoot 'src/Prediction/AdaptedCardOnPlayMirrors.cs'
if (Select-String -LiteralPath $onPlayAdapter -SimpleMatch 'PredictionModPatchAudit.AuditCardOnPlay(' -Quiet) {
    $violations.Add("${onPlayAdapter}: generated cards must use frozen root patch evidence")
}
if (-not (Select-String -LiteralPath $onPlayAdapter -SimpleMatch 'patchedOnPlayTargets.Contains(target)' -Quiet)) {
    $violations.Add("${onPlayAdapter}: missing frozen generated-card patch decision")
}

$sessionPath = Join-Path $repositoryRoot "src\Runtime\SolverControllerSessions.cs"
foreach ($sessionType in @("SolverCombatSession", "SolverSearchSession", "SolverDeploymentSession")) {
    if (-not (Select-String -LiteralPath $sessionPath -SimpleMatch "class $sessionType" -Quiet)) {
        $violations.Add("${sessionPath}: missing controller session type '$sessionType'")
    }
}

$forkBoundaryChecks = @(
    @{
        Path = Join-Path $repositoryRoot "src/Prediction/PredictionModHookSubscriberCapture.cs"
        Text = "PredictionModPatchAudit.CaptureCardOnPlay"
    },
    @{
        Path = Join-Path $repositoryRoot "src/Search/SimulatedCombatState.cs"
        Text = "_modHookSubscribers = source._modHookSubscribers;"
    },
    @{
        Path = Join-Path $repositoryRoot "src/Runtime/ContinuationStamp.cs"
        Text = "AdaptedCardOnPlayMirrors.CaptureLiveStamp()"
    },
    @{
        Path = Join-Path $repositoryRoot "src/Runtime/ContinuationStamp.cs"
        Text = "adaptedOnPlay.Stamp"
    },
    @{
        Path = Join-Path $repositoryRoot "src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs"
        Text = "return replacement;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\ModelPredictionStateMirrors.cs"
        Text = "context.Register(value, typed)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\ModelPredictionStateMirrors.cs"
        Text = "boundary.AssertForkable()"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "ModelPredictionStateMirrors.CaptureRootState(simulator,"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "ModelPredictionStateMirrors.AppendPredicted(ref fingerprint,"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "_rootModifierSources = null;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\ContinuationStamp.cs"
        Text = "ModelPredictionStateMirrors.AppendLiveContinuation(text, state, capturedPlayers)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\ContinuationStamp.cs"
        Text = "ModelPredictionStateMirrors.AppendPredicted(ref adapterFingerprint,"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\Common\PredictionForking.cs"
        Text = "interface IPredictionForkBoundary"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\Common\PredictionStateStore.cs"
        Text = "boundary.AssertForkable()"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Fork.cs"
        Text = "_activeActionChoices"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Fork.cs"
        Text = "_activeCardExecutionDeaths"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\CardPlayHookPredictionStates.cs"
        Text = "Cannot fork Pen Nib"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardPlayedMirrors.cs"
        Text = "Cannot fork Curl Up"
    }
)
foreach ($check in $forkBoundaryChecks) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing Fork boundary '$($check.Text)'")
    }
}

$searchGcPolicyPath = Join-Path $repositoryRoot "src\Runtime\SearchGcPolicy.cs"
$searchGcRecoveryPath = Join-Path $repositoryRoot "src\Runtime\SearchGcPolicy.Recovery.cs"
foreach ($forbiddenRecoveryCall in @("GC.Collect(", "CollectGeneration2")) {
    if (Select-String -LiteralPath $searchGcRecoveryPath -SimpleMatch $forbiddenRecoveryCall -Quiet) {
        $violations.Add("${searchGcRecoveryPath}: NoGC recovery must not induce a collection or enter the reclaim chain '$forbiddenRecoveryCall'")
    }
}
foreach ($gcChainRule in @(
    "return WaitForReclaimChainAsync(_reclaimTask)",
    "CollectGeneration2ForAutomaticReclaimAsync(inSearchCheckpoint: true)",
    "_inSearchManualReclaimTask = manualCompletion.Task",
    "failure == null && (_regionExitRequired || _reclaimRequired)")) {
    if (-not (Select-String -LiteralPath $searchGcPolicyPath -SimpleMatch $gcChainRule -Quiet)) {
        $violations.Add("${searchGcPolicyPath}: missing serialized reclaim-chain rule '$gcChainRule'")
    }
}
if (Select-String -LiteralPath $searchGcPolicyPath -SimpleMatch "ReclaimAfterActiveCheckpointAsync" -Quiet) {
    $violations.Add("${searchGcPolicyPath}: recursive reclaim handoff returned")
}

# GC admission accounting and scratch-container ownership remain in their existing layers.
foreach ($check in @(
    @{ RelativePath = "src/Runtime/SearchGcPolicy.cs"; Text = "scope.CompleteLifecycle(CaptureLifecycle())" },
    @{ RelativePath = "src/Runtime/SolverController.SearchLifecycle.cs"; Text = "SearchGcPolicy.EnterSearchScope(" },
    @{ RelativePath = "src/Search/CombatBeamSolver.Models.cs"; Text = "ExpansionBatchPool = new(static snapshot => snapshot.ReleaseSimulator())" },
    @{ RelativePath = "src/Search/CombatBeamSolver.ParallelExpansion.cs"; Text = "new(_run.ExpansionBatchPool)" },
    @{ RelativePath = "src/Engine/InCombat/Simulation/CombatPredictionRngSet.cs"; Text = "private sealed class FrozenStream(PredictionRngState state)" },
    @{ RelativePath = "src/Engine/InCombat/Simulation/CombatPredictionRngSet.cs"; Text = "new FrozenStream(_mutable.CaptureState())" },
    @{ RelativePath = "src/Testing/UnattendedTestRunner.Assertions.cs"; Text = "AssertLazyRngFork(scenario.CombatState.RunState.Rng)" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.ShuffleState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.ShuffleState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatCardGenerationState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatCardGenerationState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatPotionGenerationState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatPotionGenerationState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatCardSelectionState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatCardSelectionState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatEnergyCostsState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatEnergyCostsState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatTargetsState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatTargetsState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatOrbGenerationState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatOrbGenerationState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.MonsterAiState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.MonsterAiState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.NicheState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.NicheState" },
    @{ RelativePath = "src/Engine/Common/PredictedCard.cs"; Text = "internal bool TryMarkPowerAfflictionEntryChecked()" },
    @{ RelativePath = "src/Engine/Common/PredictedCard.cs"; Text = "HasCheckedPowerAfflictionEntry = HasCheckedPowerAfflictionEntry," },
    @{ RelativePath = "src/Search/SimulatedCombatState.PowerLifecycle.cs"; Text = "private HashSet<CardModel>? _liveCardsAtSnapshot;" },
    @{ RelativePath = "src/Search/SimulatedCombatState.Fork.cs"; Text = "_liveCardsAtSnapshot = _liveCardsAtSnapshot," },
    @{ RelativePath = "src/Search/SimulatedCombatState.Fork.cs"; Text = "ReferenceEquals(view.Prefix, _rootRunHookListeners)" },
    @{ RelativePath = "src/Testing/UnattendedTestRunner.Assertions.cs"; Text = "AssertFrozenRootRunListeners(scenario.CombatState, scenario.Player);" },
    @{ RelativePath = "src/Search/CombatBeamSolver.Models.cs"; Text = "SnapshotListBuffer<PredictedCard> SnapshotLiveCards = new()" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "_run.SnapshotLiveCards.Rent()" },
    @{ RelativePath = "src/Search/CombatBeamSolver.Phases.cs"; Text = "SearchWaveMemoryPolicy.ParentWaveCapacity(" })) {
    $checkPath = Join-Path $repositoryRoot $check.RelativePath
    if (-not (Select-String -LiteralPath $checkPath -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${checkPath}: missing GC research ownership boundary '$($check.Text)'")
    }
}

$cardPlayPredictionStatePath = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\CardPlayHookPredictionStates.cs"
foreach ($stableVambraceState in @(
    "internal sealed class VambracePredictionState(Vambrace relic) : IPredictionStateForkable",
    "public CardModel? TriggeringCard { get; set; } = relic._triggeringCard;",
    "public bool BlockGainedThisCombat { get; set; } = relic._blockGainedThisCombat;")) {
    if (-not (Select-String -LiteralPath $cardPlayPredictionStatePath -SimpleMatch $stableVambraceState -Quiet)) {
        $violations.Add("${cardPlayPredictionStatePath}: missing stable Vambrace state '$stableVambraceState'")
    }
}

$rootSnapshotChecks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\CombatRootSnapshot.cs"
        Text = "Combat root snapshot must be captured on the main thread."
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\SolverController.SearchLifecycle.cs"
        Text = "CombatRootSnapshot.Capture(state)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\PlayerTurnSetupPatches.cs"
        Text = "CombatRootSnapshot.Capture(combat)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\CombatSearchCoordinator.cs"
        Text = "CombatRootSnapshot root"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\RootCombatHistorySnapshot.cs"
        Text = "history.CardPlaysStarted.ToArray()"
    }
)

$preCombatApiChecks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastApi.cs"
        Text = "public static class PreCombatForecastApi"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\CombatShowcaseApi.cs"
        Text = "public static class CombatShowcaseApi"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Replay\CombatShowcaseRuntime.cs"
        Text = "SolverController.AcceptShowcaseRoute"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Replay\CombatShowcaseCollector.cs"
        Text = "CombatShowcaseCollector.FlushPendingAsync"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Replay\CombatShowcaseModEligibility.cs"
        Text = "FindGameplayModificationNames"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatLiveStateSnapshot.cs"
        Text = "RunManager.Instance.ToSave(null)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatRunSerialization.cs"
        Text = 'point["can_modify"] = false'
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatRunSerialization.cs"
        Text = 'eventChoice["variables"] is JsonObject { Count: 0 }'
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastWorker.cs"
        Text = "COMBATSOLVER_PRECOMBAT_WORKER"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastWorker.cs"
        Text = "ExpectedLoadedMods = expectedMods"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastWorker.cs"
        Text = "EnableNoGcRegionForTest = false"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastWorker.cs"
        Text = "PreCombatInterveningMapPoints = options.InterveningMapPoints"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ScenarioBuilder.cs"
        Text = "EnterMapCoordDebug"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ScenarioBuilder.cs"
        Text = "PreCombatPlayerHp:"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ScenarioBuilder.cs"
        Text = "DirectRunSnapshot:ExactStateRestored"
    }
)
foreach ($check in $preCombatApiChecks) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing pre-combat isolation boundary '$($check.Text)'")
    }
}
foreach ($apiFile in Get-ChildItem (Join-Path $repositoryRoot "src\Api") -Filter "*.cs" -File) {
    foreach ($forbiddenCall in @(
        "SolverController.RequestSearch",
        "CombatManager.Instance.SetUpCombat",
        "RunManager.Instance.EnterRoomDebug")) {
        foreach ($match in Select-String -LiteralPath $apiFile.FullName -SimpleMatch $forbiddenCall) {
            $violations.Add("$($apiFile.FullName):$($match.LineNumber): pre-combat API directly mutates live combat via '$forbiddenCall'")
        }
    }
}

$nativeChoiceRuntimePath = Join-Path $repositoryRoot "src\Runtime\NativeChoiceRuntime.cs"
$turnSetupPath = Join-Path $repositoryRoot "src\Runtime\PlayerTurnSetupPatches.cs"
foreach ($check in @(
    @{ Path = $nativeChoiceRuntimePath; Text = "internal static class NativeChoiceRuntime" },
    @{ Path = $nativeChoiceRuntimePath; Text = "NativeChoiceSurfaceKind.Hand" },
    @{ Path = $nativeChoiceRuntimePath; Text = "NativeChoiceSurfaceKind.SimpleGrid" },
    @{ Path = $nativeChoiceRuntimePath; Text = "NativeChoiceSurfaceKind.CombatPile" },
    @{ Path = $nativeChoiceRuntimePath; Text = "NativeChoiceSurfaceKind.ChooseCard" },
    @{ Path = $turnSetupPath; Text = "TryGetPlannedTurnSetupChoices" },
    @{ Path = $turnSetupPath; Text = "source=continuation choices=" },
    @{ Path = $controllerPath; Text = "ResumeAfterTurnSetupAsync" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing native choice boundary '$($check.Text)'")
    }
}
foreach ($runtimePath in Get-ChildItem (Join-Path $repositoryRoot "src\Runtime") -Filter "*.cs" -File) {
    if ($runtimePath.FullName -eq $nativeChoiceRuntimePath) {
        continue
    }
    foreach ($match in Select-String -LiteralPath $runtimePath.FullName -SimpleMatch "CardSelectCmd.PushSelector") {
        $violations.Add("$($runtimePath.FullName):$($match.LineNumber): production runtime bypasses native choice UI")
    }
}
$cardTargetingPath = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.CardTargeting.cs"
foreach ($targetingRule in @(
    "Shiv => combat.GetAmount<FanOfKnivesPower>",
    "SovereignBlade => combat.GetAmount<SeekingEdgePower>")) {
    if (-not (Select-String -LiteralPath $cardTargetingPath -SimpleMatch $targetingRule -Quiet)) {
        $violations.Add("${cardTargetingPath}: missing simulated card targeting rule '$targetingRule'")
    }
}
foreach ($check in $rootSnapshotChecks) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing root snapshot boundary '$($check.Text)'")
    }
}

$expectedBeamFiles = @(
    "CombatBeamSolver.cs",
    "CombatBeamSolver.AdmittedExpansion.cs",
    "CombatBeamSolver.EndTurnChoiceReplay.cs",
    "CombatBeamSolver.EndTurnExpansion.cs",
    "CombatBeamSolver.RoundTransition.cs",
    "CombatBeamSolver.CardChoiceContinuation.cs",
    "CombatBeamSolver.PotionChoiceContinuation.cs",
    "CombatBeamSolver.ExecutionChoiceContinuation.cs",
    "CombatBeamSolver.ExecutionChoiceContinuation.Testing.cs",
    "CombatBeamSolver.TurnExecutionContinuation.cs",
    "CombatBeamSolver.BeamRetentionPolicy.cs",
    "CombatBeamSolver.BeamRetentionPolicy.Choice.cs",
    "CombatBeamSolver.BeamRetentionPolicy.Potion.cs",
    "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs",
    "CombatBeamSolver.BeamRetentionPolicy.CrossTurn.cs",
    "CombatBeamSolver.BeamRetentionPolicy.Cycle.cs",
    "CombatBeamSolver.BeamRanking.cs",
    "CombatBeamSolver.BlockPotionInsertion.cs",
    "CombatBeamSolver.CrossTurnPlanning.cs",
    "CombatBeamSolver.CyclePlanning.cs",
    "CombatBeamSolver.CycleRegionRetention.cs",
    "CombatBeamSolver.Expansion.cs",
    "CombatBeamSolver.FinalPlanOrdering.cs",
    "CombatBeamSolver.Models.cs",
    "CombatBeamSolver.MultiplayerInterleaving.cs",
    "CombatBeamSolver.MultiplayerScenarioReevaluation.cs",
    "CombatBeamSolver.NoveltySearch.cs",
    "CombatBeamSolver.OpeningExpansion.cs",
    "CombatBeamSolver.Transpositions.cs",
    "CombatBeamSolver.OrderedMutationRetention.cs",
    "CombatBeamSolver.ParallelExpansion.cs",
    "CombatBeamSolver.PathDiagnostics.cs",
    "CombatBeamSolver.Phases.cs",
    "CombatBeamSolver.PrimaryChoiceReplay.cs",
    "CombatBeamSolver.Retention.cs",
    "CombatBeamSolver.RetentionJobs.cs",
    "CombatBeamSolver.StateEvaluation.cs",
    "CombatBeamSolver.StandPatJobs.cs",
    "CombatBeamSolver.Terminal.cs"
)
$u5InterleavePath = Join-Path $searchRoot "CombatBeamSolver.MultiplayerInterleaving.cs"
$u5InterleaveText = [IO.File]::ReadAllText($u5InterleavePath)
foreach ($u5InterleaveRule in @(
    'MaximumForecastObservationsPerTurn',
    'BuildTeamSingleActionRoutes(',
    'ProbeReverseInterleaveOrder(',
    'ShadowFutureStateFingerprint.Capture(',
    'MultiplayerInterleaveOrderPolicy.CanCollapseOrder(orderRelation)',
    'deployable=false proactive_wait=false')) {
    if (-not $u5InterleaveText.Contains($u5InterleaveRule)) {
        $violations.Add("${u5InterleavePath}: U5 local/teammate interleave boundary drifted '$u5InterleaveRule'")
    }
}
foreach ($u5ExpansionRule in @(
    'StateFingerprint transpositionKey = ExactTranspositionKey(candidate);',
    '_run.Transpositions.TryGetValue(transpositionKey, out TranspositionFrontier? frontier)',
    'StateFingerprint transpositionKey = ExactTranspositionKey(node);',
    '_run.ExpandedTranspositions.TryGetValue(transpositionKey, out TranspositionFrontier? frontier)',
    'BuildAcceptedInlineTeammateForecastNodes(node)')) {
    if (-not $crossTurnTranspositionText.Contains($u5ExpansionRule)) {
        $violations.Add("${crossTurnTranspositionPath}: U5 exact interleave transposition boundary drifted '$u5ExpansionRule'")
    }
}

$pathDiagnosticsPath = Join-Path $searchRoot "CombatBeamSolver.PathDiagnostics.cs"
foreach ($required in @(
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"); Text = 'HasRetainedRoutingChoice: RetainedRoutingChoice(node) != null' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.BeamRanking.cs"); Text = 'if (values.HasRetainedRoutingChoice)' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.SearchPolicy.cs"); Text = 'seven, [], [0, 7, 1, 4, 2, 5, 6], useTacticalOrder: true);' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-SOUL-GENERATION-CONTEXT-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-SOUL-GENERATION-SUFFIX-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-SOUL-VARIANT-PATH-TRACE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-SOUL-RETAINED-PATH-TRACE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownSoulVariantPathTrace.cs"); Text = 'requiredRetentionStep: 18, proveRetentionAliases: true' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownSoulVariantPathTrace.cs"); Text = 'RunKnownSoulGenerationContext(combat, player, fullKnownSuffix: true, frozenVariants: variants);' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownRoutePathTrace.cs"); Text = 'watched.UnionWith(variants.Values.SelectMany(variant => variant.Prefixes)' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownRoutePathTrace.cs"); Text = 'exact.GroupBy(item => new { item.PolicyLabel, item.ParentPolicyLabel })' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-EXOSKELETONS-ROUTE-REPLAY-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-EXOSKELETONS-PATH-TRACE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-EXOSKELETONS-CONTINUATION-PATH-TRACE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-EXOSKELETONS-ROUTE-NATIVE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownRoutePathTrace.cs"); Text = 'CaptureKnownRouteRootStates(root, player, enemies)' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownExoskeletonsPathTrace.cs"); Text = 'RunKnownExoskeletonsRouteReplay(combat, player, freeze: frozen);' },
    @{ Path = $pathDiagnosticsPath; Text = 'observer.WantsState(node.StateKey)' },
    @{ Path = $pathDiagnosticsPath; Text = 'observer.WantsRetentionPool(node.StateKey)' },
    @{ Path = $pathDiagnosticsPath; Text = 'SearchPathObservationStage.RetentionPoolInput' },
    @{ Path = $pathDiagnosticsPath; Text = 'Evaluation: new SearchPathEvaluationValues(' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.Retention.cs"); Text = 'SearchPathObservationStage.RetentionPoolFinal' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.Retention.cs"); Text = 'Retention.AddCrossTurnPortfolio(pool, selected, selectedSet);' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.Retention.cs"); Text = 'Retention.AddCyclePortfolio(pool, selected, selectedSet);' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.Retention.cs"); Text = 'Retention.AddCycleExitPortfolio(pool, selected, selectedSet);' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.Retention.cs"); Text = 'BeamRetentionPolicy.RequiresCrossTurnPlanning(candidate)' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"); Text = 'observedOptionLeaders.Add(optionLeader)' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.Retention.cs"); Text = 'SearchPathObservationStage.PruneFinal' },
    @{ Path = (Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardPlayedMirrors.cs"); Text = 'private static bool ApplyRelicStatPower(' },
    @{ Path = (Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardPlayedMirrors.cs"); Text = 'if (context.Simulator.IsEnding)' })) {
    if (-not (Select-String -LiteralPath $required.Path -SimpleMatch $required.Text -Quiet)) {
        $violations.Add("$($required.Path): path observation or relic command boundary is missing '$($required.Text)'")
    }
}
foreach ($forbidden in @(
    @{ Path = $pathDiagnosticsPath; Text = 'node.Actions;' },
    @{ Path = (Join-Path $searchRoot "SimulatedCombatState.Relics.cs"); Text = 'case Kunai' },
    @{ Path = (Join-Path $searchRoot "SimulatedCombatState.Relics.cs"); Text = 'case Shuriken' },
    @{ Path = (Join-Path $searchRoot "SimulatedCombatState.Relics.cs"); Text = 'Apply<DexterityPower>' })) {
    foreach ($match in Select-String -LiteralPath $forbidden.Path -SimpleMatch $forbidden.Text) {
        $violations.Add("$($match.Path):$($match.LineNumber): observer cache mutation or deferred relic stat application returned '$($forbidden.Text)'")
    }
}
foreach ($retiredCrossTurnMember in @(
    'private void AddCrossTurnPortfolio(',
    'private static CrossTurnProbeFamilyKey',
    'private void StartCrossTurnProbe(',
    'private bool RequiresCrossTurnPlanning(')) {
    foreach ($path in @(
        (Join-Path $searchRoot "CombatBeamSolver.Retention.cs"),
        (Join-Path $searchRoot "CombatBeamSolver.CrossTurnPlanning.cs"))) {
        foreach ($match in Select-String -LiteralPath $path -SimpleMatch $retiredCrossTurnMember) {
            $violations.Add("${path}:$($match.LineNumber): CrossTurn retention member returned outside BeamRetentionPolicy.CrossTurn '$retiredCrossTurnMember'")
        }
    }
}
foreach ($retiredCycleMember in @(
    'private void AddCyclePortfolio(',
    'private void AddCycleExitPortfolio(',
    'private CycleStartupRetentionKey BuildCycleStartupRetentionKey(',
    'private static SearchNode? FindActiveCycleExitCandidate(',
    'private static bool TryLeaseCycleExitCandidate(',
    'private static int CompareCycleExitFamilyCandidates(',
    'private static int CompareCycleExitCandidates(',
    'private static int CompareCycleProbeCandidates(')) {
    foreach ($match in Select-String -LiteralPath (Join-Path $searchRoot "CombatBeamSolver.Retention.cs") -SimpleMatch $retiredCycleMember) {
        $violations.Add("$($match.Path):$($match.LineNumber): Cycle retention member returned outside BeamRetentionPolicy.Cycle '$retiredCycleMember'")
    }
}
$actualBeamFiles = @($beamFiles.Name | Sort-Object)
if (($actualBeamFiles -join "|") -ne (($expectedBeamFiles | Sort-Object) -join "|")) {
    $violations.Add(
        "CombatBeamSolver partial file set differs: actual=$($actualBeamFiles -join ',') " +
        "expected=$(($expectedBeamFiles | Sort-Object) -join ',')")
}
$beamStructureChecks = @(
    @{ File = "GrowthPolicy.cs"; Text = "internal readonly record struct GrowthValues(" },
    @{ File = "SearchPolicySnapshot.cs"; Text = "public GrowthValues GrowthBudgets { get; init; }" },
    @{ File = "SearchPolicySnapshot.cs"; Text = "public bool UseNoveltyPortfolio { get; init; }" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "public NoveltySearchRun? Novelty;" },
    @{ File = "CombatBeamSolver.NoveltySearch.cs"; Text = "private bool RunNoveltyOpen(" },
    @{ File = "CombatBeamSolver.NoveltySearch.cs"; Text = "CaptureNoveltyFacts(SearchNode node)" },
    @{ File = "CombatSearchCoordinator.NoveltyPortfolio.cs"; Text = "NoveltyPortfolioBudget.Remaining(profile," },
    @{ File = "CombatSearchCoordinator.NoveltyPortfolio.cs"; Text = "IsBetterPotionPolicyResult(root, policy, exploration, baseline)" },
    @{ File = "BfwsPackedNovelty.cs"; Text = "Dictionary<BfwsFact, int> _atoms" },
    @{ File = "BfwsPackedNovelty.cs"; Text = "_parentPartition == partition" },
    @{ File = "BfwsBoundedOpen.cs"; Text = "private readonly SortedSet<Entry> _entries" },
    @{ File = "NoveltyPortfolioBudget.cs"; Text = "profile.MaxExpandedNodes - (int)expandedNodes" },
    @{ File = "CombatBeamSolver.cs"; Text = "internal sealed partial class CombatBeamSolver(" },
    @{ File = "CombatBeamSolver.cs"; Text = "private readonly SearchRunContext _run = new(" },
    @{ File = "CombatBeamSolver.cs"; Text = "private BeamRetentionPolicy Retention =>" },
    @{ File = "CombatBeamSolver.cs"; Text = "private FinalPlanOrdering FinalOrdering =>" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "private sealed partial class BeamRetentionPolicy(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Choice.cs"; Text = "private sealed partial class BeamRetentionPolicy" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Choice.cs"; Text = "BuildRootActionLineageSignature(SearchNode node)" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Potion.cs"; Text = "private sealed partial class BeamRetentionPolicy" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Potion.cs"; Text = "FinalPolicyQualificationFacts(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Potion.cs"; Text = "BuildFinalPolicyQualificationFacts(SearchNode node)" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Potion.cs"; Text = "CompareFinalPolicyQualificationSignatures(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Potion.cs"; Text = "ReservePotionQuotaLeaders(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Potion.cs"; Text = "PotionUseLineageKey(SearchNode node)" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Potion.cs"; Text = "private static bool UsesPotion(SearchNode node)" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs"; Text = "private sealed partial class BeamRetentionPolicy" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs"; Text = "public void AddOrderedMutationPortfolio(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs"; Text = "ArmOrderedMutationObservationBridges(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs"; Text = "VerifyOrderedMutationKeyPolicyForTesting(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.CrossTurn.cs"; Text = "private sealed partial class BeamRetentionPolicy" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.CrossTurn.cs"; Text = "public void AddCrossTurnPortfolio(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.CrossTurn.cs"; Text = "public static bool RequiresCrossTurnPlanning(SearchNode node)" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Cycle.cs"; Text = "private sealed partial class BeamRetentionPolicy" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Cycle.cs"; Text = "public void AddCyclePortfolio(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Cycle.cs"; Text = "public void AddCycleExitPortfolio(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "public List<SearchNode> RankBest(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "private sealed class RoutingChoiceNodes(SearchNode first) : List<SearchNode>" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "public void Clear() => NodesByChoice.Clear();" },
    @{ File = "CombatBeamSolver.BlockPotionInsertion.cs"; Text = "private BlockPotionInsertion? TryInsertBlockPotion(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "routingNodes = new RoutingChoiceNodes(node);" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "ReturnRoutingChoiceScratch(scratch);" },
    @{ File = "CombatBeamSolver.Transpositions.cs"; Text = "private readonly record struct TranspositionLabel(" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "private sealed class SearchRunContext(" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "private readonly record struct SearchFeatures(" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "private sealed partial class ParallelExpansionExecutor : IDisposable" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "public ExpansionWorkerOutcome[] Evaluate(" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "public int MaximumQueuedParents => SearchWaveMemoryPolicy.MaximumQueuedParents(DegreeOfParallelism);" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "List<ExpansionLane> lanes = new(DegreeOfParallelism);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "private ExpansionWorkerOutcome[] EvaluateQueuedParents(" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "private sealed class AdmittedParent(" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "public object ForkGate { get; } = new();" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "_coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "wave.BackgroundCompleted.Wait();" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "while (committed < parents.Length && parents[committed]!.TailCompleted)" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "_completedActions != Actions!.Count" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "_completedPotions != Potions!.Count" },
    @{ File = "CombatBeamSolver.EndTurnChoiceReplay.cs"; Text = "private PreparedEndTurnEvaluation EvaluatePreparedEndTurn(" },
    @{ File = "CombatBeamSolver.TurnExecutionContinuation.cs"; Text = "private static SearchBoundaryReason ContinuePlayerStart(" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "private sealed class RoundReplayCheckpoint(" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "combat.EndActionChoices();" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "combat.BeginActionChoices(cursor);" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "internal int VerifyRoundReplayCheckpointForTesting(" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "public bool HasObservedPostDrawRoundChoice;" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "public HashSet<string>? ObservedHandDrawShuffleChoiceSources;" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "public void CaptureBeforeHandDraw(CombatBeamSolver owner, CombatPredictionSimulator simulator," },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "checkpoint.HandDrawCount.HasValue ? PlayerStartStage.Draw : PlayerStartStage.AfterPlayer" },
    @{ File = "RootCombatCardGenerationPoolSnapshot.cs"; Text = "public bool TryGetEligibleCharacterCards(" },
    @{ File = "CombatBeamSolver.Retention.cs"; Text = "var maximum = BeamRetentionPolicy.GetLongTermResourceMaximum(pool);" },
    @{ File = "CombatBeamSolver.Retention.cs"; Text = "if (maximum.Count == pool.Count)" },
    @{ File = "CombatBeamSolver.EndTurnChoiceReplay.cs"; Text = "capture.ObservePendingChoice(this, pendingSourceId);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "endTurn.TransferEndTurnTo(Aggregate!, candidate);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "PublishCrossTurnStandPatBaselines(Node, _endTurnBaselines);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "ready.TransferPotionTo(Aggregate!, candidate);" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "private sealed class PrimaryChoiceReplayFrontier : IDisposable" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "=> branches >= 2 && finals >= branches && attempts >= branches;" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "public bool CanDispatchContinuation => CompletedReplays == Actions.Length" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "if (!budget.TrySpendReplayAttempt())" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "frontier.AssertConsumed();" },
    @{ File = "CombatBeamSolver.EndTurnChoiceReplay.cs"; Text = "CanReservePrimaryReplayPrefix(layer.Layer.Branches.Count," },
    @{ File = "CombatBeamSolver.EndTurnChoiceReplay.cs"; Text = "ResolveCollectedOccurrenceChoiceBranches(parent, layer.Occurrences)" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "_endTurnFrontier?.Dispose();" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "if (index != NextReplay || count < 1 || count > 4 || index + count > Actions.Length)" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "public ParallelExpansionExecutor? ActiveParallelExpansion;" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "_coordinator._run.ActiveParallelExpansion = null;" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "private void PrepareStandPatProbes(IEnumerable<SearchNode> nodes)" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "seen.Add(node.StateKey)" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "_run.StandPatCache.Add(batch[index].StateKey, evaluations[index]);" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "ExpansionLane[] lanes = EnsureBackgroundLanes();" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "_coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "wave.Completed.Wait();" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "public void EvaluateRetentionIndices(" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "ExpansionLane[] lanes = EnsureBackgroundLanes();" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "wave.Completed.Wait();" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "_coordinator._run.OffThreadAllocatedBytes += job.AllocatedBytes;" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "wave.Error?.Throw();" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "_run.RoutingChoiceSummaryBuilds += summaryGroups.Length;" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs"; Text = "RequestOrderedMutationObservation(candidate);" },
    @{ File = "SearchWaveMemoryPolicy.cs"; Text = "return checked(degreeOfParallelism * 2);" },
    @{ File = "SearchWaveMemoryPolicy.cs"; Text = "current >= maximum - current ? maximum : current * 2" },
    @{ File = "CombatBeamSolver.Phases.cs"; Text = "SearchWaveMemoryPolicy.GrowCapacity(" },
    @{ File = "CombatBeamSolver.Retention.cs"; Text = "end.ReleaseSimulator();" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "private void CommitExpansionBatch(" },
    @{ File = "CombatBeamSolver.Phases.cs"; Text = "public SolverResult Solve()" },
    @{ File = "CombatBeamSolver.Expansion.cs"; Text = "private IEnumerable<SearchNode> Expand(SearchNode node)" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "public List<SearchNode> RankFinal(IEnumerable<SearchNode> nodes)" },
    @{ File = "CombatBeamSolver.FinalPlanOrdering.cs"; Text = "private sealed class FinalPlanOrdering(" },
    @{ File = "CombatBeamSolver.FinalPlanOrdering.cs"; Text = "public FinalPlanSelection Select(" },
    @{ File = "CombatBeamSolver.Terminal.cs"; Text = "private List<SearchNode> AnnotateTurnOutcomes(List<SearchNode> ended)" },
    @{ File = "CombatBeamSolver.StateEvaluation.cs"; Text = "private SimulationSnapshot Snapshot(" }
)
foreach ($check in $beamStructureChecks) {
    $path = Join-Path $searchRoot $check.File
    if (-not (Select-String -LiteralPath $path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${path}: missing CombatBeamSolver stage member '$($check.Text)'")
    }
}
if (-not (Select-String -LiteralPath (Join-Path $searchRoot "CombatBeamSolver.Expansion.cs") -SimpleMatch "CreateWholeActionChoiceBudget" -Quiet)) {
    $violations.Add("CombatBeamSolver.Expansion.cs: repeated card choices are missing their whole-action branch quota")
}
$beamEntryPath = Join-Path $searchRoot "CombatBeamSolver.cs"
if (Select-String -LiteralPath $beamEntryPath -SimpleMatch "public SolverResult Solve()" -Quiet) {
    $violations.Add("${beamEntryPath}: Solve returned to the entry/field declaration file")
}
$beamRetentionFacadePath = Join-Path $searchRoot "CombatBeamSolver.Retention.cs"
if (Select-String -LiteralPath $beamRetentionFacadePath -SimpleMatch "private List<SearchNode> RankBest(" -Quiet) {
    $violations.Add("${beamRetentionFacadePath}: RankBest returned outside BeamRetentionPolicy")
}
$beamPhasesPath = Join-Path $searchRoot "CombatBeamSolver.Phases.cs"
if (-not (Select-String -LiteralPath $beamPhasesPath -SimpleMatch "TightenPrimarySearchIncumbentAtTurnLayer(" -Quiet)) {
    $violations.Add("${beamPhasesPath}: turn-layer incumbent is no longer tightened before coordinator pruning")
}
foreach ($match in Select-String -LiteralPath $beamPhasesPath -SimpleMatch "FinalizePrunedSelection(") {
    $violations.Add("$($match.Path):$($match.LineNumber): turn-layer incumbent pruning performs a second post-commit finalization")
}
foreach ($directPruneFinalizer in @(
    "ApplyPrimaryIncumbentBound(",
    "FinalizePrunedCycleExitProbeTickets(")) {
    foreach ($match in Select-String -LiteralPath $beamPhasesPath -SimpleMatch $directPruneFinalizer) {
        $violations.Add("$($match.Path):$($match.LineNumber): turn-layer pruning bypasses observation-debt finalization '$directPruneFinalizer'")
    }
}
foreach ($finalOrderingImplementation in @(
    "POLICY_BASELINE kind=potion_free",
    "PotionUsePolicy.IsEligible(",
    "PotionUsePolicy.MeetsAmbergrisRestriction(")) {
    if (Select-String -LiteralPath $beamPhasesPath -SimpleMatch $finalOrderingImplementation -Quiet) {
        $violations.Add("${beamPhasesPath}: final ordering implementation '$finalOrderingImplementation' returned outside FinalPlanOrdering")
    }
}
foreach ($retiredRunField in @(
    "private readonly SearchPerformanceMetrics _performance",
    "private int _expanded",
    "private readonly SearchWorkPacer _workPacer",
    "private readonly Dictionary<StateFingerprint, TranspositionFrontier> _transpositions")) {
    if (Select-String -LiteralPath $beamEntryPath -SimpleMatch $retiredRunField -Quiet) {
        $violations.Add("${beamEntryPath}: retired run-local field '$retiredRunField' returned")
    }
}
foreach ($removedWorkerRoot in @(
    "new SimulatedCombatState(",
    "IntentForecaster.Build(state",
    "_player.PotionSlots",
    "_player.Relics",
    "_player.Creature.MaxHp")) {
    foreach ($match in Select-String -LiteralPath $beamPaths -SimpleMatch $removedWorkerRoot) {
        $violations.Add("$($match.Path):$($match.LineNumber): worker root fallback '$removedWorkerRoot' returned")
    }
}

$rootModelBoundaryChecks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "Live combat state can only be captured on the main thread."
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "PredictionUtils.CreateRelic(relic, player)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "RunRngSet.FromSave(_runRngSnapshot)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\RelicPredictionStateSupport.cs"
        Text = "CaptureRootState("
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\PowerPredictionStateSupport.cs"
        Text = "HardenedShellPredictionState(original)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "PowerPredictionStateSupport.CaptureRootState(simulator, mutable, power)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.CombatRootSnapshot.cs"
        Text = "workerLiveConstructorRejected"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.cs"
        Text = "ICombatPredictionRootMaterializable materializable"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.cs"
        Text = "public CombatTerminalStamp? TerminalStamp { get; private set; }"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\CombatPlan.cs"
        Text = "public CombatTerminalStamp? TerminalStamp { get; } = terminalStamp;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\CombatBeamSolver.Terminal.cs"
        Text = "combatEndedTurn = node.Snapshot.CombatEndedTurn;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = ".Select(PredictionUtils.CloneModelForSimulation)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardGeneratedForCombatMirrors.cs"
        Text = "GetAeonglassWitherUpgradeCount(monster.Creature)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterSpawnSupport.cs"
        Text = ".SelectMany(combat.RelicsOf)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "foreach (BadgeModel badge in inner.BadgeModels)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "MultiplayerScalingRunStateField.SetValue(detachedMultiplayerScaling, null)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Block\ModifyBlockMultiplicativeMirrors.cs"
        Text = "registry.Register<MultiplayerScalingModel>(HandleMultiplayerScaling)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\PredictionModHookSubscriberCapture.cs"
        Text = "ModHelper.IterateAllRunStateSubscribers(runState)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\Common\PredictionUtils.cs"
        Text = "PredictionModModelSupport.CloneCardAttachedModels(source, clone)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.CardPile.cs"
        Text = "ContinueDrawExecution(player, drawCount, fromHandDraw, GetMaxHandSize(player)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.CardPile.cs"
        Text = "limits.GetMaxHandSize(player)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = ".Take(standardCombatListenerCount)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "UpdatePowerListenerOrder("
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Fork.cs"
        Text = "fork._powerListenerOrder ="
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\Common\PredictionModModelSupport.cs"
        Text = "ConditionalWeakTable<CardModel, object> BaseLibModifierCards"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.PowerRelics.cs"
        Text = "(_powerCardSources ??= []).Add(card)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "and not OrbModel"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\SimOrbQueue.cs"
        Text = "SetMutationObserver("
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Potions\OnUse\EntropicBrewMirrors.cs"
        Text = "limits.GetPotionSlotCount(target)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\CardOnPlaySupport.Batch042.cs"
        Text = "combat.DoomKill(simulator, doomed)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "BranchMonsterStaticSnapshot.Capture(monster)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "state.Static.AttacksByMove"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "_encounterSlots = inner.Encounter?.Slots.ToArray()"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.MonsterAi.cs"
        Text = "Root monster AI state was not captured"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "Root intent state was not captured"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterMoveEffects.StaticValues.cs"
        Text = "CaptureStaticIntValues(MonsterModel monster)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.MonsterAi.cs"
        Text = "GetMonsterStaticInt(Creature creature, string name)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionState.cs"
        Text = "boundary.AssertCanCaptureCreature(creature)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionState.cs"
        Text = "boundary.AssertCanCapturePlayer(player)"
    }
)
foreach ($check in $rootModelBoundaryChecks) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing root model boundary '$($check.Text)'")
    }
}

$removedModelFallbacks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "inner.ContainsCard(card)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "player.PlayerCombatState?.TurnNumber"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.RelicTurnStart.cs"
        Text = "RunState.CardMultiplayerConstraint"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Relics.cs"
        Text = "player.RunState.CardMultiplayerConstraint"
    }
)
foreach ($fallback in $removedModelFallbacks) {
    foreach ($match in Select-String -LiteralPath $fallback.Path -SimpleMatch $fallback.Text) {
        $violations.Add("$($fallback.Path):$($match.LineNumber): removed model fallback '$($fallback.Text)' returned")
    }
}

$removedWorkerReads = @(
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Fork.cs"
        Text = "new(InnerState)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardGeneratedForCombatMirrors.cs"
        Text = "monster.WitherUpgradeCount"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterSpawnSupport.cs"
        Text = "player.Relics"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\CombatRootSnapshot.cs"
        Text = ".MaterializeRoot("
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "_multiplayerScalingModel = inner.MultiplayerScalingModel"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.PowerRelics.cs"
        Text = "private CardModel? _powerCardSource;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\PotionOnUseSupport.cs"
        Text = "playerTarget.MaxHp"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.Damage.cs"
        Text = "creature.MaxHp <= 0"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Death\DeathPreventerMirrors.cs"
        Text = "context.Creature.MaxHp"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\CardOnPlaySupport.Batch042.cs"
        Text = "player.Relics"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\CardOnPlaySupport.Batch042.cs"
        Text = "creature.Powers"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\TurnStartRelicSupport.cs"
        Text = "player.Relics"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Potions\OnUse\EntropicBrewMirrors.cs"
        Text = "target.PotionSlots.Count"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "return branch.GetNextState(owner, rng)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "return state.GetWeight()"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "combat.Encounter?.GetNextSlot(combat)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterSpawnSupport.cs"
        Text = "combat.Encounter?.GetNextSlot(combat)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterSpawnSupport.cs"
        Text = "combat.Encounter?.Slots"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "IReadOnlyList<string> slots = Encounter?.Slots"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterMoveEffects.cs"
        Text = "MonsterValueReader.ReadInt(monster"
    }
)
foreach ($removedWorkerRead in $removedWorkerReads) {
    foreach ($match in Select-String -LiteralPath $removedWorkerRead.Path -SimpleMatch $removedWorkerRead.Text) {
        $violations.Add("$($removedWorkerRead.Path):$($match.LineNumber): worker live read '$($removedWorkerRead.Text)' returned")
    }
}

$unattendedEntryPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.cs"
foreach ($check in @(
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'source "$script_dir/headless-runtime.sh"' },
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'hr_acquire "$process_pid" "$process_identity_start_time"' },
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'if ((option_value[stop-instance] == 1)); then' },
    @{ Path = 'tools/run-unattended-test.ps1'; Text = ". (Join-Path `$PSScriptRoot 'headless-runtime.ps1')" },
    @{ Path = 'tools/run-unattended-test.ps1'; Text = 'if ($StopInstance) {' },
    @{ Path = 'tools/run-headless-matrix.sh'; Text = '--stop-instance' },
    @{ Path = 'tools/run-headless-matrix.ps1'; Text = '"-StopInstance"' },
    @{ Path = 'tools/headless-runtime.sh'; Text = 'hr_prepare_snapshot() {' },
    @{ Path = 'tools/headless-runtime.sh'; Text = 'hr_bind() {' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Set-HeadlessGameSnapshot(' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Enter-HeadlessHostLease(' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Set-HeadlessHostGame(' })) {
    $path = Join-Path $repositoryRoot $check.Path
    if (-not (Select-String -LiteralPath $path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${path}: missing headless infrastructure ownership boundary '$($check.Text)'")
    }
}
foreach ($matrix in @('tools/run-headless-matrix.sh', 'tools/run-headless-matrix.ps1')) {
    $path = Join-Path $repositoryRoot $matrix
    if (Select-String -LiteralPath $path -SimpleMatch 'MATRIX-CLEANUP' -Quiet) {
        $violations.Add("${path}: matrix cleanup must not dispatch a new game request")
    }
}
foreach ($helper in @('tools/headless-runtime.sh', 'tools/headless-runtime.ps1')) {
    $path = Join-Path $repositoryRoot $helper
    foreach ($forbidden in @('combat_solver_test_request.json', 'SolverSettings')) {
        if (Select-String -LiteralPath $path -SimpleMatch $forbidden -Quiet) {
            $violations.Add("${path}: protocol/game settings leaked into headless resource owner '$forbidden'")
        }
    }
}
$unattendedProtocolHostPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ProtocolHost.cs"
$unattendedWriterPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.Writer.cs"
$unattendedScenarioBuilderPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ScenarioBuilder.cs"
$unattendedAssertionsPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.Assertions.cs"
$unattendedExecutorPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.Executor.cs"
foreach ($check in @(
    @{ Path = $unattendedEntryPath; Text = "private static readonly ProtocolHost Host = new();" },
    @{ Path = $unattendedProtocolHostPath; Text = "private sealed partial class ProtocolHost" },
    @{ Path = $unattendedProtocolHostPath; Text = "private async Task RunRequestLoopAsync(NGame host)" },
    @{ Path = $unattendedProtocolHostPath; Text = "private void Activate(UnattendedTestRequest request)" },
    @{ Path = $unattendedProtocolHostPath; Text = "private void Reset()" },
    @{ Path = $unattendedWriterPath; Text = "private sealed partial class Writer(" },
    @{ Path = $unattendedWriterPath; Text = "public RuntimeMemorySnapshot Write(" },
    @{ Path = $unattendedWriterPath; Text = "private static void WriteResult(UnattendedTestResult result, UnattendedTestRequest request)" },
    @{ Path = $unattendedScenarioBuilderPath; Text = "private sealed partial class ScenarioBuilder(" },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/GeneratedCombatScenario.cs"); Text = "internal static ResolvedGeneratedCombatScenario Resolve(" },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.GeneratedScenario.cs"); Text = "private void PrepareGeneratedScenario()" },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.GeneratedScenario.cs"); Text = "private void CaptureGeneratedOpening(" },
    @{ Path = $unattendedWriterPath; Text = "public void WriteGeneratedArtifact(" },
    @{ Path = $unattendedScenarioBuilderPath; Text = "public async Task<ScenarioContext> BuildAsync()" },
    @{ Path = $unattendedScenarioBuilderPath; Text = "public CombatState? CombatState { get; private set; }" },
    @{ Path = $unattendedAssertionsPath; Text = "private sealed class Assertions(" },
    @{ Path = $unattendedAssertionsPath; Text = "public async Task RunBeforeExecutionAsync(ScenarioContext scenario)" },
    @{ Path = $unattendedAssertionsPath; Text = "public void AssertAfterExecution(ScenarioContext scenario, ExecutionOutcome outcome)" },
    @{ Path = $unattendedExecutorPath; Text = "private sealed class Executor(" },
    @{ Path = $unattendedExecutorPath; Text = "public async Task<ExecutionOutcome> ExecuteAsync(ScenarioContext scenario)" },
    @{ Path = $unattendedExecutorPath; Text = "private FastModeType? ApplySettingsOverrides()" },
    @{ Path = $unattendedExecutorPath; Text = "public void RestoreSettings()" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing unattended protocol boundary '$($check.Text)'")
    }
}
foreach ($retiredProtocolHostMember in @(
    "private static bool _requestLoopStarted",
    "private static async Task RunRequestLoopAsync",
    "private static void WriteResult(UnattendedTestResult result, UnattendedTestRequest request)",
    "private static RuntimeMemorySnapshot CaptureRuntimeMemory()")) {
    if (Select-String -LiteralPath $unattendedEntryPath -SimpleMatch $retiredProtocolHostMember -Quiet) {
        $violations.Add("${unattendedEntryPath}: protocol host member '$retiredProtocolHostMember' returned to runner entry")
    }
}
if (Select-String -LiteralPath $unattendedEntryPath -SimpleMatch "StartNewSingleplayerRun(" -Quiet) {
    $violations.Add("${unattendedEntryPath}: scenario construction returned outside ScenarioBuilder")
}
foreach ($assertionImplementation in @(
    "VerifyPredictionFailureBoundaries",
    "ExpectedFinishedTurn is")) {
    if (Select-String -LiteralPath $unattendedEntryPath -SimpleMatch $assertionImplementation -Quiet) {
        $violations.Add("${unattendedEntryPath}: unattended assertion '$assertionImplementation' returned outside Assertions")
    }
}
foreach ($executorImplementation in @(
    "SolverController.SetFullAuto(",
    "StopAfterExpectedReuse",
    "orb_differential_",
    "potion_differential_")) {
    if (Select-String -LiteralPath $unattendedEntryPath -SimpleMatch $executorImplementation -Quiet) {
        $violations.Add("${unattendedEntryPath}: unattended executor implementation '$executorImplementation' returned outside Executor")
    }
}

$overlaySnapshotPath = Join-Path $repositoryRoot "src\UI\SolverOverlaySnapshot.cs"
$overlayRendererPaths = @(
    (Join-Path $repositoryRoot "src\UI\SolverOverlay.cs"),
    (Join-Path $repositoryRoot "src\UI\SolverRouteRow.cs"),
    (Join-Path $repositoryRoot "src\UI\SolverActionPill.cs"),
    (Join-Path $repositoryRoot "src\UI\SolverActionBar.cs")
)
foreach ($check in @(
    @{ Path = $overlaySnapshotPath; Text = "internal sealed record SolverOverlaySnapshot(" },
    @{ Path = $overlaySnapshotPath; Text = "public static SolverOverlaySnapshot Capture(SolverResult result, bool unexpectedReplan)" },
    @{ Path = Join-Path $repositoryRoot "src\UI\SolverOverlay.cs"; Text = "public static void ShowResult(Node host, SolverOverlaySnapshot snapshot)" },
    @{ Path = Join-Path $repositoryRoot "src\UI\SolverRouteRow.cs"; Text = "public void Populate(SolverOverlayTurnSnapshot turn)" },
    @{ Path = Join-Path $repositoryRoot "src\UI\SolverActionPill.cs"; Text = "public static Control Create(SolverOverlayActionSnapshot action)" },
    @{ Path = Join-Path $repositoryRoot "src\Runtime\SolverController.cs"; Text = "SolverOverlaySnapshot.CaptureWithReviewedWorldlines(" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing overlay snapshot boundary '$($check.Text)'")
    }
}
foreach ($rendererPath in $overlayRendererPaths) {
    foreach ($mutableSearchType in @("SolverResult", "PlanAction", "PlanCardChoice", "ModelDb")) {
        foreach ($match in Select-String -LiteralPath $rendererPath -SimpleMatch $mutableSearchType) {
            $violations.Add("${rendererPath}:$($match.LineNumber): mutable search type '$mutableSearchType' returned to renderer")
        }
    }
}

$bugReportExporterPath = Join-Path $repositoryRoot "src\Diagnostics\BugReports\CombatBugReportExporter.cs"
$diagnosticJournalPath = Join-Path $repositoryRoot "src\Runtime\CombatDiagnosticJournal.cs"
$bugReportUploaderPath = Join-Path $repositoryRoot "src\Diagnostics\BugReports\CombatBugReportUploader.cs"
$solverSettingsPanelPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.cs"
$solverSettingsGeneralPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.General.cs"
$solverSettingsPerformancePath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.Performance.cs"
$solverSettingsBugReportsPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.BugReports.cs"
$solverSettingsControlsPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.Controls.cs"
foreach ($check in @(
    @{ Path = $diagnosticJournalPath; Text = "AppendOnlyEventLog<CombatLogEntry>" },
    @{ Path = $diagnosticJournalPath; Text = "_session?.Log.CaptureAsync()" },
    @{ Path = $bugReportExporterPath; Text = "Entry.Logger.Journal.CaptureAsync()" },
    @{ Path = $bugReportExporterPath; Text = "WriteDiagnosticLogs(archive, diagnosticLogs)" },
    @{ Path = $bugReportExporterPath; Text = "private static readonly BlockingCollection<Action> BackgroundOperations = new();" },
    @{ Path = $bugReportExporterPath; Text = "QueueCheckpointWrite(session, capture);" },
    @{ Path = $bugReportExporterPath; Text = "Task<ForensicArchiveBundle> forensicsTask = QueueBackground(" },
    @{ Path = $bugReportExporterPath; Text = "ForensicArchiveBundle forensics = await forensicsTask.ConfigureAwait(false);" },
    @{ Path = $bugReportExporterPath; Text = "CombatBugReportMetadata.CaptureCombat" },
    @{ Path = $bugReportUploaderPath; Text = "ReadMetadata(zipPath, submissionId, description)" },
    @{ Path = $bugReportUploaderPath; Text = "AllowAutoRedirect = false" },
    @{ Path = $bugReportUploaderPath; Text = "IProgress<CombatBugReportUploadProgress>" },
    @{ Path = $bugReportUploaderPath; Text = "HttpCompletionOption.ResponseHeadersRead" },
    @{ Path = $bugReportUploaderPath; Text = "CancellationToken requestCancellationToken" },
    @{ Path = $bugReportUploaderPath; Text = "ReadServerReceipt(body)" },
    @{ Path = $bugReportUploaderPath; Text = "UseProxy = false" },
    @{ Path = $solverSettingsBugReportsPath; Text = "private ProgressBar _uploadProgress = null!;" },
    @{ Path = $solverSettingsBugReportsPath; Text = "private volatile bool _uploadInProgress;" },
    @{ Path = $solverSettingsBugReportsPath; Text = "Interlocked.Exchange(ref _uploadCompletion, completion)" },
    @{ Path = $solverSettingsBugReportsPath; Text = "TryApplyUploadCompletion()" },
    @{ Path = $solverSettingsBugReportsPath; Text = "等待服务器确认" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing bug-report ownership boundary '$($check.Text)'")
    }
}
if (Select-String -LiteralPath $bugReportUploaderPath -SimpleMatch "using Godot" -Quiet) {
    $violations.Add("${bugReportUploaderPath}: uploader must not own Godot UI state")
}
foreach ($legacyLogRead in @("AddFileTail(", "CaptureLogStarts(", '"*.log"')) {
    if (Select-String -LiteralPath $bugReportExporterPath -SimpleMatch $legacyLogRead -Quiet) {
        $violations.Add("${bugReportExporterPath}: global log collection must stay out of report exports")
    }
}

$searchCompletionNotifierPath = Join-Path $repositoryRoot "src\Runtime\SearchCompletionNotifier.cs"
foreach ($check in @(
    @{ Path = $searchCompletionNotifierPath; Text = "if (!OperatingSystem.IsWindows())" },
    @{ Path = $searchCompletionNotifierPath; Text = "DisplayServer.GetName()" },
    @{ Path = $searchCompletionNotifierPath; Text = 'EntryPoint = "Shell_NotifyIconW"' },
    @{ Path = $searchCompletionNotifierPath; Text = 'EntryPoint = "LoadIconW"' },
    @{ Path = $searchCompletionNotifierPath; Text = "GetWindowThreadProcessId(foreground, out uint processId)" },
    @{ Path = $searchCompletionNotifierPath; Text = "ShellNotifyIcon(NotifyIconDelete, ref data)" },
    @{ Path = (Join-Path $repositoryRoot "src\Runtime\SolverController.SearchLifecycle.cs"); Text = "SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Stale)" },
    @{ Path = $turnSetupPath; Text = "SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed)" },
    @{ Path = $solverSettingsGeneralPath; Text = "CreateSearchCompletionNotificationPolicyInput()" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing search completion notification boundary '$($check.Text)'")
    }
}

foreach ($check in @(
    @{ Path = $solverSettingsPanelPath; Text = "TrySelectPage(SettingsPage page)" },
    @{ Path = $solverSettingsPanelPath; Text = "CommitPending()" },
    @{ Path = $solverSettingsGeneralPath; Text = "CreateGeneralPage()" },
    @{ Path = $solverSettingsPerformancePath; Text = "CreatePerformancePage()" },
    @{ Path = $solverSettingsPerformancePath; Text = "SetAdvancedParametersExpanded" },
    @{ Path = $solverSettingsBugReportsPath; Text = "CreateBugReportsPage()" },
    @{ Path = $solverSettingsControlsPath; Text = "CreatePageScroll(Control content)" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing settings panel ownership boundary '$($check.Text)'")
    }
}

$mirrorRegistryPath = Join-Path $repositoryRoot "src\Engine\Common\Mirrors\MethodMirrorRegistry.cs"
foreach ($check in @(
    @{ File = 'src/Search/SearchPolicySnapshot.cs'; Text = 'IReadOnlyList<RelicCounterTarget> RelicTargets' },
    @{ File = 'src/Search/CombatBeamSolver.Phases.cs'; Text = 'policy.RelicTargetsSatisfied(node.Snapshot.RelicCounters)' },
    @{ File = 'src/Search/CombatSearchCoordinator.cs'; Text = 'policy.RelicTargetsSatisfied(result.Snapshot.RelicCounters)' },
    @{ File = 'src/Runtime/SolvedRouteCache.cs'; Text = 'policy.RelicTargets' },
    @{ File = 'src/Search/CombatBeamSolver.OpeningExpansion.cs'; Text = 'ApplyFixedPrefix(seed, prefix)' },
    @{ File = 'src/UI/SolverRelicStrategyPanel.cs'; Text = 'row.Enabled.ButtonPressed' })) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $check.File) -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.File): missing relic policy ownership '$($check.Text)'")
    }
}
$dynamicVarMetadataPath = Join-Path $repositoryRoot "src\Runtime\DynamicVarCloneMetadataPatches.cs"
foreach ($rule in @('SimulationNotificationIsolation.IsActive', '"DynamicVarUpgrades"', 'table.TryGetValue(source', 'Tips.TryGetValue(__0')) {
    if (-not (Select-String -LiteralPath $dynamicVarMetadataPath -SimpleMatch $rule -Quiet)) {
        $violations.Add("DynamicVarCloneMetadataPatches.cs: missing sparse metadata boundary '$rule'")
    }
}
if (Select-String -LiteralPath $dynamicVarMetadataPath -SimpleMatch '.Clear()' -Quiet) {
    $violations.Add('DynamicVarCloneMetadataPatches.cs: global metadata clearing is forbidden')
}
$mirrorDescriptorPath = Join-Path $repositoryRoot "src\Engine\Common\Mirrors\MethodMirrorRegistryDescriptor.cs"
$coverageCatalogPath = Join-Path $repositoryRoot "tools\CoverageCatalog\Program.cs"
foreach ($check in @(
    @{ Path = $mirrorDescriptorPath; Text = "public interface IMethodMirrorRegistryDescriptorProvider" },
    @{ Path = $mirrorDescriptorPath; Text = "public sealed record MethodMirrorRegistryDescriptor(" },
    @{ Path = $mirrorRegistryPath; Text = ": IMethodMirrorRegistryDescriptorProvider" },
    @{ Path = $mirrorRegistryPath; Text = "public MethodMirrorRegistryDescriptor DescribeMirrorSupport()" },
    @{ Path = $coverageCatalogPath; Text = "registry is not IMethodMirrorRegistryDescriptorProvider descriptorProvider" },
    @{ Path = $coverageCatalogPath; Text = "descriptorProvider.DescribeMirrorSupport()" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing mirror registry descriptor boundary '$($check.Text)'")
    }
}
foreach ($privateRegistryField in @('"_registrations"', '"_inferrer"', '"_strictInferrer"')) {
    foreach ($match in Select-String -LiteralPath $coverageCatalogPath -SimpleMatch $privateRegistryField) {
        $violations.Add("${coverageCatalogPath}:$($match.LineNumber): private registry reflection '$privateRegistryField' returned")
    }
}
if (Select-String -LiteralPath (Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs") `
        -SimpleMatch "_monsterAiStates?.Remove(creature)" -Quiet) {
    $violations.Add("SimulatedCombatState.cs: active-roster removal must retain known-monster AI state through move completion")
}

$metadataReuseChecks = @(
    @{ File = 'src/Runtime/PowerAmountComparisonPatch.cs'; Text = 'Enum.GetUnderlyingType(typeof(PowerStackType)) != typeof(int)' },
    @{ File = 'src/Runtime/PowerAmountComparisonPatch.cs'; Text = 'if (matches.Count != 2' },
    @{ File = 'src/Runtime/PowerAmountComparisonPatch.cs'; Text = 'code[i].labels.Count != 0 || code[i].blocks.Count != 0' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = 'IReadOnlyList<PowerModel>? powers = effectivePrefix is not null ? _effectivePowers : null;' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = '_effectiveHookListenerPrefix = null;' },
    @{ File = 'src/Search/SimulatedCombatState.Fork.cs'; Text = 'ReferenceEquals(_activeHookListenerPrefix, _effectiveHookListenerPrefix)' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = 'private IReadOnlyList<AbstractModel> GetBaseHookListenerPrefix()' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = 'if (insertionIndex < 0 && requirePrefixAnchor)' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = '_baseHookListenerPrefix = null;' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = 'private void InvalidateCardAndOrbHookListeners()' },
    @{ File = 'src/Search/SimulatedCombatState.Fork.cs'; Text = 'fork._baseHookListenerPrefix = RemapCachedModels(_baseHookListenerPrefix, context);' },
    @{ File = 'src/Search/CombatBeamSolver.BeamRetentionPolicy.cs'; Text = 'group.RankSummary = new(' },
    @{ File = 'src/Search/CombatBeamSolver.BeamRetentionPolicy.cs'; Text = 'ComputeRoutingParentRetentionRank(group)' },
    @{ File = 'src/Engine/Common/MirroredHookListenerFilter.cs'; Text = 'shared.Matches(source)' },
    @{ File = 'src/Engine/Common/MirroredHookListenerFilter.cs'; Text = 'Volatile.Write(ref _sharedLayouts[slot], layout)' },
    @{ File = 'src/Engine/Common/MirroredHookListenerFilter.cs'; Text = 'source.Count <= MaxSharedLayoutLength' },
    @{ File = 'src/Engine/Common/MirroredHookListenerFilter.cs'; Text = 'BaseHooks.Append(NativeKeywordHook)' },
    @{ File = 'src/Engine/InCombat/Simulation/CombatPredictedCardExtensions.cs'; Text = '!listeners.HasAny(MirroredHookMask.TryModifyKeywordsInCombat)' }
)
foreach ($check in $metadataReuseChecks) {
    $path = Join-Path $repositoryRoot $check.File
    if (-not (Select-String -LiteralPath $path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${path}: missing exact metadata reuse boundary '$($check.Text)'")
    }
}

# Keep the no-op dispatch metadata complete when callbacks are added to the facade.
foreach ($file in @("CombatBeamSolver.RetentionJobs.cs", "CombatBeamSolver.BeamRetentionPolicy.cs", "CombatBeamSolver.BeamRetentionPolicy.Mutation.cs", "CombatBeamSolver.BeamRetentionPolicy.CrossTurn.cs", "CombatBeamSolver.BeamRetentionPolicy.Cycle.cs")) {
    foreach ($forbidden in @("Parallel.For(", "Task.Run(")) {
        if (Select-String -LiteralPath (Join-Path $searchRoot $file) -SimpleMatch $forbidden -Quiet) {
            $violations.Add("$($file): retention work bypassed fixed lanes '$forbidden'")
        }
    }
}

$mirroredFilterText = Get-Content -LiteralPath (Join-Path $repositoryRoot "src/Engine/Common/MirroredHookListenerFilter.cs") -Raw
$mirroredHookNames = [System.Collections.Generic.HashSet[string]]::new()
[void]$mirroredHookNames.Add('TryModifyKeywordsInCombat')
foreach ($sourceFile in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src/Engine/InCombat/Mirrors") -Filter '*.cs' -Recurse) {
    $sourceText = Get-Content -LiteralPath $sourceFile.FullName -Raw
    foreach ($match in [regex]::Matches($sourceText, 'nameof\(AbstractModel\.([A-Za-z][A-Za-z0-9]*)\)')) {
        [void]$mirroredHookNames.Add($match.Groups[1].Value)
    }
}
$hookFacadeText = Get-Content -LiteralPath (Join-Path $repositoryRoot "src/Engine/InCombat/Mirrors/HookMirrors.cs") -Raw
foreach ($match in [regex]::Matches($hookFacadeText, '(?:listener|modifier)\.([A-Za-z][A-Za-z0-9]*)\(')) {
    [void]$mirroredHookNames.Add($match.Groups[1].Value)
}
foreach ($hookName in $mirroredHookNames) {
    if (-not $mirroredFilterText.Contains("nameof(AbstractModel.$hookName)")) {
        $violations.Add("Missing mirrored hook participation metadata: $hookName")
    }
}

# Native clone eligibility stays outside search scheduling and keeps the runtime gate.
foreach ($requiredCloneBoundary in @(
    @{ Path = 'src/Runtime/BaseLibCloneConcurrencyPatch.cs'; Text = 'BaseLibCloneConcurrency.Enter()' },
    @{ Path = 'src/Engine/Common/PredictionUtils.cs'; Text = 'NativeModelCloneConcurrency.CanCloneIndependently(source)' }
)) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $requiredCloneBoundary.Path) -SimpleMatch $requiredCloneBoundary.Text -Quiet)) {
        $violations.Add("Missing clone boundary: $($requiredCloneBoundary.Path)")
    }
}
if (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Engine/Common/NativeModelCloneConcurrency.cs') -SimpleMatch 'CombatSolver.Search' -Quiet) {
    $violations.Add('Clone eligibility depends on search policy.')
}

# Stable manual-potion prefixes share action completion and retain ordinary Fork guards.
foreach ($rule in @(
    @{ RelativePath = 'src/Prediction/PotionChoiceContinuation.cs'; Text = 'seed.AssertForkable();' },
    @{ RelativePath = 'src/Prediction/PotionChoiceContinuation.cs'; Text = 'lock (_gate)' },
    @{ RelativePath = 'src/Prediction/PotionChoiceContinuation.cs'; Text = '!PotionChoiceMirrors.RequiresChoice(potion)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.PotionChoiceContinuation.cs'; Text = 'ReferenceEquals(_parent, candidate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.PotionChoiceContinuation.cs'; Text = '_run.PotionChoicePrefixForks++;' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.Expansion.cs'; Text = 'PotionExecutionSupport.Complete(' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.PrimaryChoiceReplay.cs'; Text = 'PotionCheckpoint?.Dispose();' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ParallelExpansion.cs'; Text = '_run.PotionChoicePrefixForks += source.PotionChoicePrefixForks;' }
)) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $rule.RelativePath) -SimpleMatch $rule.Text -Quiet)) {
        $violations.Add("Missing potion continuation ownership boundary: $($rule.RelativePath): $($rule.Text)")
    }
}
foreach ($forbidden in @('Task<', 'Func<', 'Action<')) {
    if (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Prediction/PotionChoiceContinuation.cs') -SimpleMatch $forbidden -Quiet) {
        $violations.Add("Potion continuation retained an executable closure: $forbidden")
    }
}

# Nested execution saves owned data frames and preserves the ordinary transaction guards.
foreach ($rule in @(
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs'; Text = 'GuardOrdinaryExecutionContinuationFork();' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'if (_owner.HasCapturedExecutionContinuation && !_acknowledged)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'StateStore.SupportsManualCardChoiceContinuation' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'DetachPendingExecutionChoice();' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'CombatPredictionState state = State.Fork(context);' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'step.Scopes?.Fork(context), step.Frame.Fork(context)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'using (_trace.ResumeExecution(step.Trace))' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.DrawContinuation.cs'; Text = 'mapped! : card.Fork(context)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionHistory.ExecutionContinuation.cs'; Text = 'unresolved.SetEquals(deferred)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionHistory.ExecutionContinuation.cs'; Text = 'active.SetEquals(activePlays)' },
    @{ RelativePath = 'src/Search/SimulatedCombatState.ExecutionScopes.cs'; Text = 'ForkExecutionDeaths(Deaths, context);' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'; Text = 'tail.ConsumedChoices != prefix.Count' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'; Text = 'ReferenceEquals(_parent, candidate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'; Text = 'lock (_gate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'; Text = '_simulator = null; _parent = null; _action = null; _prefix = null; _continuation = null;' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ParallelExpansion.cs'; Text = '_run.ExecutionChoiceReuses += source.ExecutionChoiceReuses;' }
)) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $rule.RelativePath) -SimpleMatch $rule.Text -Quiet)) {
        $violations.Add("Missing execution continuation ownership boundary: $($rule.RelativePath): $($rule.Text)")
    }
}
foreach ($relativePath in @(
    'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs',
    'src/Search/SimulatedCombatState.ExecutionScopes.cs',
    'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'
)) {
    foreach ($forbidden in @('Task<', 'Func<', 'Action<')) {
        if (Select-String -LiteralPath (Join-Path $repositoryRoot $relativePath) -SimpleMatch $forbidden -Quiet) {
            $violations.Add("Execution continuation retained an executable closure: $relativePath : $forbidden")
        }
    }
}

# A suspended own-choice frame belongs to its continuation; ordinary Fork remains strict.
foreach ($rule in @(
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs'; Text = 'GuardOrdinaryCardContinuationFork();' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'PrepareExecutionCardPlay(source.Card, source.Play, context);' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'CardPlay play = context.RequireRemap(source.Play);' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'StateStore.SupportsManualCardChoiceContinuation' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'DetachPendingManualCardChoice();' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'source.Choice.Fork(context)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'frame.Choice.Resolve(this, frame.Card)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'child._blockGainedByCardPlay.Add(play, block)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionHistory.CardContinuation.cs'; Text = 'e.Options.Select(CopyOption)' },
    @{ RelativePath = 'src/Search/SimulatedCombatState.CardContinuation.cs'; Text = 'Options = spec.Options.Select(context.RequireRemap)' },
    @{ RelativePath = 'src/Prediction/CardChoiceContinuation.cs'; Text = 'lock (_gate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.CardChoiceContinuation.cs'; Text = 'ReferenceEquals(_parent, candidate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.CardChoiceContinuation.cs'; Text = 'return Enumerate(this, checkpoint, branches);' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.Expansion.cs'; Text = 'countTransition: false' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.PrimaryChoiceReplay.cs'; Text = 'CardCheckpoint?.Dispose();' },
    @{ RelativePath = 'src/Search/SimulatedCombatState.CardContinuation.cs'; Text = '_cardExecutionScopeDepth != 0' }
)) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $rule.RelativePath) -SimpleMatch $rule.Text -Quiet)) {
        $violations.Add("Missing card continuation ownership boundary: $($rule.RelativePath): $($rule.Text)")
    }
}
foreach ($forbidden in @('Task<', 'Func<', 'Action<')) {
    if (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Prediction/CardChoiceContinuation.cs') -SimpleMatch $forbidden -Quiet) {
        $violations.Add("Continuation retained an executable closure: $forbidden")
    }
}

if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Search/CombatHistoryCounterKey.cs') -SimpleMatch 'simulator.History.GetCounters(owner)' -Quiet)) {
    $violations.Add('History key must consume incremental totals')
}
$historySolverPath = Join-Path $repositoryRoot 'src/Search/CombatBeamSolver.cs'
if (-not (Select-String -LiteralPath $historySolverPath -SimpleMatch 'root.PlayerCount == 1' -Quiet)) {
    $violations.Add('History-sensitive transposition key lost the single-player gate')
}
if (-not (Select-String -LiteralPath $historySolverPath -SimpleMatch 'CombatHistoryCounterKey.AppliesTo(root.PlayerCardIds)' -Quiet)) {
    $violations.Add('History-sensitive transposition key lost the reader-card gate')
}
foreach ($historyFile in @('CombatPredictionHistory.cs', 'CombatPredictionHistory.CardContinuation.cs', 'CombatPredictionHistory.ExecutionContinuation.cs')) {
    $historyPath = Join-Path $repositoryRoot "src/Engine/InCombat/Simulation/$historyFile"
    if (-not (Select-String -LiteralPath $historyPath -SimpleMatch '_counterOwner' -Quiet)) {
        $violations.Add("History fork lost counter owner: $historyFile")
    }
    if (-not (Select-String -LiteralPath $historyPath -SimpleMatch '_counters' -Quiet)) {
        $violations.Add("History fork lost counter totals: $historyFile")
    }
}
if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Search/CombatBeamSolver.StateEvaluation.cs') -SimpleMatch 'CombatHistoryCounterKey.Append(ref key, simulator, _player)' -Quiet)) {
    $violations.Add('State key no longer includes history counters')
}

if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Search/CombatSearchCoordinator.cs') -SimpleMatch 'PotionStrategy = policy.PotionStrategy.ForForcedBaseline()' -Quiet)) {
    $violations.Add('Mixed Smart potion search must establish a forced-only baseline')
}
if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Search/CombatSearchCoordinator.cs') -SimpleMatch 'result.PotionStrategicCostByTurn.Values.Sum() - forced.ForcedStrategicHpCost' -Quiet)) {
    $violations.Add('Smart potion opportunity cost must exclude forced potion cost')
}
if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Search/CombatSearchCoordinator.cs') -SimpleMatch '!= SolverPotionDirective.Force' -Quiet)) {
    $violations.Add('Smart potion layer capacity must exclude forced directives')
}
if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Search/PotionStrategySnapshot.cs') -SimpleMatch 'if (_onlyForcedUses)' -Quiet)) {
    $violations.Add('Forced potion baseline lost its optional-use gate')
}

if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Search/CombatBeamSolver.Phases.cs') -SimpleMatch 'TurnStartChoicePreviewPolicy.ChoicesForTurn(' -Quiet)) {
    $violations.Add('Live search previews must carry frozen turn-start choices')
}
if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/UI/SolverOverlaySnapshot.cs') -SimpleMatch 'FormatTurnStartChoices(preview.TurnStartChoices)' -Quiet)) {
    $violations.Add('Current-turn overlay preview must render turn-start choices')
}
if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/UI/SolverOverlaySnapshot.cs') -SimpleMatch 'FormatTurnStartChoices(frontier.TurnStartChoices)' -Quiet)) {
    $violations.Add('Frontier overlay preview must render turn-start choices')
}


# Card semantics below are pinned to the repository's v0.107.1 game-body assembly.
# These guards intentionally reject later-version behavior when upstream changes are backported.
$trackingCardPath = Join-Path $repositoryRoot 'src/Prediction/CardOnPlaySupport.Batch042.cs'
$trackingCardText = [IO.File]::ReadAllText($trackingCardPath)
if (-not $trackingCardText.Contains('combat.GetAmount<TrackingPower>(owner) > 0 ? 1 : 2')) {
    $violations.Add("${trackingCardPath}: 0.107.1 Tracking must apply 2 initially and +1 thereafter")
}
if ($trackingCardText.Contains('combat.Apply<TrackingPower>(owner, 50, owner)')) {
    $violations.Add("${trackingCardPath}: later-version percentage Tracking application returned")
}

$damageMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Damage/ModifyDamageMirrors.cs'
$damageMirrorText = [IO.File]::ReadAllText($damageMirrorPath)
$trackingHandlerStart = $damageMirrorText.IndexOf('private static decimal HandleTrackingPower')
$trackingHandlerEnd = $damageMirrorText.IndexOf('private static decimal HandleWeakPower', $trackingHandlerStart)
if ($trackingHandlerStart -lt 0 -or $trackingHandlerEnd -le $trackingHandlerStart) {
    $violations.Add("${damageMirrorPath}: Tracking multiplier handler boundary is missing")
}
else {
    $trackingHandler = $damageMirrorText.Substring($trackingHandlerStart, $trackingHandlerEnd - $trackingHandlerStart)
    if (-not $trackingHandler.Contains('combat.GetAmount<TrackingPower>(power.Owner)')) {
        $violations.Add("${damageMirrorPath}: 0.107.1 Tracking multiplier must use the branch TrackingPower amount")
    }
    if ($trackingHandler.Contains('/ 100m')) {
        $violations.Add("${damageMirrorPath}: later-version percentage Tracking multiplier returned")
    }
}

$bespokeCardPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/BespokeCardMirrors.cs'
$bespokeCardText = [IO.File]::ReadAllText($bespokeCardPath)
$sacrificeStart = $bespokeCardText.IndexOf('public static void SacrificeOnPlay')
$sacrificeEnd = $bespokeCardText.IndexOf('public static void SecondWindOnPlay', $sacrificeStart)
if ($sacrificeStart -lt 0 -or $sacrificeEnd -le $sacrificeStart) {
    $violations.Add("${bespokeCardPath}: Sacrifice mirror boundary is missing")
}
else {
    $sacrificeBlock = $bespokeCardText.Substring($sacrificeStart, $sacrificeEnd - $sacrificeStart)
    if (-not $sacrificeBlock.Contains('MaxHp * 2')) {
        $violations.Add("${bespokeCardPath}: 0.107.1 Sacrifice must gain twice Osty's max HP as block")
    }
    if ($sacrificeBlock.Contains('MaxHp * 3')) {
        $violations.Add("${bespokeCardPath}: later-version Sacrifice x3 block returned")
    }
}

$cardOnPlayPath = Join-Path $repositoryRoot 'src/Prediction/CardOnPlaySupport.cs'
$cardOnPlayText = [IO.File]::ReadAllText($cardOnPlayPath)
$forgottenRitualStart = $cardOnPlayText.IndexOf('case ForgottenRitual')
$forgottenRitualEnd = $cardOnPlayText.IndexOf('case Fuel:', $forgottenRitualStart)
if ($forgottenRitualStart -lt 0 -or $forgottenRitualEnd -le $forgottenRitualStart) {
    $violations.Add("${cardOnPlayPath}: Forgotten Ritual compensation boundary is missing")
}
else {
    $forgottenRitualBlock = $cardOnPlayText.Substring(
        $forgottenRitualStart,
        $forgottenRitualEnd - $forgottenRitualStart)
    if (-not $forgottenRitualBlock.Contains(
        'case ForgottenRitual when combat.WasCardExhaustedThisTurn(owner):')) {
        $violations.Add("${cardOnPlayPath}: 0.107.1 Forgotten Ritual must require owner card Exhaust history before gaining Energy")
    }
    if ($forgottenRitualBlock.Contains('case ForgottenRitual or Luminesce:')) {
        $violations.Add("${cardOnPlayPath}: later-version unconditional Forgotten Ritual Energy behavior returned")
    }
}

$hazeStart = $cardOnPlayText.IndexOf('case Haze:')
$hazeEnd = $cardOnPlayText.IndexOf('case HiddenCache:', $hazeStart)
if ($hazeStart -lt 0 -or $hazeEnd -le $hazeStart) {
    $violations.Add("${cardOnPlayPath}: Haze compensation boundary is missing")
}
else {
    $hazeBlock = $cardOnPlayText.Substring($hazeStart, $hazeEnd - $hazeStart)
    if (-not $hazeBlock.Contains('PoisonPower')) {
        $violations.Add("${cardOnPlayPath}: 0.107.1 Haze must apply Poison")
    }
    if ($hazeBlock.Contains('WeakPower')) {
        $violations.Add("${cardOnPlayPath}: later-version Haze Weak application returned")
    }
}


$resultLocationMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/ModifyCardPlayResultLocationMirrors.cs'
$resultLocationMirrorText = [IO.File]::ReadAllText($resultLocationMirrorPath)
$feralStart = $resultLocationMirrorText.IndexOf('private static CardLocation HandleFeralPower')
$feralEnd = $resultLocationMirrorText.IndexOf('private static CardLocation HandleNostalgiaPower', $feralStart)
if ($feralStart -lt 0 -or $feralEnd -le $feralStart) {
    $violations.Add("${resultLocationMirrorPath}: Feral result-pile mirror boundary is missing")
}
else {
    $feralBlock = $resultLocationMirrorText.Substring($feralStart, $feralEnd - $feralStart)
    foreach ($requiredFeralRule in @(
        'card.Owner.Creature != power.Owner',
        'card.Type != CardType.Attack',
        'context.Resources.EnergyValue > 0',
        'state.ZeroCostAttacksPlayed >= power.Amount',
        'location.pileType = PileType.Hand',
        'location.player = card.Owner')) {
        if (-not $feralBlock.Contains($requiredFeralRule)) {
            $violations.Add("${resultLocationMirrorPath}: missing 0.107.1 Feral rule '$requiredFeralRule'")
        }
    }
    if ($feralBlock.Contains('card.IsDupe')) {
        $violations.Add("${resultLocationMirrorPath}: v0.108 Feral dupe exclusion returned")
    }
}


$historyCoursePath = Join-Path $repositoryRoot 'src/Search/SimulatedCombatState.AutoPlay.cs'
$historyCourseText = [IO.File]::ReadAllText($historyCoursePath)
$historyRecordStart = $historyCourseText.IndexOf('private void RecordHistoryCourseAttack')
$historyRecordEnd = $historyCourseText.IndexOf('public void CommitHistoryCourseTurn', $historyRecordStart)
if ($historyRecordStart -lt 0 -or $historyRecordEnd -le $historyRecordStart) {
    $violations.Add("${historyCoursePath}: History Course simulated-turn tracking boundary is missing")
}
else {
    $historyRecordBlock = $historyCourseText.Substring(
        $historyRecordStart,
        $historyRecordEnd - $historyRecordStart)
    if (-not $historyRecordBlock.Contains(
        'card.Preview.Type is not (CardType.Attack or CardType.Skill)')) {
        $violations.Add("${historyCoursePath}: 0.107.1 History Course must track both Attacks and Skills")
    }
    if ($historyRecordBlock.Contains('card.Preview.Type != CardType.Attack')) {
        $violations.Add("${historyCoursePath}: v0.109 Attack-only History Course tracking returned")
    }
    if (-not $historyRecordBlock.Contains('card.Preview.IsDupe')) {
        $violations.Add("${historyCoursePath}: History Course must still exclude gameplay dupes")
    }
}

$historyRootStart = $historyCourseText.IndexOf('private PredictedCard? GetPreviousTurnAttack')
$historyRootEnd = $historyCourseText.IndexOf('private Dictionary<Player, PredictedCard>? ForkHistoryCourseCards', $historyRootStart)
if ($historyRootStart -lt 0 -or $historyRootEnd -le $historyRootStart) {
    $violations.Add("${historyCoursePath}: History Course live-root lookup boundary is missing")
}
else {
    $historyRootBlock = $historyCourseText.Substring(
        $historyRootStart,
        $historyRootEnd - $historyRootStart)
    if (-not $historyRootBlock.Contains(
        '(entry.CardPlay.Card.Type is CardType.Attack or CardType.Skill)')) {
        $violations.Add("${historyCoursePath}: 0.107.1 live History Course lookup must accept Attacks and Skills")
    }
    if ($historyRootBlock.Contains('entry.CardPlay.Card.Type == CardType.Attack')) {
        $violations.Add("${historyCoursePath}: v0.109 Attack-only live History Course lookup returned")
    }
    if (-not $historyRootBlock.Contains('!entry.CardPlay.Card.IsDupe')) {
        $violations.Add("${historyCoursePath}: live History Course lookup must exclude dupes")
    }
}

$cardOnPlayMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs'
$cardOnPlayMirrorText = [IO.File]::ReadAllText($cardOnPlayMirrorPath)
if (-not $cardOnPlayMirrorText.Contains('registry.Register<Scare>(static (_, _) => { });')) {
    $violations.Add("${cardOnPlayMirrorPath}: 0.107.1 Scare must remain explicitly mirrorable")
}
$scareCardEffectSpecPath = Join-Path $repositoryRoot 'src/Prediction/CardEffectSpecRegistry.cs'
$scareCardEffectSpecText = [IO.File]::ReadAllText($scareCardEffectSpecPath)
if (-not $scareCardEffectSpecText.Contains('[typeof(Scare)] = [AllEnemies<WeakPower>(_ => 1)]')) {
    $violations.Add("${scareCardEffectSpecPath}: 0.107.1 Scare must apply 1 Weak to every hittable enemy")
}

$bigBangSpecPath = Join-Path $repositoryRoot 'src/Prediction/CardEffectSpecRegistry.cs'
$bigBangSpecText = [IO.File]::ReadAllText($bigBangSpecPath)
$bigBangStart = $bigBangSpecText.IndexOf('case BigBang:')
$bigBangEnd = $bigBangSpecText.IndexOf('case BloodWall or Breakthrough or Hemokinesis:', $bigBangStart)
if ($bigBangStart -lt 0 -or $bigBangEnd -le $bigBangStart) {
    $violations.Add("${bigBangSpecPath}: Big Bang 0.107.1 effect boundary is missing")
}
else {
    $bigBangBlock = $bigBangSpecText.Substring($bigBangStart, $bigBangEnd - $bigBangStart)
    $bigBangStars = $bigBangBlock.IndexOf('simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue)')
    $bigBangEnergy = $bigBangBlock.IndexOf('simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue)')
    $bigBangForge = $bigBangBlock.IndexOf('PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue)')
    if ($bigBangStars -lt 0 -or $bigBangEnergy -le $bigBangStars -or $bigBangForge -le $bigBangEnergy) {
        $violations.Add("${bigBangSpecPath}: 0.107.1 Big Bang order must remain Stars -> Energy -> Forge")
    }
}

$shiningStrikeSpecPath = Join-Path $repositoryRoot 'src/Prediction/CardEffectSpecRegistry.cs'
$shiningStrikeSpecText = [IO.File]::ReadAllText($shiningStrikeSpecPath)
$shiningStrikeStart = $shiningStrikeSpecText.IndexOf('case ShiningStrike:')
$shiningStrikeEnd = $shiningStrikeSpecText.IndexOf('case SolarStrike:', $shiningStrikeStart)
if ($shiningStrikeStart -lt 0 -or $shiningStrikeEnd -le $shiningStrikeStart) {
    $violations.Add("${shiningStrikeSpecPath}: Shining Strike 0.107.1 effect boundary is missing")
}
else {
    $shiningStrikeBlock = $shiningStrikeSpecText.Substring(
        $shiningStrikeStart,
        $shiningStrikeEnd - $shiningStrikeStart)
    foreach ($requiredShiningStrikeRule in @(
        'simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue)',
        'new ShiningStrikeExecutionFrame(playedCard)',
        'ReturnShiningStrikeToDrawPile(simulator, playedCard)')) {
        if (-not $shiningStrikeBlock.Contains($requiredShiningStrikeRule)) {
            $violations.Add("${shiningStrikeSpecPath}: missing 0.107.1 Shining Strike rule '$requiredShiningStrikeRule'")
        }
    }
}
$shiningStrikeHelperStart = $shiningStrikeSpecText.IndexOf('private static void ReturnShiningStrikeToDrawPile')
$shiningStrikeHelperEnd = $shiningStrikeSpecText.IndexOf('private static void AddFixed<TCard>', $shiningStrikeHelperStart)
if ($shiningStrikeHelperStart -lt 0 -or $shiningStrikeHelperEnd -le $shiningStrikeHelperStart) {
    $violations.Add("${shiningStrikeSpecPath}: Shining Strike Draw-top helper boundary is missing")
}
else {
    $shiningStrikeHelper = $shiningStrikeSpecText.Substring(
        $shiningStrikeHelperStart,
        $shiningStrikeHelperEnd - $shiningStrikeHelperStart)
    foreach ($requiredShiningStrikeHelperRule in @(
        'HasKeyword(simulator.State, CardKeyword.Exhaust)',
        '!playedCard.Preview.ExhaustOnNextPlay',
        'AddToPile(playedCard, PileType.Draw, CardPilePosition.Top)',
        'context.RequireRemap(Card)')) {
        if (-not $shiningStrikeHelper.Contains($requiredShiningStrikeHelperRule)) {
            $violations.Add("${shiningStrikeSpecPath}: missing Shining Strike helper rule '$requiredShiningStrikeHelperRule'")
        }
    }
    if ($shiningStrikeHelper.Contains('IsDupe')) {
        $violations.Add("${shiningStrikeSpecPath}: v0.108 Shining Strike dupe exclusion returned")
    }
}

$generatedCardHookPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardGeneratedForCombatMirrors.cs'
$generatedCardHookText = [IO.File]::ReadAllText($generatedCardHookPath)
$regaliteStart = $generatedCardHookText.IndexOf('private static void HandleRegalite')
$regaliteEnd = $generatedCardHookText.IndexOf('#if !STS2_01071', $regaliteStart)
if ($regaliteStart -lt 0 -or $regaliteEnd -le $regaliteStart) {
    $violations.Add("${generatedCardHookPath}: Regalite generated-card hook boundary is missing")
}
else {
    $regaliteBlock = $generatedCardHookText.Substring($regaliteStart, $regaliteEnd - $regaliteStart)
    if (-not $regaliteBlock.Contains('context.Creator == relic.Owner') -or
        -not $regaliteBlock.Contains('GainBlock(relic.Owner.Creature, relic.DynamicVars.Block)')) {
        $violations.Add("${generatedCardHookPath}: 0.107.1 Regalite must grant Block for every owner-created card")
    }
    if ($regaliteBlock.Contains('UsedThisTurn') -or
        $regaliteBlock.Contains('RegalitePredictionState')) {
        $violations.Add("${generatedCardHookPath}: v0.110 once-per-turn Regalite behavior returned")
    }
}
$relicPredictionStatePath = Join-Path $repositoryRoot 'src/Prediction/RelicPredictionStateSupport.cs'
$relicPredictionStateText = [IO.File]::ReadAllText($relicPredictionStatePath)
if ($relicPredictionStateText.Contains('RegalitePredictionState')) {
    $violations.Add("${relicPredictionStatePath}: 0.107.1 Regalite has no per-turn prediction state")
}

$batch043Path = Join-Path $repositoryRoot 'src/Prediction/CardOnPlaySupport.Batch043.cs'
$batch043Text = [IO.File]::ReadAllText($batch043Path)
$eidolonStart = $batch043Text.IndexOf('private static void ApplyEidolon(')
$eidolonEnd = $batch043Text.IndexOf('private static void ApplyKnifeTrap(', $eidolonStart)
if ($eidolonStart -lt 0 -or $eidolonEnd -le $eidolonStart) {
    $violations.Add("${batch043Path}: Eidolon helper boundary is missing")
}
else {
    $eidolonBlock = $batch043Text.Substring($eidolonStart, $eidolonEnd - $eidolonStart)
    foreach ($requiredEidolonRule in @(
        '.Hand.Cards',
        'simulator.AcknowledgeExecutionDispatch()',
        'ContinueEidolon(',
        'simulator.Exhaust(cards[index])',
        'exhaustedCount++',
        'simulator.AppendExecutionContinuation(',
        'new EidolonExecutionFrame(',
        'exhaustedCount >= 9',
        'combat.Apply<IntangiblePower>',
        'PlayedCard = context.RequireRemap(PlayedCard)',
        'Cards = Cards.Select(card => context.RequireRemap(card)).ToArray()')) {
        if (-not $eidolonBlock.Contains($requiredEidolonRule)) {
            $violations.Add("${batch043Path}: missing 0.107.1 Eidolon rule '$requiredEidolonRule'")
        }
    }
    $eidolonExhaustIndex = $eidolonBlock.IndexOf('simulator.Exhaust(cards[index])')
    $eidolonCountIndex = $eidolonBlock.IndexOf('exhaustedCount++', $eidolonExhaustIndex)
    $eidolonPendingIndex = $eidolonBlock.IndexOf('if (simulator.HasPendingChoice)', $eidolonCountIndex)
    if ($eidolonExhaustIndex -lt 0 -or
        $eidolonCountIndex -le $eidolonExhaustIndex -or
        $eidolonPendingIndex -le $eidolonCountIndex) {
        $violations.Add("${batch043Path}: Eidolon must carry the completed Exhaust into its continuation count before suspending")
    }
    if (-not $batch043Text.Contains('ApplyEidolon(simulator, combat, playedCard);')) {
        $violations.Add("${batch043Path}: Eidolon continuation must retain the prediction-owned played card")
    }
    if ($eidolonBlock.Contains('.ExhaustPile.Cards') -or
        $eidolonBlock.Contains('CardExecutionSupport.AutoPlay') -or
        $eidolonBlock.Contains('CardKeyword.Ethereal')) {
        $violations.Add("${batch043Path}: v0.109 Eidolon exhaust-pile autoplay behavior returned")
    }
}

$outbreakCardPath = Join-Path $repositoryRoot 'src/Prediction/CardOnPlaySupport.Batch042.cs'
$outbreakCardText = [IO.File]::ReadAllText($outbreakCardPath)
$outbreakStart = $outbreakCardText.IndexOf('case Outbreak:')
$outbreakEnd = $outbreakCardText.IndexOf('case PrimalForce:', $outbreakStart)
if ($outbreakStart -lt 0 -or $outbreakEnd -le $outbreakStart) {
    $violations.Add("${outbreakCardPath}: Outbreak card boundary is missing")
}
else {
    $outbreakBlock = $outbreakCardText.Substring($outbreakStart, $outbreakEnd - $outbreakStart)
    if ((-not $outbreakBlock.Contains('combat.Apply<OutbreakPower>(')) -or
        (-not $outbreakBlock.Contains('card.DynamicVars["OutbreakPower"].IntValue'))) {
        $violations.Add("${outbreakCardPath}: 0.107.1 Outbreak must apply OutbreakPower from its dynamic var")
    }
    if ($outbreakBlock.Contains('ApplyOutbreak(')) {
        $violations.Add("${outbreakCardPath}: later-version immediate Outbreak poison settlement returned")
    }
}
if ($outbreakCardText.Contains('private static void ApplyOutbreak(')) {
    $violations.Add("${outbreakCardPath}: retired later-version Outbreak helper returned")
}

$powerLifecyclePath = Join-Path $repositoryRoot 'src/Prediction/PowerLifecycleSupport.cs'
$powerLifecycleText = [IO.File]::ReadAllText($powerLifecyclePath)
foreach ($requiredOutbreakLifecycle in @(
    'case OutbreakPower outbreak when change.Delta > 0',
    'change.Power is PoisonPower',
    'PowerPredictionStateSupport.RecordOutbreakPoisonApplication(simulator, outbreak)',
    'combat.GetAmount<OutbreakPower>(outbreak.Owner)',
    'ValueProp.Unpowered')) {
    if (-not $powerLifecycleText.Contains($requiredOutbreakLifecycle)) {
        $violations.Add("${powerLifecyclePath}: missing 0.107.1 Outbreak lifecycle '$requiredOutbreakLifecycle'")
    }
}

$powerPredictionStatePath = Join-Path $repositoryRoot 'src/Prediction/PowerPredictionStateSupport.cs'
$powerPredictionStateText = [IO.File]::ReadAllText($powerPredictionStatePath)
foreach ($requiredOutbreakState in @(
    'NativeOutbreakPoisonApplications',
    'OutbreakPoisonApplications',
    'RecordOutbreakPoisonApplication',
    'OutbreakPower.poisonThreshold',
    'case (OutbreakPower value, OutbreakPower original)')) {
    if (-not $powerPredictionStateText.Contains($requiredOutbreakState)) {
        $violations.Add("${powerPredictionStatePath}: missing Outbreak hidden-state rule '$requiredOutbreakState'")
    }
}

$simulatedCombatPath = Join-Path $repositoryRoot 'src/Search/SimulatedCombatState.cs'
if (-not (Select-String -LiteralPath $simulatedCombatPath -SimpleMatch 'PowerPredictionStateSupport.OutbreakPoisonApplications(simulator, outbreak)' -Quiet)) {
    $violations.Add("${simulatedCombatPath}: Outbreak hidden counter must participate in the search fingerprint")
}
$continuationStampPath = Join-Path $repositoryRoot 'src/Runtime/ContinuationStamp.cs'
$continuationStampText = [IO.File]::ReadAllText($continuationStampPath)
if ((-not $continuationStampText.Contains('PoisonApplications=')) -or
    (-not $continuationStampText.Contains('NativeOutbreakPoisonApplications(outbreak)')) -or
    (-not $continuationStampText.Contains('OutbreakPoisonApplications(simulator, outbreak)'))) {
    $violations.Add("${continuationStampPath}: Outbreak hidden counter must participate in exact continuation stamps")
}


$cardEffectSpecPath = Join-Path $repositoryRoot 'src/Prediction/CardEffectSpecRegistry.cs'
$cardEffectSpecText = [IO.File]::ReadAllText($cardEffectSpecPath)
if ($cardEffectSpecText.Contains('[typeof(GuidingStar)] = [Owner<DrawCardsNextTurnPower>')) {
    $violations.Add("${cardEffectSpecPath}: 0.107.1 Guiding Star draws immediately; deferred draw power returned")
}
$fightThroughStart = $cardEffectSpecText.IndexOf('case FightThrough:')
$fightThroughEnd = $cardEffectSpecText.IndexOf('case GraveWarden:', $fightThroughStart)
if ($fightThroughStart -lt 0 -or $fightThroughEnd -le $fightThroughStart) {
    $violations.Add("${cardEffectSpecPath}: Fight Through generation boundary is missing")
}
else {
    $fightThroughBlock = $cardEffectSpecText.Substring($fightThroughStart, $fightThroughEnd - $fightThroughStart)
    if (-not $fightThroughBlock.Contains('AddFixed<Wound>(simulator, card, PileType.Discard, 2)')) {
        $violations.Add("${cardEffectSpecPath}: 0.107.1 Fight Through must generate exactly two Wounds")
    }
    if ($fightThroughBlock.Contains('PileType.Discard, 1')) {
        $violations.Add("${cardEffectSpecPath}: incorrect one-Wound Fight Through behavior returned")
    }
}


$cardPowerSupportPath = Join-Path $repositoryRoot 'src/Prediction/CardPowerOnPlaySupport.SToZ.cs'
$cardPowerSupportText = [IO.File]::ReadAllText($cardPowerSupportPath)
$wellLaidPlansStart = $cardPowerSupportText.IndexOf('case WellLaidPlans:')
if ($wellLaidPlansStart -lt 0) {
    $violations.Add("${cardPowerSupportPath}: Well-Laid Plans boundary is missing")
}
else {
    $wellLaidPlansBlock = $cardPowerSupportText.Substring(
        $wellLaidPlansStart,
        [Math]::Min(320, $cardPowerSupportText.Length - $wellLaidPlansStart))
    if (-not $wellLaidPlansBlock.Contains('card.DynamicVars["RetainAmount"].IntValue')) {
        $violations.Add("${cardPowerSupportPath}: 0.107.1 Well-Laid Plans must use RetainAmount")
    }
    if ($wellLaidPlansBlock.Contains('WellLaidPlansPower>(owner, 1, owner)')) {
        $violations.Add("${cardPowerSupportPath}: fixed one-stack Well-Laid Plans behavior returned")
    }
}


$calculatedVarSpecPath = Join-Path $repositoryRoot 'src/Prediction/CalculatedVarSpecRegistry.cs'
$calculatedVarSpecText = [IO.File]::ReadAllText($calculatedVarSpecPath)
$sacrificeCalculatedStart = $calculatedVarSpecText.IndexOf('Sacrifice =>')
$sacrificeCalculatedEnd = $calculatedVarSpecText.IndexOf('TimesUp =>', $sacrificeCalculatedStart)
if ($sacrificeCalculatedStart -lt 0 -or $sacrificeCalculatedEnd -le $sacrificeCalculatedStart) {
    $violations.Add("${calculatedVarSpecPath}: Sacrifice calculated-var boundary is missing")
}
else {
    $sacrificeCalculatedBlock = $calculatedVarSpecText.Substring(
        $sacrificeCalculatedStart,
        $sacrificeCalculatedEnd - $sacrificeCalculatedStart)
    if (-not $sacrificeCalculatedBlock.Contains('GetOstyMaxHp(simulator, model.Owner) * 2')) {
        $violations.Add("${calculatedVarSpecPath}: 0.107.1 Sacrifice calculated block must use Osty max HP x2")
    }
    if ($sacrificeCalculatedBlock.Contains('GetOstyMaxHp(simulator, model.Owner) * 3')) {
        $violations.Add("${calculatedVarSpecPath}: later-version Sacrifice x3 calculated block returned")
    }
}

$expectFightStart = $calculatedVarSpecText.IndexOf('ExpectAFight =>')
$expectFightEnd = $calculatedVarSpecText.IndexOf('HelixDrill =>', $expectFightStart)
if ($expectFightStart -lt 0 -or $expectFightEnd -le $expectFightStart) {
    $violations.Add("${calculatedVarSpecPath}: Expect a Fight calculated-var boundary is missing")
}
else {
    $expectFightCalculatedBlock = $calculatedVarSpecText.Substring(
        $expectFightStart,
        $expectFightEnd - $expectFightStart)
    if (-not $expectFightCalculatedBlock.Contains('candidate.Preview.Type == CardType.Attack')) {
        $violations.Add("${calculatedVarSpecPath}: 0.107.1 Expect a Fight energy must count Attack cards in hand")
    }
    if ($expectFightCalculatedBlock.Contains('StrengthPower')) {
        $violations.Add("${calculatedVarSpecPath}: incorrect Strength-based Expect a Fight calculation returned")
    }
}

$cardOnPlayRegistryPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs'
if (-not (Select-String -LiteralPath $cardOnPlayRegistryPath -SimpleMatch 'registry.Register<ExpectAFight>(BespokeCardMirrors.ExpectAFightOnPlay);' -Quiet)) {
    $violations.Add("${cardOnPlayRegistryPath}: 0.107.1 Expect a Fight requires its dedicated OnPlay mirror")
}
$bespokeOnPlayPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/BespokeCardMirrors.cs'
$bespokeOnPlayText = [IO.File]::ReadAllText($bespokeOnPlayPath)
$expectFightMirrorStart = $bespokeOnPlayText.IndexOf('public static void ExpectAFightOnPlay')
$expectFightMirrorEnd = $bespokeOnPlayText.IndexOf('public static void TwinStrikeOnPlay', $expectFightMirrorStart)
if ($expectFightMirrorStart -lt 0 -or $expectFightMirrorEnd -le $expectFightMirrorStart) {
    $violations.Add("${bespokeOnPlayPath}: Expect a Fight mirror boundary is missing")
}
else {
    $expectFightMirrorBlock = $bespokeOnPlayText.Substring(
        $expectFightMirrorStart,
        $expectFightMirrorEnd - $expectFightMirrorStart)
    if ((-not $expectFightMirrorBlock.Contains('context.Simulator.GainEnergy(')) -or
        (-not $expectFightMirrorBlock.Contains('card.DynamicVars["CalculatedEnergy"]'))) {
        $violations.Add("${bespokeOnPlayPath}: Expect a Fight must gain its calculated energy before the post-mirror power effect")
    }
}
if (-not $cardEffectSpecText.Contains('[typeof(ExpectAFight)] = [Owner<NoEnergyGainPower>(_ => 1)]')) {
    $violations.Add("${cardEffectSpecPath}: 0.107.1 Expect a Fight must apply one NoEnergyGainPower after gaining energy")
}


$hyperbeamStart = $cardEffectSpecText.IndexOf('#if STS2_01071', $cardEffectSpecText.IndexOf('[typeof(Hegemony)]'))
$hyperbeamEnd = $cardEffectSpecText.IndexOf('#else', $hyperbeamStart)
if ($hyperbeamStart -lt 0 -or $hyperbeamEnd -le $hyperbeamStart) {
    $violations.Add("${cardEffectSpecPath}: Hyperbeam 0.107.1 compatibility boundary is missing")
}
else {
    $hyperbeam01071Block = $cardEffectSpecText.Substring($hyperbeamStart, $hyperbeamEnd - $hyperbeamStart)
    if (-not $hyperbeam01071Block.Contains('[typeof(Hyperbeam)] = [Owner<FocusPower>(card => -card.DynamicVars["FocusPower"].IntValue)]')) {
        $violations.Add("${cardEffectSpecPath}: 0.107.1 Hyperbeam must apply negative FocusPower after its attack")
    }
    if ($hyperbeam01071Block.Contains('[typeof(Hyperbeam)] = []')) {
        $violations.Add("${cardEffectSpecPath}: empty 0.107.1 Hyperbeam effect returned")
    }
}


$cardDrawMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardDrawCardMirrors.cs'
$cardDrawMirrorText = [IO.File]::ReadAllText($cardDrawMirrorPath)
$expertiseStart = $cardDrawMirrorText.IndexOf('public static void ExpertiseOnPlay')
$expertiseEnd = $cardDrawMirrorText.IndexOf('public static void FetchOnPlay', $expertiseStart)
if ($expertiseStart -lt 0 -or $expertiseEnd -le $expertiseStart) {
    $violations.Add("${cardDrawMirrorPath}: Expertise mirror boundary is missing")
}
else {
    $expertiseBlock = $cardDrawMirrorText.Substring($expertiseStart, $expertiseEnd - $expertiseStart)
    if (-not $expertiseBlock.Contains('card.DynamicVars.Cards.BaseValue - context.OwnerState.Hand.Cards.Count')) {
        $violations.Add("${cardDrawMirrorPath}: 0.107.1 Expertise must draw only enough cards to reach its Cards hand-size target")
    }
    if ($expertiseBlock.Contains('GiveSingleTurnRetain')) {
        $violations.Add("${cardDrawMirrorPath}: later-version Expertise retain behavior returned")
    }
    if ($expertiseBlock.Contains('Draw(card.Owner, card.DynamicVars.Cards.IntValue)')) {
        $violations.Add("${cardDrawMirrorPath}: fixed-count Expertise draw returned")
    }
}


$scrapeStart = $cardDrawMirrorText.IndexOf('public static void ScrapeOnPlay')
$scrapeEnd = $cardDrawMirrorText.IndexOf('public static void ScrawlOnPlay', $scrapeStart)
if ($scrapeStart -lt 0 -or $scrapeEnd -le $scrapeStart) {
    $violations.Add("${cardDrawMirrorPath}: Scrape mirror boundary is missing")
}
else {
    $scrapeBlock = $cardDrawMirrorText.Substring($scrapeStart, $scrapeEnd - $scrapeStart)
    if (-not $scrapeBlock.Contains('ContinueCardDrawSequence(context, CardDrawSequence.Scrape)')) {
        $violations.Add("${cardDrawMirrorPath}: 0.107.1 Scrape must enter its resumable attack -> draw -> discard sequence")
    }
}
$cardDrawContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardDrawCardMirrors.ExecutionContinuation.cs'
$cardDrawContinuationText = [IO.File]::ReadAllText($cardDrawContinuationPath)
$scrapeContinuationStart = $cardDrawContinuationText.IndexOf('case CardDrawSequence.Scrape:')
$scrapeContinuationEnd = $cardDrawContinuationText.IndexOf('default:', $scrapeContinuationStart)
if ($scrapeContinuationStart -lt 0 -or $scrapeContinuationEnd -le $scrapeContinuationStart) {
    $violations.Add("${cardDrawContinuationPath}: Scrape continuation boundary is missing")
}
else {
    $scrapeContinuationBlock = $cardDrawContinuationText.Substring(
        $scrapeContinuationStart,
        $scrapeContinuationEnd - $scrapeContinuationStart)
    if (-not $scrapeContinuationBlock.Contains('EnergyCost.GetWithModifiers(CostModifiers.Local) != 0')) {
        $violations.Add("${cardDrawContinuationPath}: 0.107.1 Scrape must test the drawn card's local Energy cost only")
    }
    if (-not $scrapeContinuationBlock.Contains('EnergyCost.CostsX')) {
        $violations.Add("${cardDrawContinuationPath}: 0.107.1 Scrape must still discard X-cost cards")
    }
    if ($scrapeContinuationBlock.Contains('GetEnergyCostValueWithModifiers(context.Simulator)')) {
        $violations.Add("${cardDrawContinuationPath}: later-version/global-cost Scrape behavior returned")
    }
}

$orbCardMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/OrbCardMirrors.cs'
$orbCardMirrorText = [IO.File]::ReadAllText($orbCardMirrorPath)
$orbCardContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/OrbCardMirrors.ExecutionContinuation.cs'
$orbCardContinuationText = [IO.File]::ReadAllText($orbCardContinuationPath)
$nullStart = $orbCardMirrorText.IndexOf('public static void NullOnPlay')
$nullEnd = $orbCardMirrorText.IndexOf('public static void QuadcastOnPlay', $nullStart)
if ($nullStart -lt 0 -or $nullEnd -le $nullStart) {
    $violations.Add("${orbCardMirrorPath}: Null mirror boundary is missing")
}
else {
    $nullBlock = $orbCardMirrorText.Substring($nullStart, $nullEnd - $nullStart)
    $attackIndex = $nullBlock.IndexOf('context.AttackSingle()')
    $tailIndex = $nullBlock.IndexOf('ContinueOrQueueTail(context, OrbCardTailKind.NullWeakThenDark)')
    if ($attackIndex -lt 0 -or $tailIndex -le $attackIndex) {
        $violations.Add("${orbCardMirrorPath}: 0.107.1 Null must enter its Weak -> Dark Orb continuation after the attack")
    }
    foreach ($requiredNullTailRule in @(
        'case OrbCardTailKind.NullWeakThenDark:',
        'combat.Apply<WeakPower>(',
        'card.DynamicVars.Weak.IntValue',
        'ContinueOrQueueTail(context, OrbCardTailKind.NullDark)',
        'case OrbCardTailKind.NullDark:',
        'simulator.OrbChannel<DarkOrb>(playedCard.Preview.Owner)')) {
        if (-not $orbCardContinuationText.Contains($requiredNullTailRule)) {
            $violations.Add("${orbCardContinuationPath}: missing 0.107.1 Null continuation rule '$requiredNullTailRule'")
        }
    }
}
$flankingSpec = '[typeof(Flanking)] = [Target<FlankingPower>(_ => 2)]'
if (-not $cardEffectSpecText.Contains($flankingSpec)) {
    $violations.Add("${cardEffectSpecPath}: 0.107.1 Flanking must apply an instanced FlankingPower amount 2 to its target")
}
if (-not $damageMirrorText.Contains('registry.Register<FlankingPower>(HandleFlankingPower);')) {
    $violations.Add("${damageMirrorPath}: FlankingPower damage multiplier mirror is missing")
}
$flankingDamageStart = $damageMirrorText.IndexOf('private static decimal HandleFlankingPower')
$flankingDamageEnd = $damageMirrorText.IndexOf('private static decimal HandleFlutterPower', $flankingDamageStart)
if ($flankingDamageStart -lt 0 -or $flankingDamageEnd -le $flankingDamageStart) {
    $violations.Add("${damageMirrorPath}: FlankingPower multiplier handler boundary is missing")
}
else {
    $flankingDamageBlock = $damageMirrorText.Substring($flankingDamageStart, $flankingDamageEnd - $flankingDamageStart)
    foreach ($requiredFlankingDamage in @(
        'context.Target != power.Owner',
        '!context.Props.IsPoweredAttack()',
        'context.Dealer == power.Applier',
        'return power.Amount;')) {
        if (-not $flankingDamageBlock.Contains($requiredFlankingDamage)) {
            $violations.Add("${damageMirrorPath}: missing 0.107.1 Flanking damage rule '$requiredFlankingDamage'")
        }
    }
}
$simulatedCombatText = [IO.File]::ReadAllText($simulatedCombatPath)
if ((-not $simulatedCombatText.Contains('simulated is FlankingPower flanking && applier != null')) -or
    (-not $simulatedCombatText.Contains('flanking.DynamicVars["Applier"]'))) {
    $violations.Add("${simulatedCombatPath}: Flanking application must preserve the native applier display state")
}
$corePowerSupportPath = Join-Path $repositoryRoot 'src/Prediction/CorePowerSupport.cs'
$corePowerSupportText = [IO.File]::ReadAllText($corePowerSupportPath)
foreach ($requiredFlankingExpiry in @(
    '.OfType<FlankingPower>()',
    'participantSet.Contains(power.Owner)',
    'StateStore.GetPowerAmount(flanking).Consume()',
    'combat.SetPowerAmount(flanking, 0)')) {
    if (-not $corePowerSupportText.Contains($requiredFlankingExpiry)) {
        $violations.Add("${corePowerSupportPath}: missing 0.107.1 Flanking end-of-side removal '$requiredFlankingExpiry'")
    }
}


$coordinateSpec = '[typeof(Coordinate)] = [Target<CoordinatePower>(card => card.DynamicVars.Strength.IntValue)]'
if (-not $cardEffectSpecText.Contains($coordinateSpec)) {
    $violations.Add("${cardEffectSpecPath}: 0.107.1 Coordinate must apply its Strength amount as CoordinatePower to the target")
}
$coordinateApplyStart = $cardEffectSpecText.IndexOf('private static void ApplyPower(')
$coordinateApplyEnd = $cardEffectSpecText.IndexOf('private static CardPowerEffect Owner<', $coordinateApplyStart)
if ($coordinateApplyStart -lt 0 -or $coordinateApplyEnd -le $coordinateApplyStart) {
    $violations.Add("${cardEffectSpecPath}: shared card Power application boundary is missing")
}
else {
    $coordinateApplyBlock = $cardEffectSpecText.Substring($coordinateApplyStart, $coordinateApplyEnd - $coordinateApplyStart)
    if (-not $coordinateApplyBlock.Contains('combat.ApplyTemporaryStrengthGain<CoordinatePower>(target, amount, applier)')) {
        $violations.Add("${cardEffectSpecPath}: CoordinatePower must use temporary Strength gain semantics")
    }
}


if (-not $cardEffectSpecText.Contains('typeof(Rampage), typeof(Tank), typeof(Whistle)')) {
    $violations.Add("${cardEffectSpecPath}: Tank must remain in the explicit 0.107.1 card-effect catalog")
}
$tankStart = $cardEffectSpecText.IndexOf('case Tank:')
$tankEnd = $cardEffectSpecText.IndexOf('case Rampage rampage:', $tankStart)
if ($tankStart -lt 0 -or $tankEnd -le $tankStart) {
    $violations.Add("${cardEffectSpecPath}: Tank card-effect boundary is missing")
}
else {
    $tankBlock = $cardEffectSpecText.Substring($tankStart, $tankEnd - $tankStart)
    foreach ($requiredTankRule in @(
        'combat.Apply<TankPower>(ownerCreature, 1, ownerCreature)',
        'combat.GetTeammatesOf(ownerCreature)',
        'simulator.State.GetCreature(creature).IsAlive',
        'creature.IsPlayer',
        '!ReferenceEquals(creature, ownerCreature)',
        'combat.Apply<GuardedPower>(teammate, 1, ownerCreature)')) {
        if (-not $tankBlock.Contains($requiredTankRule)) {
            $violations.Add("${cardEffectSpecPath}: missing 0.107.1 Tank rule '$requiredTankRule'")
        }
    }
}
if ((-not $simulatedCombatText.Contains('simulated is GuardedPower guarded && applier != null')) -or
    (-not $simulatedCombatText.Contains('guarded.DynamicVars["Applier"]'))) {
    $violations.Add("${simulatedCombatPath}: Guarded application must preserve the native applier display state")
}
foreach ($requiredGuardedCleanup in @(
    '.OfType<GuardedPower>()',
    'ReferenceEquals(power.Applier, dead)',
    'StateStore.GetPowerAmount(guarded).Consume()',
    'combat.SetPowerAmount(guarded, 0)')) {
    if (-not $corePowerSupportText.Contains($requiredGuardedCleanup)) {
        $violations.Add("${corePowerSupportPath}: missing Guarded cleanup after Tank owner death '$requiredGuardedCleanup'")
    }
}


$beaconSpec = '[typeof(BeaconOfHope)] = [Owner<BeaconOfHopePower>(_ => 1)]'
if (-not $cardEffectSpecText.Contains($beaconSpec)) {
    $violations.Add("${cardEffectSpecPath}: 0.107.1 Beacon of Hope must apply one BeaconOfHopePower to its owner")
}
$afterBlockGainedPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Block/AfterBlockGainedMirrors.cs'
$afterBlockGainedText = [IO.File]::ReadAllText($afterBlockGainedPath)
foreach ($requiredBeaconRule in @(
    'registry.Register<BeaconOfHopePower>(HandleBeaconOfHopePower);',
    'context.Amount * 0.5m',
    'context.State.GetCreature(creature).IsAlive',
    'state.HasAlreadyBeenGivenBlock = true',
    'ValueProp.Unpowered')) {
    if (-not $afterBlockGainedText.Contains($requiredBeaconRule)) {
        $violations.Add("${afterBlockGainedPath}: missing 0.107.1 Beacon of Hope rule '$requiredBeaconRule'")
    }
}


$hammerTimeSpec = '[typeof(HammerTime)] = [Owner<HammerTimePower>(_ => 1)]'
if (-not $cardEffectSpecText.Contains($hammerTimeSpec)) {
    $violations.Add("${cardEffectSpecPath}: 0.107.1 Hammer Time must apply one HammerTimePower")
}
$persistentPowerSupportPath = Join-Path $repositoryRoot 'src/Prediction/PersistentPowerSupport.cs'
$persistentPowerSupportText = [IO.File]::ReadAllText($persistentPowerSupportPath)
foreach ($requiredHammerTimeForgeRule in @(
    'AbstractModel? source = null',
    'if (source is HammerTimePower)',
    'hammerTime = combat.GetPower<HammerTimePower>(player.Creature)',
    'combat.GetAmount<HammerTimePower>(player.Creature) <= 0',
    'simulator.State.Players.ToArray()',
    '!simulator.State.GetCreature(teammate.Creature).IsAlive',
    'Forge(simulator, teammate, amount, hammerTime)',
    'ForgeExecutionStage.HammerTimePlayers')) {
    if (-not $persistentPowerSupportText.Contains($requiredHammerTimeForgeRule)) {
        $violations.Add("${persistentPowerSupportPath}: missing 0.107.1 Hammer Time Forge rule '$requiredHammerTimeForgeRule'")
    }
}


$cardOnPlayRegistryText = [IO.File]::ReadAllText($cardOnPlayRegistryPath)
if (-not $cardOnPlayRegistryText.Contains('registry.Register<Intercept>(BespokeCardMirrors.InterceptOnPlay);')) {
    $violations.Add("${cardOnPlayRegistryPath}: 0.107.1 Intercept requires its dedicated native-order OnPlay mirror")
}
$cardOnPlayCatalogPath = Join-Path $repositoryRoot 'src/Prediction/CardOnPlayCompensationCatalog.cs'
$cardOnPlayCatalogText = [IO.File]::ReadAllText($cardOnPlayCatalogPath)
if (-not $cardOnPlayCatalogText.Contains('typeof(Bolas), typeof(Intercept), typeof(RightHandHand)')) {
    $violations.Add("${cardOnPlayCatalogPath}: Intercept must remain in the compensated OnPlay catalog")
}
$interceptMirrorStart = $bespokeOnPlayText.IndexOf('public static void InterceptOnPlay')
$interceptMirrorEnd = $bespokeOnPlayText.IndexOf('public static void TwinStrikeOnPlay', $interceptMirrorStart)
if ($interceptMirrorStart -lt 0 -or $interceptMirrorEnd -le $interceptMirrorStart) {
    $violations.Add("${bespokeOnPlayPath}: Intercept mirror boundary is missing")
}
else {
    $interceptMirrorBlock = $bespokeOnPlayText.Substring($interceptMirrorStart, $interceptMirrorEnd - $interceptMirrorStart)
    foreach ($requiredInterceptOnPlay in @(
        'context.GainBlock(card.Owner.Creature);',
        'ContinueOrQueueTail(context, BespokeTailKind.InterceptCoverage);')) {
        if (-not $interceptMirrorBlock.Contains($requiredInterceptOnPlay)) {
            $violations.Add("${bespokeOnPlayPath}: missing 0.107.1 Intercept OnPlay rule '$requiredInterceptOnPlay'")
        }
    }
    foreach ($requiredInterceptTailRule in @(
        'combat.Apply<CoveredPower>(context.Target, 1, card.Owner.Creature);',
        'PowerPredictionStateSupport.ApplyInterceptCoverage(')) {
        if (-not $bespokeOnPlayText.Contains($requiredInterceptTailRule)) {
            $violations.Add("${bespokeOnPlayPath}: missing 0.107.1 Intercept continuation rule '$requiredInterceptTailRule'")
        }
    }
}
$powerPredictionStateText = [IO.File]::ReadAllText($powerPredictionStatePath)
foreach ($requiredInterceptState in @(
    'typeof(PowerModel).GetField("_internalData"',
    'GetNestedType("Data", BindingFlags.NonPublic)',
    '.GetField("coveredCreatures"',
    'NativeInterceptCoveredCreatures',
    'InterceptCoveredCreatures',
    'ApplyInterceptCoverage',
    'case (InterceptPower value, InterceptPower original)',
    'new InterceptPredictionState(NativeInterceptCoveredCreatures(original))',
    'internal sealed class InterceptPredictionState')) {
    if (-not $powerPredictionStateText.Contains($requiredInterceptState)) {
        $violations.Add("${powerPredictionStatePath}: missing Intercept hidden-state rule '$requiredInterceptState'")
    }
}
foreach ($requiredInterceptDamage in @(
    'registry.Register<CoveredPower>(HandleCoveredPower);',
    'registry.Register<InterceptPower>(HandleInterceptPower);',
    'context.Target == power.Owner && context.Props.IsPoweredAttack()',
    'PowerPredictionStateSupport.InterceptCoveredCreatures(context.Simulator, power).Count + 1')) {
    if (-not $damageMirrorText.Contains($requiredInterceptDamage)) {
        $violations.Add("${damageMirrorPath}: missing Intercept damage rule '$requiredInterceptDamage'")
    }
}
if (-not $simulatedCombatText.Contains('PowerPredictionStateSupport.InterceptCoveredCreatures(simulator, intercept)')) {
    $violations.Add("${simulatedCombatPath}: Intercept covered-target state must participate in the search fingerprint")
}
$continuationStampText = [IO.File]::ReadAllText($continuationStampPath)
if (-not $continuationStampText.Contains('NativeInterceptCoveredCreatures(intercept)')) {
    $violations.Add("${continuationStampPath}: live Intercept covered-target state is missing from exact continuation")
}
if (-not $continuationStampText.Contains('InterceptCoveredCreatures(simulator, intercept)')) {
    $violations.Add("${continuationStampPath}: predicted Intercept covered-target state is missing from exact continuation")
}
$afterDeathPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Death/AfterDeathMirrors.cs'
$afterDeathText = [IO.File]::ReadAllText($afterDeathPath)
if (-not $afterDeathText.Contains('registry.Register<CoveredPower>(HandleCoveredPower);')) {
    $violations.Add("${afterDeathPath}: CoveredPower applier-death cleanup mirror is missing")
}
if (-not $afterDeathText.Contains('ReferenceEquals(context.Creature, power.Applier)')) {
    $violations.Add("${afterDeathPath}: CoveredPower must expire when its applier dies")
}
$endTurnPowerPath = Join-Path $repositoryRoot 'src/Prediction/EndTurnPowerSupport.cs'
$endTurnPowerText = [IO.File]::ReadAllText($endTurnPowerPath)
if (-not $endTurnPowerText.Contains('case CoveredPower or InterceptPower when side == CombatSide.Enemy:')) {
    $violations.Add("${endTurnPowerPath}: Covered/Intercept must expire after the enemy side turn")
}


$miseryRegistryRule = 'registry.Register<Misery>(BespokeCardMirrors.MiseryOnPlay);'
if (-not $cardOnPlayRegistryText.Contains($miseryRegistryRule)) {
    $violations.Add("${cardOnPlayRegistryPath}: 0.107.1 Misery requires its dedicated native-order OnPlay mirror")
}
if ($cardEffectSpecText.Contains('case Misery when target != null:') -or
    $cardEffectSpecText.Contains('private static void SpreadDebuffs(')) {
    $violations.Add("${cardEffectSpecPath}: Misery must not use the old post-attack aggregated Debuff approximation")
}
$miseryStart = $bespokeOnPlayText.IndexOf('public static void MiseryOnPlay')
$miseryEnd = $bespokeOnPlayText.IndexOf('public static void MaulOnPlay', $miseryStart)
if ($miseryStart -lt 0 -or $miseryEnd -le $miseryStart) {
    $violations.Add("${bespokeOnPlayPath}: Misery mirror boundary is missing")
}
else {
    $miseryBlock = $bespokeOnPlayText.Substring($miseryStart, $miseryEnd - $miseryStart)
    foreach ($requiredMiseryRule in @(
        'MiseryDebuffSnapshot[] debuffs = combat.EffectivePowers()',
        'ReferenceEquals(power.Owner, context.Target)',
        'power.TypeForCurrentAmount == MegaCrit.Sts2.Core.Entities.Powers.PowerType.Debuff',
        'DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)',
        'new MiserySpreadExecutionFrame(context.Target, debuffs)',
        'foreach (MiseryDebuffSnapshot debuff in debuffs)',
        'combat.ApplyPower(debuff.PowerType, enemy, debuff.Amount, debuff.Applier)')) {
        if (-not $miseryBlock.Contains($requiredMiseryRule)) {
            $violations.Add("${bespokeOnPlayPath}: missing 0.107.1 Misery rule '$requiredMiseryRule'")
        }
    }
    if ($miseryBlock.Contains('GroupBy(')) {
        $violations.Add("${bespokeOnPlayPath}: Misery Debuffs must retain native Power order and must not be grouped by type")
    }
}

$demonicShieldRegistryRule = 'registry.Register<DemonicShield>(BespokeCardMirrors.DemonicShieldOnPlay);'
if (-not $cardOnPlayRegistryText.Contains($demonicShieldRegistryRule)) {
    $violations.Add("${cardOnPlayRegistryPath}: 0.107.1 Demonic Shield requires its dedicated native-order OnPlay mirror")
}
if (-not $cardOnPlayCatalogText.Contains('typeof(DemonicShield)')) {
    $violations.Add("${cardOnPlayCatalogPath}: Demonic Shield must remain in the compensated OnPlay catalog")
}
$demonicShieldStart = $bespokeOnPlayText.IndexOf('public static void DemonicShieldOnPlay')
$demonicShieldEnd = $bespokeOnPlayText.IndexOf('public static void InterceptOnPlay', $demonicShieldStart)
if ($demonicShieldStart -lt 0 -or $demonicShieldEnd -le $demonicShieldStart) {
    $violations.Add("${bespokeOnPlayPath}: Demonic Shield mirror boundary is missing")
}
else {
    $demonicShieldBlock = $bespokeOnPlayText.Substring($demonicShieldStart, $demonicShieldEnd - $demonicShieldStart)
    foreach ($requiredDemonicShieldRule in @(
        'context.Simulator.Damage(',
        'card.DynamicVars.HpLoss.BaseValue',
        'ValueProp.Unblockable',
        'ValueProp.Unpowered',
        'context.Card,',
        'ContinueOrQueueTail(context, BespokeTailKind.DemonicShieldGainBlock);')) {
        if (-not $demonicShieldBlock.Contains($requiredDemonicShieldRule)) {
            $violations.Add("${bespokeOnPlayPath}: missing 0.107.1 Demonic Shield rule '$requiredDemonicShieldRule'")
        }
    }
    foreach ($requiredDemonicShieldTailRule in @(
        'context.Calculate(card.DynamicVars.CalculatedBlock)',
        'context.GainBlock(',
        'context.Target')) {
        if (-not $bespokeOnPlayText.Contains($requiredDemonicShieldTailRule)) {
            $violations.Add("${bespokeOnPlayPath}: missing 0.107.1 Demonic Shield continuation rule '$requiredDemonicShieldTailRule'")
        }
    }
}

$sneakySpec = '[typeof(Sneaky)] = [Owner<SneakyPower>("SneakyPower")]'
if (-not $cardEffectSpecText.Contains($sneakySpec)) {
    $violations.Add("${cardEffectSpecPath}: 0.107.1 Sneaky must apply SneakyPower from its Power dynamic var")
}
$afterCardPlayedPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardPlayedMirrors.cs'
$afterCardPlayedText = [IO.File]::ReadAllText($afterCardPlayedPath)
foreach ($requiredSneakyRule in @(
    'registry.Register<SneakyPower>(HandleSneakyPower);',
    'context.PreviewCard.Owner.Creature != power.Owner',
    'context.PreviewCard.Type == CardType.Attack',
    'context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered)')) {
    if (-not $afterCardPlayedText.Contains($requiredSneakyRule)) {
        $violations.Add("${afterCardPlayedPath}: missing 0.107.1 Sneaky rule '$requiredSneakyRule'")
    }
}


$mimicRegistryRule = 'registry.Register<Mimic>(BespokeCardMirrors.MimicOnPlay);'
if (-not $cardOnPlayRegistryText.Contains($mimicRegistryRule)) {
    $violations.Add("${cardOnPlayRegistryPath}: 0.107.1 Mimic requires a dedicated owner-Block OnPlay mirror")
}
if (-not $cardOnPlayCatalogText.Contains('typeof(Mimic)')) {
    $violations.Add("${cardOnPlayCatalogPath}: Mimic must remain in the compensated OnPlay catalog")
}
$mimicStart = $bespokeOnPlayText.IndexOf('public static void MimicOnPlay')
$mimicEnd = $bespokeOnPlayText.IndexOf('public static void MaulOnPlay', $mimicStart)
if ($mimicStart -lt 0 -or $mimicEnd -le $mimicStart) {
    $violations.Add("${bespokeOnPlayPath}: Mimic mirror boundary is missing")
}
else {
    $mimicBlock = $bespokeOnPlayText.Substring($mimicStart, $mimicEnd - $mimicStart)
    foreach ($requiredMimicRule in @(
        'card.Owner.Creature',
        'context.Calculate(card.DynamicVars.CalculatedBlock)',
        'card.DynamicVars.CalculatedBlock.Props')) {
        if (-not $mimicBlock.Contains($requiredMimicRule)) {
            $violations.Add("${bespokeOnPlayPath}: missing 0.107.1 Mimic rule '$requiredMimicRule'")
        }
    }
    if ($mimicBlock.Contains('context.GainBlock(context.Target')) {
        $violations.Add("${bespokeOnPlayPath}: Mimic must gain Block on its owner, not on the selected ally")
    }
}


$endTurnPowerText = [IO.File]::ReadAllText($endTurnPowerPath)
$knockdownExpiry = 'case KnockdownPower when ownerParticipates:'
if (-not $endTurnPowerText.Contains($knockdownExpiry)) {
    $violations.Add("${endTurnPowerPath}: 0.107.1 Knockdown instances must expire when their owner participates in side-turn end")
}
if (-not $endTurnPowerText.Contains('combat.SetPowerAmount(power, 0);')) {
    $violations.Add("${endTurnPowerPath}: Knockdown expiry must remove the specific instanced power")
}
if (-not $cardEffectSpecText.Contains('[typeof(Knockdown)] = [Target<KnockdownPower>("KnockdownPower")]')) {
    $violations.Add("${cardEffectSpecPath}: Knockdown must remain an instanced target Power application")
}
if (-not $simulatedCombatText.Contains('if (simulated is KnockdownPower knockdown && applier != null)')) {
    $violations.Add("${simulatedCombatPath}: Knockdown applier display state must remain branch-local")
}


# Pillar of Creation is a version trap: 0.107.1 triggers on every card generated by
# the owner. v0.109 introduced first-trigger-per-turn state, which must not leak
# back into this compatibility target.
$cardPowerOnPlayPath = Join-Path $repositoryRoot 'src/Prediction/CardPowerOnPlaySupport.cs'
$cardPowerOnPlayText = [IO.File]::ReadAllText($cardPowerOnPlayPath)
if (-not $cardPowerOnPlayText.Contains('case PillarOfCreation:')) {
    $violations.Add("${cardPowerOnPlayPath}: 0.107.1 Pillar of Creation Power application is missing")
}
if (-not $cardPowerOnPlayText.Contains('combat.Apply<PillarOfCreationPower>(owner, card.DynamicVars.Block.IntValue, owner);')) {
    $violations.Add("${cardPowerOnPlayPath}: Pillar of Creation must use its pinned card Block dynamic var as the Power amount")
}

$afterCardGeneratedPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardGeneratedForCombatMirrors.cs'
$afterCardGeneratedText = [IO.File]::ReadAllText($afterCardGeneratedPath)
$pillarStart = $afterCardGeneratedText.IndexOf('private static void HandlePillarOfCreationPower(')
$pillarEnd = $afterCardGeneratedText.IndexOf('private static void HandleSmokestackPower(', $pillarStart)
if ($pillarStart -lt 0 -or $pillarEnd -le $pillarStart) {
    $violations.Add("${afterCardGeneratedPath}: Pillar of Creation hook boundary is missing")
}
else {
    $pillarBlock = $afterCardGeneratedText.Substring($pillarStart, $pillarEnd - $pillarStart)
    foreach ($requiredPillarRule in @(
        'context.Creator?.Creature == power.Owner',
        'context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered)')) {
        if (-not $pillarBlock.Contains($requiredPillarRule)) {
            $violations.Add("${afterCardGeneratedPath}: missing 0.107.1 Pillar of Creation rule '$requiredPillarRule'")
        }
    }
    foreach ($laterPillarState in @(
        'StateStore.Get(',
        'PredictionState',
        'HasTriggered',
        'TriggeredThisTurn',
        'FirstGenerated')) {
        if ($pillarBlock.Contains($laterPillarState)) {
            $violations.Add("${afterCardGeneratedPath}: v0.109 once-per-turn Pillar of Creation state returned '$laterPillarState'")
        }
    }
}


# Single-player 0.107.1 version traps that later beta patches reworked.
# These paths intentionally consume the pinned 0.107.1 CardModel/DynamicVar data
# instead of copying later card text or later upstream solver behavior.
$calculatedVarSpecPath = Join-Path $repositoryRoot 'src/Prediction/CalculatedVarSpecRegistry.cs'
$calculatedVarSpecText = [IO.File]::ReadAllText($calculatedVarSpecPath)
foreach ($requiredMirageRule in @(
    'typeof(PreciseCut), typeof(Stack), typeof(Squeeze), typeof(Mirage)',
    'Mirage => combat.Enemies',
    '.Where(enemy => simulator.State.GetCreature(enemy).IsAlive)',
    '.Sum(enemy => combat.GetAmount<PoisonPower>(enemy))')) {
    if (-not $calculatedVarSpecText.Contains($requiredMirageRule)) {
        $violations.Add("${calculatedVarSpecPath}: missing 0.107.1 Mirage poison-sum Block rule '$requiredMirageRule'")
    }
}

$cardOnPlaySupportPath = Join-Path $repositoryRoot 'src/Prediction/CardOnPlaySupport.cs'
$cardOnPlaySupportText = [IO.File]::ReadAllText($cardOnPlaySupportPath)
$fuelStart = $cardOnPlaySupportText.IndexOf('case Fuel:')
$fuelEnd = $cardOnPlaySupportText.IndexOf('case Haze:', $fuelStart)
if ($fuelStart -lt 0 -or $fuelEnd -le $fuelStart) {
    $violations.Add("${cardOnPlaySupportPath}: Fuel OnPlay boundary is missing")
}
else {
    $fuelBlock = $cardOnPlaySupportText.Substring($fuelStart, $fuelEnd - $fuelStart)
    $fuelEnergy = $fuelBlock.IndexOf('simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);')
    $fuelDraw = $fuelBlock.IndexOf('simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);')
    if ($fuelEnergy -lt 0 -or $fuelDraw -lt 0 -or $fuelDraw -le $fuelEnergy) {
        $violations.Add("${cardOnPlaySupportPath}: 0.107.1 Fuel must gain Energy then draw its Cards amount")
    }
}

$compactStart = $cardEffectSpecText.IndexOf('case Compact:')
$compactEnd = $cardEffectSpecText.IndexOf('case Claw claw:', $compactStart)
if ($compactStart -lt 0 -or $compactEnd -le $compactStart) {
    $violations.Add("${cardEffectSpecPath}: Compact card-effect boundary is missing")
}
else {
    $compactBlock = $cardEffectSpecText.Substring($compactStart, $compactEnd - $compactStart)
    foreach ($requiredCompactRule in @(
        'candidate.Preview.Type == CardType.Status',
        'CanonicalModels.Card<Fuel>()',
        'card.IsUpgraded')) {
        if (-not $compactBlock.Contains($requiredCompactRule)) {
            $violations.Add("${cardEffectSpecPath}: missing 0.107.1 Compact/Fuel rule '$requiredCompactRule'")
        }
    }
}

$afterGeneratedVersionTrapPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardGeneratedForCombatMirrors.cs'
$afterGeneratedVersionTrapText = [IO.File]::ReadAllText($afterGeneratedVersionTrapPath)
$rocketPunchStart = $afterGeneratedVersionTrapText.IndexOf('private static void HandleRocketPunch(')
$rocketPunchEnd = $afterGeneratedVersionTrapText.IndexOf('internal sealed class AfterCardGeneratedForCombatMirrorContext', $rocketPunchStart)
if ($rocketPunchStart -lt 0 -or $rocketPunchEnd -le $rocketPunchStart) {
    $violations.Add("${afterGeneratedVersionTrapPath}: Rocket Punch hook boundary is missing")
}
else {
    $rocketPunchBlock = $afterGeneratedVersionTrapText.Substring(
        $rocketPunchStart,
        $rocketPunchEnd - $rocketPunchStart)
    foreach ($requiredRocketPunchRule in @(
        'context.Creator == card.Owner',
        'context.PreviewCard.Owner == card.Owner',
        'context.PreviewCard.Type == CardType.Status',
        'EnergyCost.SetUntilPlayed(0)')) {
        if (-not $rocketPunchBlock.Contains($requiredRocketPunchRule)) {
            $violations.Add("${afterGeneratedVersionTrapPath}: missing 0.107.1 Rocket Punch rule '$requiredRocketPunchRule'")
        }
    }
}

$enchantmentOnPlayPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Enchantments/OnPlay/EnchantmentOnPlayMirrors.cs'
$enchantmentOnPlayText = [IO.File]::ReadAllText($enchantmentOnPlayPath)
$inkyStart = $enchantmentOnPlayText.IndexOf('private static void HandleInky(')
$inkyEnd = $enchantmentOnPlayText.IndexOf('private static void HandleMomentum(', $inkyStart)
if ($inkyStart -lt 0 -or $inkyEnd -le $inkyStart) {
    $violations.Add("${enchantmentOnPlayPath}: Inky OnPlay boundary is missing")
}
else {
    $inkyBlock = $enchantmentOnPlayText.Substring($inkyStart, $inkyEnd - $inkyStart)
    foreach ($requiredInkyOnPlayRule in @(
        'typeof(WeakPower)',
        'enchantment.DynamicVars.Weak.IntValue',
        'context.PreviewCard.Owner.Creature')) {
        if (-not $inkyBlock.Contains($requiredInkyOnPlayRule)) {
            $violations.Add("${enchantmentOnPlayPath}: missing 0.107.1 Inky Weak rule '$requiredInkyOnPlayRule'")
        }
    }
}
$modifyDamageMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Damage/ModifyDamageMirrors.cs'
$modifyDamageMirrorText = [IO.File]::ReadAllText($modifyDamageMirrorPath)
if ($modifyDamageMirrorText.Contains('Register<Inky>')) {
    $violations.Add("${modifyDamageMirrorPath}: 0.107.1 Inky additive damage must remain on the pinned native ModifyDamageAdditive hook")
}
foreach ($requiredNativeDamageFallback in @(
    'private static decimal InvokeOriginalAdditive(',
    'return listener.ModifyDamageAdditive(',
    'context.CardSource?.Preview')) {
    if (-not $modifyDamageMirrorText.Contains($requiredNativeDamageFallback)) {
        $violations.Add("${modifyDamageMirrorPath}: native additive damage fallback required by 0.107.1 Inky is missing '$requiredNativeDamageFallback'")
    }
}

$synchronizeStart = $cardOnPlaySupportText.IndexOf('case Synchronize:')
$synchronizeEnd = $cardOnPlaySupportText.IndexOf('case TheSmith:', $synchronizeStart)
if ($synchronizeStart -lt 0 -or $synchronizeEnd -le $synchronizeStart) {
    $violations.Add("${cardOnPlaySupportPath}: Synchronize OnPlay boundary is missing")
}
else {
    $synchronizeBlock = $cardOnPlaySupportText.Substring(
        $synchronizeStart,
        $synchronizeEnd - $synchronizeStart)
    foreach ($requiredSynchronizeRule in @(
        '.OrbQueue.Orbs',
        '.Select(orb => orb.Id)',
        '.Distinct()',
        'card.DynamicVars.CalculationBase.IntValue',
        'card.DynamicVars.CalculationExtra.IntValue * orbTypes',
        'combat.ApplyTemporaryFocus<SynchronizePower>')) {
        if (-not $synchronizeBlock.Contains($requiredSynchronizeRule)) {
            $violations.Add("${cardOnPlaySupportPath}: missing 0.107.1 Synchronize rule '$requiredSynchronizeRule'")
        }
    }
}


$rootCardGenerationPoolPath = Join-Path $repositoryRoot 'src/Search/RootCombatCardGenerationPoolSnapshot.cs'
$rootCardGenerationPoolText = [IO.File]::ReadAllText($rootCardGenerationPoolPath)
foreach ($requiredGenerationBoundary in @(
    'IReadOnlyList<Player> colorlessPlayers,',
    'IReadOnlyList<Player> characterPlayers,',
    'new(colorlessPlayers.Count, ReferenceEqualityComparer.Instance)',
    'foreach (Player player in colorlessPlayers)',
    'new(characterPlayers.Count, ReferenceEqualityComparer.Instance)',
    'foreach (Player player in characterPlayers)')) {
    if (-not $rootCardGenerationPoolText.Contains($requiredGenerationBoundary)) {
        $violations.Add("${rootCardGenerationPoolPath}: missing split multiplayer generation boundary '$requiredGenerationBoundary'")
    }
}
if ($simulatedCombatText -notmatch 'RootCombatCardGenerationPoolSnapshot[.]Capture[(][\s\r\n]*_players,[\s\r\n]*_rootCapturedPlayers,') {
    $violations.Add("${simulatedCombatPath}: multiplayer root must freeze colorless eligibility for the public roster while keeping character pools local-only")
}
$cardGenerationMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardGenerationCardMirrors.cs'
$cardGenerationMirrorText = [IO.File]::ReadAllText($cardGenerationMirrorPath)
$largesseStart = $cardGenerationMirrorText.IndexOf('public static void LargesseOnPlay')
$largesseEnd = $cardGenerationMirrorText.IndexOf('public static void MadScienceOnPlay', $largesseStart)
if ($largesseStart -lt 0 -or $largesseEnd -le $largesseStart) {
    $violations.Add("${cardGenerationMirrorPath}: Largesse mirror boundary is missing")
}
else {
    $largesseBlock = $cardGenerationMirrorText.Substring($largesseStart, $largesseEnd - $largesseStart)
    if (-not $largesseBlock.Contains('context.Simulator') -or
        -not $largesseBlock.Contains('.GetDistinctUnlockedColorlessForCombat(') -or
        -not $largesseBlock.Contains('targetPlayer')) {
        $violations.Add("${cardGenerationMirrorPath}: Largesse must select from the root-frozen target-player colorless generation pool")
    }
    if ($largesseBlock.Contains('targetPlayer.GetUnlockedColorlessCards')) {
        $violations.Add("${cardGenerationMirrorPath}: Largesse background prediction must not reread the remote player's live UnlockState")
    }
}

$cardGenerationContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardGenerationCardMirrors.ExecutionContinuation.cs'
$cardGenerationContinuationText = [IO.File]::ReadAllText($cardGenerationContinuationPath)
$madScienceStart = $cardGenerationMirrorText.IndexOf('public static void MadScienceOnPlay')
$madScienceEnd = $cardGenerationMirrorText.IndexOf('public static void ManifestAuthorityOnPlay', $madScienceStart)
if ($madScienceStart -lt 0 -or $madScienceEnd -le $madScienceStart) {
    $violations.Add("${cardGenerationMirrorPath}: Mad Science mirror boundary is missing")
}
else {
    $madScienceBlock = $cardGenerationMirrorText.Substring(
        $madScienceStart,
        $madScienceEnd - $madScienceStart)
    $madScienceRiderStart = $cardGenerationContinuationText.IndexOf('private static bool ContinueMadScienceRider(')
    $madScienceRiderEnd = $cardGenerationContinuationText.IndexOf('private static bool QueueGenerationCardContinuation(', $madScienceRiderStart)
    if ($madScienceRiderStart -lt 0 -or $madScienceRiderEnd -le $madScienceRiderStart) {
        $violations.Add("${cardGenerationContinuationPath}: Mad Science rider continuation boundary is missing")
    }
    else {
        $madScienceRiderBlock = $cardGenerationContinuationText.Substring(
            $madScienceRiderStart,
            $madScienceRiderEnd - $madScienceRiderStart)
        if (-not $madScienceRiderBlock.Contains('context.Simulator.AddToPile(cards, PileType.Hand)')) {
            $violations.Add("${cardGenerationContinuationPath}: 0.107.1 Mad Science Chaos rider must use ordinary pile insertion")
        }
        if ($madScienceRiderBlock.Contains('AddGeneratedCardsToCombat(')) {
            $violations.Add("${cardGenerationContinuationPath}: v0.108 generated-card Mad Science Chaos behavior returned")
        }
    }
    foreach ($requiredMadScienceAttackRule in @(
        'ContinueMadScienceAttacks(card, context, nextHit: 0, hitCount)',
        'for (int hitIndex = nextHit; hitIndex < hitCount; hitIndex++)',
        'DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)',
        'new MadScienceAttackExecutionFrame(',
        'hitIndex + 1',
        'return ContinueMadScienceRider(context, stage: 0);')) {
        if (-not $madScienceBlock.Contains($requiredMadScienceAttackRule)) {
            $violations.Add("${cardGenerationMirrorPath}: missing 0.107.1 Mad Science Violence rule '$requiredMadScienceAttackRule'")
        }
    }
    if ($madScienceBlock.Contains('.WithHitCount(')) {
        $violations.Add("${cardGenerationMirrorPath}: Mad Science Violence must use separate 0.107.1 AttackCommands, not later grouped-hit semantics")
    }
}

$turnStartChoiceSupportPath = Join-Path $repositoryRoot 'src/Prediction/TurnStartChoiceSupport.cs'
$turnStartChoiceSupportText = [IO.File]::ReadAllText($turnStartChoiceSupportPath)
foreach ($requiredWellLaidPlansChoiceRule in @(
    'public static bool ResolveSingleTurnRetain(',
    'Where(card => !card.Preview.ShouldRetainThisTurn)',
    'PlanChoiceEffect.ApplyRetain,',
    '0,',
    'maxCount,',
    'card.MutablePreview.GiveSingleTurnRetain()')) {
    if (-not $turnStartChoiceSupportText.Contains($requiredWellLaidPlansChoiceRule)) {
        $violations.Add("${turnStartChoiceSupportPath}: missing 0.107.1 Well-Laid Plans choice rule '$requiredWellLaidPlansChoiceRule'")
    }
}
$endTurnPowerPath = Join-Path $repositoryRoot 'src/Prediction/EndTurnPowerSupport.cs'
$endTurnPowerText = [IO.File]::ReadAllText($endTurnPowerPath)
foreach ($requiredWellLaidPlansPowerRule in @(
    'public static bool TriggerBeforeFlushLate(',
    'PersistentRelicSupport.ShouldFlush(combat, player)',
    'combat.GetPower<WellLaidPlansPower>(player.Creature)',
    'TurnStartChoiceSupport.ResolveSingleTurnRetain(',
    'combat.ActiveExecutionChoices')) {
    if (-not $endTurnPowerText.Contains($requiredWellLaidPlansPowerRule)) {
        $violations.Add("${endTurnPowerPath}: missing 0.107.1 Well-Laid Plans BeforeFlushLate rule '$requiredWellLaidPlansPowerRule'")
    }
}
$corePowerSupportPath = Join-Path $repositoryRoot 'src/Prediction/CorePowerSupport.cs'
$corePowerSupportText = [IO.File]::ReadAllText($corePowerSupportPath)
$flushStart = $corePowerSupportText.IndexOf('public static void FlushPlayerHandAtTurnEnd')
$flushEnd = $corePowerSupportText.IndexOf('public static void TickDurations', $flushStart)
if ($flushStart -lt 0 -or $flushEnd -le $flushStart) {
    $violations.Add("${corePowerSupportPath}: hand-flush helper boundary is missing")
}
else {
    $flushBlock = $corePowerSupportText.Substring($flushStart, $flushEnd - $flushStart)
    if ($flushBlock.Contains('EnchantmentLifecycleSupport.BeforeFlush')) {
        $violations.Add("${corePowerSupportPath}: BeforeFlush must execute before Well-Laid Plans BeforeFlushLate, not inside FlushPlayerHandAtTurnEnd")
    }
}
$expansionPath = Join-Path $repositoryRoot 'src/Search/CombatBeamSolver.Expansion.cs'
$expansionText = [IO.File]::ReadAllText($expansionPath)
$advanceRoundStart = $expansionText.IndexOf('private SearchBoundaryReason AdvanceRound(')
$advanceRoundEnd = $expansionText.IndexOf('private ActionCandidate BuildCandidate(', $advanceRoundStart)
if ($advanceRoundStart -lt 0 -or $advanceRoundEnd -le $advanceRoundStart) {
    $violations.Add("${expansionPath}: AdvanceRound boundary is missing")
}
else {
    $advanceRoundBlock = $expansionText.Substring($advanceRoundStart, $advanceRoundEnd - $advanceRoundStart)
    $beforeFlushIndex = $advanceRoundBlock.IndexOf('EnchantmentLifecycleSupport.BeforeFlush(simulator, _player)')
    $beforeFlushLateIndex = $advanceRoundBlock.IndexOf('EndTurnPowerSupport.TriggerBeforeFlushLate(simulator, simulatedCombat, _player)')
    $flushIndex = $advanceRoundBlock.IndexOf('CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, simulatedCombat, _player)')
    if ($beforeFlushIndex -lt 0 -or
        $beforeFlushLateIndex -le $beforeFlushIndex -or
        $flushIndex -le $beforeFlushLateIndex) {
        $violations.Add("${expansionPath}: 0.107.1 end-turn order must be BeforeFlush -> Well-Laid Plans BeforeFlushLate -> FlushPlayerHand")
    }
}

$bespokeContinuationRules = @(
    'private sealed record BespokeTailExecutionFrame(',
    'private sealed record FiendFireExecutionFrame(',
    'private sealed record LeadingStrikeExecutionFrame(',
    'private sealed record SecondWindExecutionFrame(',
    'ContinueFiendFire(',
    'ContinueLeadingStrikeShivs(',
    'ContinueSecondWind(',
    'BespokeTailKind.BoneShardsGainBlock',
    'BespokeTailKind.DemonicShieldGainBlock',
    'BespokeTailKind.InterceptCoverage',
    'BespokeTailKind.MaulGrowth',
    'BespokeTailKind.TheScytheGrowth',
    'BespokeTailKind.SacrificeGainBlock',
    'BespokeTailKind.SovereignBladeParryBlock',
    'Cards = Cards.Select(candidate => context.RequireRemap(candidate)).ToArray()')
foreach ($requiredBespokeContinuationRule in $bespokeContinuationRules) {
    if (-not $bespokeOnPlayText.Contains($requiredBespokeContinuationRule)) {
        $violations.Add("${bespokeOnPlayPath}: missing multi-step OnPlay continuation rule '$requiredBespokeContinuationRule'")
    }
}

foreach ($bespokeContinuationMethod in @(
    'BoneShards',
    'DemonicShield',
    'Intercept',
    'FiendFire',
    'LeadingStrike',
    'Misery',
    'Maul',
    'TheScythe',
    'Sacrifice',
    'SecondWind',
    'SovereignBlade')) {
    $methodStart = $bespokeOnPlayText.IndexOf("public static void $($bespokeContinuationMethod)OnPlay")
    $nextPublic = $bespokeOnPlayText.IndexOf([Environment]::NewLine + '    public static void ', $methodStart + 1)
    $nextPrivate = $bespokeOnPlayText.IndexOf([Environment]::NewLine + '    private static ', $methodStart + 1)
    $candidates = @($nextPublic, $nextPrivate) | Where-Object { $_ -gt $methodStart }
    $methodEnd = if ($candidates.Count -gt 0) { ($candidates | Measure-Object -Minimum).Minimum } else { $bespokeOnPlayText.Length }
    if ($methodStart -lt 0 -or $methodEnd -le $methodStart) {
        $violations.Add("${bespokeOnPlayPath}: continuation method boundary missing for $bespokeContinuationMethod")
        continue
    }
    $methodBlock = $bespokeOnPlayText.Substring($methodStart, $methodEnd - $methodStart)
    if (-not $methodBlock.Contains('context.Simulator.AcknowledgeExecutionDispatch();')) {
        $violations.Add("${bespokeOnPlayPath}: $bespokeContinuationMethod must acknowledge the adapted execution dispatch before a resumable suffix")
    }
}

$orbContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.Orb.ExecutionContinuation.cs'
$orbContinuationText = [IO.File]::ReadAllText($orbContinuationPath)
$orbSimulatorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.Orb.cs'
$orbSimulatorText = [IO.File]::ReadAllText($orbSimulatorPath)
foreach ($requiredOrbContinuationRule in @(
    'private sealed record OrbChannelBatchExecutionFrame<TOrb>(',
    'private sealed record OrbChannelExecutionFrame(',
    'private sealed record OrbEvokeAfterModelExecutionFrame(',
    'private sealed record OrbEvokeNextExecutionFrame(',
    'private sealed record OrbPassiveTriggerExecutionFrame(',
    'private sealed record OrbPassiveAfterModelExecutionFrame(',
    'PrepareExecutionOrb(Orb, context)',
    'PrepareExecutionEnemyDeathSet(ProcessedEnemyDeaths, context)',
    'ProcessedEnemyDeaths = context.RequireRemap(ProcessedEnemyDeaths)',
    'context.Register(orb, fork)',
    'ContinueOrbChannelAfterEvoke(Player, Orb)',
    'ResolveDeathsFirst: true',
    'ResolveDeathsFirst: false')) {
    if (-not $orbContinuationText.Contains($requiredOrbContinuationRule)) {
        $violations.Add("${orbContinuationPath}: missing Orb execution continuation rule '$requiredOrbContinuationRule'")
    }
}
foreach ($requiredOrbSimulatorRule in @(
    'new OrbChannelExecutionFrame(player, orb)',
    'new OrbEvokeAfterModelExecutionFrame(evokedOrb, targets.ToArray())',
    'new OrbPassiveAfterModelExecutionFrame(processedEnemyDeaths)',
    'ContinueOrbChannelAfterEvoke(player, orb)',
    'ContinueOrbEvokeNext(',
    'ContinueOrbPassiveTriggers(')) {
    if (-not $orbSimulatorText.Contains($requiredOrbSimulatorRule)) {
        $violations.Add("${orbSimulatorPath}: missing resumable Orb simulator rule '$requiredOrbSimulatorRule'")
    }
}


$orbCardMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/OrbCardMirrors.cs'
$orbCardMirrorText = [IO.File]::ReadAllText($orbCardMirrorPath)
$orbCardContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/OrbCardMirrors.ExecutionContinuation.cs'
$orbCardContinuationText = [IO.File]::ReadAllText($orbCardContinuationPath)
foreach ($requiredOrbCardContinuationRule in @(
    'private sealed record OrbCardTailExecutionFrame(',
    'private sealed record ChaosExecutionFrame(',
    'private sealed record DarknessPassiveExecutionFrame(',
    'private sealed record ShatterExecutionFrame(',
    'private sealed record TeslaPassiveExecutionFrame(',
    'ContinueOrQueueTail(',
    'ContinueChaos(',
    'ContinueDarknessPassives(',
    'ContinueShatterEvokes(',
    'ContinueTeslaPassives(',
    'Orbs = Orbs.Select(orb => context.RequireRemap(orb)).ToArray()')) {
    if (-not $orbCardContinuationText.Contains($requiredOrbCardContinuationRule)) {
        $violations.Add("${orbCardContinuationPath}: missing Orb-card continuation rule '$requiredOrbCardContinuationRule'")
    }
}
foreach ($orbCardMethod in @(
    'BallLightning',
    'Chaos',
    'Chill',
    'ColdSnap',
    'ConsumingShadow',
    'Coolheaded',
    'Darkness',
    'Dualcast',
    'Fusion',
    'Glacier',
    'Glasswork',
    'IceLance',
    'Ignition',
    'MeteorStrike',
    'MultiCast',
    'Null',
    'Quadcast',
    'Rainbow',
    'Refract',
    'ShadowShield',
    'Shatter',
    'Spinner',
    'Tempest',
    'TeslaCoil',
    'Voltaic',
    'Zap')) {
    $methodStart = $orbCardMirrorText.IndexOf("public static void $($orbCardMethod)OnPlay")
    $nextMethod = $orbCardMirrorText.IndexOf([Environment]::NewLine + '    public static void ', $methodStart + 1)
    $methodEnd = if ($nextMethod -gt $methodStart) { $nextMethod } else { $orbCardMirrorText.Length }
    if ($methodStart -lt 0 -or $methodEnd -le $methodStart) {
        $violations.Add("${orbCardMirrorPath}: Orb-card method boundary missing for $orbCardMethod")
        continue
    }
    $methodBlock = $orbCardMirrorText.Substring($methodStart, $methodEnd - $methodStart)
    if (-not $methodBlock.Contains('context.Simulator.AcknowledgeExecutionDispatch();')) {
        $violations.Add("${orbCardMirrorPath}: $orbCardMethod must acknowledge its resumable OnPlay dispatch")
    }
}

$cardDrawSequencePath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardDrawCardMirrors.cs'
$cardDrawSequenceText = [IO.File]::ReadAllText($cardDrawSequencePath)
$cardDrawContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardDrawCardMirrors.ExecutionContinuation.cs'
$cardDrawContinuationText = [IO.File]::ReadAllText($cardDrawContinuationPath)
$voltaicStart = $orbCardMirrorText.IndexOf('public static void VoltaicOnPlay')
$voltaicEnd = $orbCardMirrorText.IndexOf('public static void ZapOnPlay', $voltaicStart)
if ($voltaicStart -lt 0 -or $voltaicEnd -le $voltaicStart) {
    $violations.Add("${orbCardMirrorPath}: Voltaic mirror boundary is missing")
}
else {
    $voltaicBlock = $orbCardMirrorText.Substring($voltaicStart, $voltaicEnd - $voltaicStart)
    if (-not $voltaicBlock.Contains('GetLightningChannelsForCalculatedVar(context.Simulator, card.Owner)')) {
        $violations.Add("${orbCardMirrorPath}: Voltaic must use frozen root + branch-local Lightning history")
    }
    if ($voltaicBlock.Contains('CombatManager.Instance.History')) {
        $violations.Add("${orbCardMirrorPath}: Voltaic must not reread mutable live combat history")
    }
}

foreach ($cardDrawContinuationMethod in @(
    'Adrenaline',
    'Offering',
    'Neurosurge',
    'SpoilsOfBattle',
    'CompileDriver',
    'EscapePlan',
    'Fetch',
    'Ftl',
    'HuddleUp',
    'Pillage',
    'Reboot',
    'Restlessness',
    'Scrape')) {
    $methodStart = $cardDrawSequenceText.IndexOf("public static void $($cardDrawContinuationMethod)OnPlay")
    $nextPublic = $cardDrawSequenceText.IndexOf([Environment]::NewLine + '    public static void ', $methodStart + 1)
    $nextDirective = $cardDrawSequenceText.IndexOf([Environment]::NewLine + '#', $methodStart + 1)
    $candidates = @($nextPublic, $nextDirective) | Where-Object { $_ -gt $methodStart }
    $methodEnd = if ($candidates.Count -gt 0) { ($candidates | Measure-Object -Minimum).Minimum } else { $cardDrawSequenceText.Length }
    if ($methodStart -lt 0 -or $methodEnd -le $methodStart) {
        $violations.Add("${cardDrawSequencePath}: continuation method boundary missing for $cardDrawContinuationMethod")
        continue
    }
    $methodBlock = $cardDrawSequenceText.Substring($methodStart, $methodEnd - $methodStart)
    if (-not $methodBlock.Contains('context.Simulator.AcknowledgeExecutionDispatch();')) {
        $violations.Add("${cardDrawSequencePath}: $cardDrawContinuationMethod must acknowledge resumable OnPlay dispatch")
    }
    if (-not $methodBlock.Contains('ContinueCardDrawSequence(')) {
        $violations.Add("${cardDrawSequencePath}: $cardDrawContinuationMethod must enter the card-draw continuation state machine")
    }
}
foreach ($requiredCardDrawContinuationRule in @(
    'private sealed record CardDrawExecutionFrame(',
    'CombatPredictionSimulator.ForkExecutionCardList(list, context)',
    'DrawnCards = DrawnCards is null ? null : context.RequireRemap(DrawnCards)',
    'CardDrawSequence.Pillage',
    'CardDrawSequence.Scrape',
    'CardDrawSequence.EscapePlan',
    'teammates: allies',
    'nextTeammate: index + 1',
    '!context.State.GetCreature(teammate).IsAlive')) {
    if (-not ($cardDrawSequenceText.Contains($requiredCardDrawContinuationRule) -or
              $cardDrawContinuationText.Contains($requiredCardDrawContinuationRule))) {
        $violations.Add("${cardDrawContinuationPath}: missing card-draw continuation rule '$requiredCardDrawContinuationRule'")
    }
}

foreach ($generatedCardContinuationMethod in @(
    'BundleOfJoy',
    'Distraction',
    'InfernalBlade',
    'JackOfAllTrades',
    'Jackpot',
    'Largesse',
    'ManifestAuthority',
    'Metamorphosis',
    'Stoke',
    'WhiteNoise')) {
    $methodStart = $cardGenerationMirrorText.IndexOf("public static void $($generatedCardContinuationMethod)OnPlay")
    $nextPublic = $cardGenerationMirrorText.IndexOf([Environment]::NewLine + '    public static void ', $methodStart + 1)
    $nextPrivate = $cardGenerationMirrorText.IndexOf([Environment]::NewLine + '    private static ', $methodStart + 1)
    $candidates = @($nextPublic, $nextPrivate) | Where-Object { $_ -gt $methodStart }
    $methodEnd = if ($candidates.Count -gt 0) { ($candidates | Measure-Object -Minimum).Minimum } else { $cardGenerationMirrorText.Length }
    if ($methodStart -lt 0 -or $methodEnd -le $methodStart) {
        $violations.Add("${cardGenerationMirrorPath}: generated-card method boundary missing for $generatedCardContinuationMethod")
        continue
    }
    $methodBlock = $cardGenerationMirrorText.Substring($methodStart, $methodEnd - $methodStart)
    if (-not $methodBlock.Contains('context.Simulator.AcknowledgeExecutionDispatch();')) {
        $violations.Add("${cardGenerationMirrorPath}: $generatedCardContinuationMethod must acknowledge generated-card execution continuation")
    }
}

$generatedContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardGenerationContinuation.cs'
$generatedContinuationText = [IO.File]::ReadAllText($generatedContinuationPath)
foreach ($requiredGeneratedContinuationRule in @(
    'private sealed record GeneratedCardBatchExecutionFrame(',
    'public IEnumerable<CombatPredictionHistoryEntry> DeferredEntries => [PendingEntry];',
    'ForkExecutionCardList(Cards, context)',
    'PendingEntry = context.RequireRemap(PendingEntry)',
    'History.CardGenerationResolved(pendingEntry, pendingCard!)',
    'new GeneratedCardBatchExecutionFrame(')) {
    if (-not $generatedContinuationText.Contains($requiredGeneratedContinuationRule)) {
        $violations.Add("${generatedContinuationPath}: missing generated-card continuation rule '$requiredGeneratedContinuationRule'")
    }
}
$cardPilePath = Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardPile.cs'
$cardPileText = [IO.File]::ReadAllText($cardPilePath)
if (-not $cardPileText.Contains('ContinueGeneratedCardBatch(')) {
    $violations.Add("${cardPilePath}: generated-card batches must enter the resumable batch helper")
}

$afterGeneratedPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardGeneratedForCombatMirrors.cs'
$afterGeneratedText = [IO.File]::ReadAllText($afterGeneratedPath)
foreach ($requiredTrashRule in @(
    'ContinueTrashToTreasure(context.Simulator, power, player, nextIndex: 0)',
    'private sealed record TrashToTreasureExecutionFrame(',
    'Power = (TrashToTreasurePower)context.RemapOrSelf(Power)',
    'index < power.Amount')) {
    if (-not $afterGeneratedText.Contains($requiredTrashRule)) {
        $violations.Add("${afterGeneratedPath}: missing resumable 0.107.1 Trash to Treasure rule '$requiredTrashRule'")
    }
}

$stokeStart = $cardGenerationMirrorText.IndexOf('public static void StokeOnPlay')
$stokeEnd = $cardGenerationMirrorText.IndexOf('public static void WhiteNoiseOnPlay', $stokeStart)
if ($stokeStart -lt 0 -or $stokeEnd -le $stokeStart) {
    $violations.Add("${cardGenerationMirrorPath}: Stoke mirror boundary is missing")
}
else {
    $stokeBlock = $cardGenerationMirrorText.Substring($stokeStart, $stokeEnd - $stokeStart)
    foreach ($requiredStokeRule in @(
        'context.Simulator.AcknowledgeExecutionDispatch();',
        'List<PredictedCard> cardsToExhaust = context.OwnerState.Hand.Cards.ToList();',
        'new StokeExecutionFrame(playedCard, play, cardsToExhaust, index + 1)',
        'cardsToExhaust.Count',
        'simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner)',
        'CardsToExhaust = context.RequireRemap(CardsToExhaust)')) {
        if (-not $stokeBlock.Contains($requiredStokeRule)) {
            $violations.Add("${cardGenerationMirrorPath}: missing resumable 0.107.1 Stoke rule '$requiredStokeRule'")
        }
    }
}

$persistentPowerSupportPath = Join-Path $repositoryRoot 'src/Prediction/PersistentPowerSupport.cs'
$persistentPowerSupportText = [IO.File]::ReadAllText($persistentPowerSupportPath)
foreach ($requiredForgeContinuationRule in @(
    'private static bool ContinueForge(',
    'ForgeExecutionStage.AfterGenerated',
    'ForgeExecutionStage.HammerTimePlayers',
    'private sealed record ForgeExecutionFrame(',
    'new ForgeExecutionFrame(',
    'simulator.State.Players.ToArray()',
    '!simulator.State.GetCreature(teammate.Creature).IsAlive',
    'Forge(simulator, teammate, amount, hammerTime)',
    'Source = Source is null ? null : context.RemapOrSelf(Source)',
    '(HammerTimePower)context.RemapOrSelf(HammerTime)')) {
    if (-not $persistentPowerSupportText.Contains($requiredForgeContinuationRule)) {
        $violations.Add("${persistentPowerSupportPath}: missing resumable 0.107.1 Forge rule '$requiredForgeContinuationRule'")
    }
}
$forgeStart = $persistentPowerSupportText.IndexOf('public static void Forge(')
if ($forgeStart -lt 0) {
    $violations.Add("${persistentPowerSupportPath}: Forge entry point is missing")
}
else {
    $forgeBlock = $persistentPowerSupportText.Substring($forgeStart)
    $generatedIndex = $forgeBlock.IndexOf('simulator.AddGeneratedCardToCombat(')
    $bladeGrowthIndex = $forgeBlock.IndexOf('((SovereignBlade)card.MutablePreview).AddDamage(amount)')
    $hammerLoopIndex = $forgeBlock.IndexOf('ForgeExecutionStage.HammerTimePlayers')
    if ($generatedIndex -lt 0 -or $bladeGrowthIndex -le $generatedIndex -or $hammerLoopIndex -le $bladeGrowthIndex) {
        $violations.Add("${persistentPowerSupportPath}: 0.107.1 Forge order must remain generated Blade -> Blade growth -> Hammer Time propagation")
    }
}

$bulkUpStart = $corePowerSupportText.IndexOf('case BulkUp:')
$bulkUpEnd = $corePowerSupportText.IndexOf('case Resonance:', $bulkUpStart)
if ($bulkUpStart -lt 0 -or $bulkUpEnd -le $bulkUpStart) {
    $violations.Add("${corePowerSupportPath}: Bulk Up card-power boundary is missing")
}
else {
    $bulkUpBlock = $corePowerSupportText.Substring($bulkUpStart, $bulkUpEnd - $bulkUpStart)
    $bulkDexterity = $bulkUpBlock.IndexOf('combat.Apply<DexterityPower>')
    $bulkResolve = $bulkUpBlock.IndexOf('PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat)')
    $bulkPending = $bulkUpBlock.IndexOf('if (simulator.HasPendingChoice)', $bulkResolve)
    $bulkRemoveSlots = $bulkUpBlock.IndexOf('.OrbQueue.RemoveCapacity(')
    $bulkStrength = $bulkUpBlock.IndexOf('combat.Apply<StrengthPower>')
    if ($bulkDexterity -lt 0 -or
        $bulkResolve -le $bulkDexterity -or
        $bulkPending -le $bulkResolve -or
        $bulkRemoveSlots -le $bulkPending -or
        $bulkStrength -le $bulkRemoveSlots) {
        $violations.Add("${corePowerSupportPath}: 0.107.1 Bulk Up order must be Dexterity -> resolve listeners -> remove Orb slots -> Strength")
    }
}

$attackSimulatorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.Attack.cs'
$attackSimulatorText = [IO.File]::ReadAllText($attackSimulatorPath)
foreach ($boundary in @(
    @{ Start = 'public AttackCommand BeginAttackContext(AttackCommand command)'; End = 'public void AddAttackContextHit' },
    @{ Start = 'public void EndAttackContext(AttackCommand attackContext, bool completed = true)'; End = 'public void ExecuteAttack(AttackCommand attackCommand)' },
    @{ Start = 'public void ExecuteAttack(AttackCommand attackCommand)'; End = '// Mirrors AttackCommand.GetPossibleTargets' })) {
    $start = $attackSimulatorText.IndexOf($boundary.Start)
    $end = $attackSimulatorText.IndexOf($boundary.End, $start + 1)
    if ($start -lt 0 -or $end -le $start) {
        $violations.Add("${attackSimulatorPath}: opaque attack boundary '$($boundary.Start)' is missing")
        continue
    }
    $block = $attackSimulatorText.Substring($start, $end - $start)
    if (-not $block.Contains('using (BeginExecutionDispatch())')) {
        $violations.Add("${attackSimulatorPath}: '$($boundary.Start)' must reject unsafe partial execution continuations")
    }
}

$damageSimulatorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.Damage.cs'
$damageSimulatorText = [IO.File]::ReadAllText($damageSimulatorPath)
foreach ($boundary in @(
    @{ Start = "public IReadOnlyList<DamageResult> Damage(`n        IReadOnlyList<Creature> targets,`n        decimal amount,`n        ValueProp props,`n        Creature? dealer,`n        PredictedCard? cardSource,`n        CardPlay? cardPlay)"; End = 'private IReadOnlyList<DamageResult> DamageSingleTarget(' },
    @{ Start = 'private IReadOnlyList<DamageResult> DamageSingleTarget('; End = '// Mirrors the per-target body of CreatureCmd.Damage.' },
    @{ Start = 'public bool Kill(Creature creature, bool force = false)'; End = '// Mirrors CreatureCmd.Kill.' },
    @{ Start = 'public bool Kill(IReadOnlyList<Creature> creatures, bool force = false)'; End = '// Mirrors CreatureCmd.KillWithoutCheckingWinCondition' })) {
    $start = $damageSimulatorText.IndexOf($boundary.Start)
    $end = $damageSimulatorText.IndexOf($boundary.End, $start + 1)
    if ($start -lt 0 -or $end -le $start) {
        $violations.Add("${damageSimulatorPath}: opaque damage/kill boundary '$($boundary.Start)' is missing")
        continue
    }
    $block = $damageSimulatorText.Substring($start, $end - $start)
    if (-not $block.Contains('using (BeginExecutionDispatch())')) {
        $violations.Add("${damageSimulatorPath}: '$($boundary.Start)' must reject unsafe partial execution continuations")
    }
}

$cardSelectionMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardSelectionCardMirrors.cs'
$cardSelectionMirrorText = [IO.File]::ReadAllText($cardSelectionMirrorPath)
$cardSelectionContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardSelectionCardMirrors.ExecutionContinuation.cs'
$cardSelectionContinuationText = [IO.File]::ReadAllText($cardSelectionContinuationPath)
foreach ($requiredSelectionContinuationRule in @(
    'private sealed record CardSelectionExecutionFrame(',
    'CardSelectionSequence.BeatDown',
    'CardSelectionSequence.Catastrophe',
    'CardSelectionSequence.Cinder',
    'CardSelectionSequence.DrainPower',
    'CardSelectionSequence.Thrash',
    'CardSelectionSequence.TrueGrit',
    'CardSelectionSequence.Uproar',
    'ContinueBeatDown(',
    'ContinueCatastrophe(',
    'ContinueCardSelectionSequence(',
    'ForkExecutionCardList(Cards, context)',
    'Cards = Cards is null ? null : context.RequireRemap(Cards)')) {
    if (-not $cardSelectionContinuationText.Contains($requiredSelectionContinuationRule)) {
        $violations.Add("${cardSelectionContinuationPath}: missing 0.107.1 selection continuation rule '$requiredSelectionContinuationRule'")
    }
}
foreach ($selectionMethod in @(
    'BeatDown',
    'Catastrophe',
    'Cinder',
    'DrainPower',
    'Thrash',
    'TrueGrit',
    'Uproar')) {
    $methodStart = $cardSelectionMirrorText.IndexOf("public static void $($selectionMethod)OnPlay")
    $nextPublic = $cardSelectionMirrorText.IndexOf([Environment]::NewLine + '    public static void ', $methodStart + 1)
    $nextPrivate = $cardSelectionMirrorText.IndexOf([Environment]::NewLine + '    private static ', $methodStart + 1)
    $candidates = @($nextPublic, $nextPrivate) | Where-Object { $_ -gt $methodStart }
    $methodEnd = if ($candidates.Count -gt 0) { ($candidates | Measure-Object -Minimum).Minimum } else { $cardSelectionMirrorText.Length }
    if ($methodStart -lt 0 -or $methodEnd -le $methodStart) {
        $violations.Add("${cardSelectionMirrorPath}: selection continuation method boundary missing for $selectionMethod")
        continue
    }
    $methodBlock = $cardSelectionMirrorText.Substring($methodStart, $methodEnd - $methodStart)
    if (-not $methodBlock.Contains('context.Simulator.AcknowledgeExecutionDispatch();')) {
        $violations.Add("${cardSelectionMirrorPath}: $selectionMethod must acknowledge its resumable OnPlay sequence")
    }
}

$randomTargetMirrorPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/RandomTargetAttackCardMirrors.cs'
$randomTargetMirrorText = [IO.File]::ReadAllText($randomTargetMirrorPath)
$randomTargetContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/RandomTargetAttackCardMirrors.ExecutionContinuation.cs'
$randomTargetContinuationText = [IO.File]::ReadAllText($randomTargetContinuationPath)
foreach ($requiredFlakEntryRule in @(
    'context.Simulator.AcknowledgeExecutionDispatch();',
    'int hitCount = (int)context.Calculate(card.DynamicVars["CalculatedHits"]);',
    'ContinueFlakCannon(context, statuses, hitCount, nextIndex: 0)')) {
    if (-not $randomTargetMirrorText.Contains($requiredFlakEntryRule)) {
        $violations.Add("${randomTargetMirrorPath}: missing 0.107.1 Flak Cannon entry rule '$requiredFlakEntryRule'")
    }
}
foreach ($requiredFlakContinuationRule in @(
    'private sealed record FlakCannonExecutionFrame(',
    'ForkExecutionCardList(Statuses, context)',
    'Statuses = context.RequireRemap(Statuses)',
    'context.Simulator.Exhaust(statuses[index])',
    'new FlakCannonExecutionFrame(',
    'index + 1',
    'context.AttackRandomOpponents(hitCount)')) {
    if (-not $randomTargetContinuationText.Contains($requiredFlakContinuationRule)) {
        $violations.Add("${randomTargetContinuationPath}: missing 0.107.1 Flak Cannon continuation rule '$requiredFlakContinuationRule'")
    }
}

$rootCombatHistoryPath = Join-Path $repositoryRoot 'src/Search/RootCombatHistorySnapshot.cs'
$rootCombatHistoryText = [IO.File]::ReadAllText($rootCombatHistoryPath)
foreach ($requiredRootHistoryEntry in @(
    'CardGeneratedEntry[] CardsGenerated',
    'OrbChanneledEntry[] OrbsChanneled',
    'history.Entries.OfType<CardGeneratedEntry>().ToArray()',
    'history.Entries.OfType<OrbChanneledEntry>().ToArray()')) {
    if (-not $rootCombatHistoryText.Contains($requiredRootHistoryEntry)) {
        $violations.Add("${rootCombatHistoryPath}: missing frozen calculated-card history entry '$requiredRootHistoryEntry'")
    }
}

$cardEventHistoryPath = Join-Path $repositoryRoot 'src/Search/SimulatedCombatState.CardEventHistory.cs'
$cardEventHistoryText = [IO.File]::ReadAllText($cardEventHistoryPath)
foreach ($requiredHistoryRule in @(
    'GetFinishedCardPlaysForCalculatedVar',
    'GetGeneratedCardsForCalculatedVar',
    'GetLightningChannelsForCalculatedVar',
    'GetUnblockedDamageEventsForCalculatedVar',
    'GetEtherealPlaysForCalculatedVar',
    'GetCardsDrawnForCalculatedVar',
    'AppendCalculatedCardHistoryFingerprint',
    'AppendLiveCalculatedCardHistory',
    'AppendPredictedCalculatedCardHistory')) {
    if (-not $cardEventHistoryText.Contains($requiredHistoryRule)) {
        $violations.Add("${cardEventHistoryPath}: missing history-sensitive calculated-card rule '$requiredHistoryRule'")
    }
}

if ($calculatedVarSpecText.Contains('CombatManager.Instance.History')) {
    $violations.Add("${calculatedVarSpecPath}: calculated vars must use frozen root history plus branch-local prediction history")
}
foreach ($requiredCalculatedHistoryCall in @(
    'GetGeneratedCardsForCalculatedVar',
    'GetLightningChannelsForCalculatedVar',
    'GetUnblockedDamageEventsForCalculatedVar',
    'GetEtherealPlaysForCalculatedVar',
    'GetFinishedCardPlaysForCalculatedVar',
    'GetCardsDrawnForCalculatedVar')) {
    if (-not $calculatedVarSpecText.Contains($requiredCalculatedHistoryCall)) {
        $violations.Add("${calculatedVarSpecPath}: missing frozen history call '$requiredCalculatedHistoryCall'")
    }
}

$simulatedCombatStatePath = Join-Path $repositoryRoot 'src/Search/SimulatedCombatState.cs'
$simulatedCombatStateText = [IO.File]::ReadAllText($simulatedCombatStatePath)
if (-not $simulatedCombatStateText.Contains('AppendCalculatedCardHistoryFingerprint(ref fingerprint, simulator);')) {
    $violations.Add("${simulatedCombatStatePath}: history-sensitive calculated vars must participate in state fingerprinting")
}

$continuationStampPath = Join-Path $repositoryRoot 'src/Runtime/ContinuationStamp.cs'
$continuationStampText = [IO.File]::ReadAllText($continuationStampPath)
foreach ($requiredHistoryStamp in @(
    'SimulatedCombatState.AppendLiveCalculatedCardHistory(text, player);',
    'combat.AppendPredictedCalculatedCardHistory(text, simulator, player);')) {
    if (-not $continuationStampText.Contains($requiredHistoryStamp)) {
        $violations.Add("${continuationStampPath}: missing history-sensitive continuation stamp '$requiredHistoryStamp'")
    }
}

$cardGenerationContinuationPath = Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Cards/OnPlay/CardGenerationCardMirrors.ExecutionContinuation.cs'
$cardGenerationContinuationText = [IO.File]::ReadAllText($cardGenerationContinuationPath)
foreach ($requiredGenerationPrefixRule in @(
    'GenerationCardSequence.Jackpot',
    'GenerationCardSequence.ManifestAuthority',
    'private sealed record GenerationCardExecutionFrame(',
    'ContinueGenerationCardSequence(',
    'private sealed record MadScienceMainExecutionFrame(',
    'private sealed record MadScienceRiderExecutionFrame(',
    'ContinueMadScienceMain(',
    'ContinueMadScienceRider(',
    'nextStage: 2',
    'typeof(DexterityPower)',
    'typeof(WeakPower)',
    'typeof(VulnerablePower)')) {
    if (-not $cardGenerationContinuationText.Contains($requiredGenerationPrefixRule)) {
        $violations.Add("${cardGenerationContinuationPath}: missing generated-card/Mad Science continuation rule '$requiredGenerationPrefixRule'")
    }
}
foreach ($generatedPrefixMethod in @('Jackpot', 'MadScience', 'ManifestAuthority')) {
    $methodStart = $cardGenerationMirrorText.IndexOf("public static void $($generatedPrefixMethod)OnPlay")
    $nextPublic = $cardGenerationMirrorText.IndexOf([Environment]::NewLine + '    public static void ', $methodStart + 1)
    $nextPrivate = $cardGenerationMirrorText.IndexOf([Environment]::NewLine + '    private static ', $methodStart + 1)
    $candidates = @($nextPublic, $nextPrivate) | Where-Object { $_ -gt $methodStart }
    $methodEnd = if ($candidates.Count -gt 0) { ($candidates | Measure-Object -Minimum).Minimum } else { $cardGenerationMirrorText.Length }
    if ($methodStart -lt 0 -or $methodEnd -le $methodStart) {
        $violations.Add("${cardGenerationMirrorPath}: generated-prefix method boundary missing for $generatedPrefixMethod")
        continue
    }
    $methodBlock = $cardGenerationMirrorText.Substring($methodStart, $methodEnd - $methodStart)
    if (-not $methodBlock.Contains('context.Simulator.AcknowledgeExecutionDispatch();')) {
        $violations.Add("${cardGenerationMirrorPath}: $generatedPrefixMethod must acknowledge its resumable OnPlay sequence")
    }
}
if ($cardGenerationMirrorText.Contains('ApplyMadScienceRider(')) {
    $violations.Add("${cardGenerationMirrorPath}: non-resumable Mad Science rider helper returned")
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    throw "Refactor boundary verification failed with $($violations.Count) violation(s)."
}

$archiveContract = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Replay/CheckpointArchive.cs'))
if ($archiveContract -match '\b(Godot|SolverController|RunManager)\b') {
    throw 'Checkpoint archive contract must remain independent of the game runtime.'
}
$nativeReplay = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Testing/UnattendedTestRunner.NativeReplay.cs'))
if ($nativeReplay.Contains('ApplyReplayStateAsync(')) {
    throw 'Native recorded replay must reconstruct state through native actions.'
}
Write-Output "REFACTOR_BOUNDARIES_OK search_files=$($searchFiles.Count)"
