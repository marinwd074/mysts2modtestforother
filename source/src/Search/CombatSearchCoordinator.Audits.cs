using System.Diagnostics;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private sealed class SupplementalAuditStage(SolverResult primary, CancellationToken deadline, CancellationToken cancellation)
    {
        internal SolverResult Best { get; private set; } = primary;
        internal CancellationToken Deadline { get; } = deadline;
        internal CancellationToken Cancellation { get; } = cancellation;

        internal SolverResult Solve(CombatBeamSolver solver)
            => solver.Solve(Deadline) ?? throw new OperationCanceledException(Deadline);

        internal void ConsiderCompleted(CombatRootSnapshot root, SearchPolicySnapshot policy, SolverResult candidate)
        {
            if (IsBetterCompletedResult(root, policy, candidate, Best))
                Best = candidate;
        }
    }

    private static SolverResult RunSupplementalAudits(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        Stopwatch requestClock,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback,
        SolverResult? earlySmartPotionBaseline,
        SolverResult? earlySmartPotionScout,
        bool e4SmartPotionBudgetActive)
    {
        long remainingMilliseconds = profile.SoftTimeBudgetMilliseconds - requestClock.ElapsedMilliseconds;
        if (remainingMilliseconds <= 0)
        {
            if (TryReuseEarlySmartPotionScout(
                    root,
                    policy,
                    primary,
                    earlySmartPotionBaseline,
                    earlySmartPotionScout,
                    out SolverResult? deadlineReusedScout,
                    allowBoundedVictory: true))
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SUPPLEMENTAL_AUDIT_BUDGET exhausted=true " +
                    $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                    $"budget_ms={profile.SoftTimeBudgetMilliseconds} " +
                    $"reused_early_scout=true");
                return deadlineReusedScout!;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SUPPLEMENTAL_AUDIT_BUDGET exhausted=true " +
                $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                $"budget_ms={profile.SoftTimeBudgetMilliseconds} " +
                $"reused_early_scout=false");
            return primary;
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(remainingMilliseconds));
        SolverResult selected = primary;
        string? activeE4Stage = null;
        SolverResult? activeE4Before = null;
        SupplementalAuditStage? activeStage = null;
        SearchRequestWorkSnapshot activeE4WorkBefore = default;
        long activeE4StartedMs = 0;

        SearchRequestWorkSnapshot E4WorkSnapshot()
            => policy.RequestWorkTotals?.Snapshot() ?? default;

        void LogE4SupplementalValue(
            string stage,
            SolverResult before,
            SolverResult? after,
            SearchRequestWorkSnapshot workBefore,
            long startedMs,
            string status,
            string admission)
        {
            SearchRequestWorkSnapshot workAfter = E4WorkSnapshot();
            long elapsedMs = Math.Max(0, requestClock.ElapsedMilliseconds - startedMs);
            long expanded = Math.Max(0, workAfter.ExpandedNodes - workBefore.ExpandedNodes);
            long transitions = Math.Max(0, workAfter.TransitionCount - workBefore.TransitionCount);
            SolverInterimResult beforeSummary = BuildInterimResult(root, policy, before);
            string relation = "no_result";
            string afterHpLoss = "-";
            string afterEnemyHp = "-";
            string afterPotions = "-";
            string afterMemberKind = "none";
            if (after != null)
            {
                SolverInterimResult afterSummary = BuildInterimResult(root, policy, after);
                relation = ClassifyE4SupplementalRelation(beforeSummary, afterSummary);
                afterHpLoss = afterSummary.ProjectedBattleHpLost.ToString();
                afterEnemyHp = afterSummary.EnemyHp.ToString();
                afterPotions = afterSummary.ProjectedBattlePotionCount.ToString();
                CandidateOrigin? afterOrigin = after.SearchEfficiencyOrigin;
                afterMemberKind = afterOrigin == null
                    ? "unknown"
                    : policy.PortfolioTelemetry?.FindSearchMember(afterOrigin.SearchMemberId)?.Kind
                        ?? "unknown";
            }

            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_E4_SUPPLEMENTAL_VALUE " +
                $"stage={stage} status={status} relation={relation} admission={admission} " +
                $"elapsed_ms={elapsedMs} expanded={expanded} transitions={transitions} " +
                $"selected_changed={(after != null && !ReferenceEquals(before, after)).ToString().ToLowerInvariant()} " +
                $"before_hp_loss={beforeSummary.ProjectedBattleHpLost} " +
                $"before_enemy_hp={beforeSummary.EnemyHp} " +
                $"before_potions={beforeSummary.ProjectedBattlePotionCount} " +
                $"after_hp_loss={afterHpLoss} after_enemy_hp={afterEnemyHp} " +
                $"after_potions={afterPotions} after_member_kind={afterMemberKind}");
        }

        SolverResult MeasureE4Stage(
            string stage,
            SolverResult before,
            Func<SupplementalAuditStage, SolverResult> run,
            Func<string>? completedStatus = null)
        {
            activeE4Stage = stage;
            activeE4Before = before;
            activeE4WorkBefore = E4WorkSnapshot();
            activeE4StartedMs = requestClock.ElapsedMilliseconds;
            activeStage = new(before, deadline.Token, cancellationToken);
            SolverResult after = run(activeStage);
            SolverInterimResult beforeSummary = BuildInterimResult(root, policy, before);
            SolverInterimResult afterSummary = BuildInterimResult(root, policy, after);
            bool admitted = ShouldAdmitE4SupplementalResult(
                e4SmartPotionBudgetActive,
                beforeSummary,
                afterSummary);
            LogE4SupplementalValue(
                stage,
                before,
                after,
                activeE4WorkBefore,
                activeE4StartedMs,
                status: completedStatus?.Invoke() ?? "completed",
                admission: admitted ? "accepted" : "rejected_regression");
            activeE4Stage = null;
            activeE4Before = null;
            activeStage = null;
            return admitted ? after : before;
        }

        try
        {
            if (!policy.PotionStrategy.HasForcedDirectives)
            {
                SolverResult beforeRequired = selected;
                selected = MeasureE4Stage(
                    "required_potion",
                    beforeRequired,
                    stage => AuditRequiredPotionUse(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        progressCallback,
                        profile,
                        stage));
            }
            if (ResolveTakeoverResult(selected, policy.Interaction) is { } requiredTakeoverResult)
                return requiredTakeoverResult;
            if (!policy.PotionStrategy.HasForcedDirectives
                && HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            if (TryReuseEarlySmartPotionScout(
                    root,
                    policy,
                    selected,
                    earlySmartPotionBaseline,
                    earlySmartPotionScout,
                    out SolverResult? reusedScout))
            {
                SolverResult beforeReuse = selected;
                selected = MeasureE4Stage(
                    "early_smart_scout_reuse",
                    beforeReuse,
                    _ => reusedScout!);
                if (ReferenceEquals(selected, reusedScout))
                    interimResultCallback?.Invoke(selected);
            }
            else
            {
                SolverResult beforeSmart = selected;
                using CancellationTokenSource smartDeadline =
                    CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                long smartStageStartMs = requestClock.ElapsedMilliseconds;
                int smartStageDeadlineMs = ComputeE4SmartPotionStageDeadlineMilliseconds(
                    profile.SoftTimeBudgetMilliseconds,
                    smartStageStartMs,
                    e4SmartPotionBudgetActive);
                bool smartBudgetActive =
                    smartStageDeadlineMs < profile.SoftTimeBudgetMilliseconds;
                if (smartBudgetActive)
                {
                    smartDeadline.CancelAfter(TimeSpan.FromMilliseconds(
                        Math.Max(1L, smartStageDeadlineMs - smartStageStartMs)));
                }
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SEARCH_E4_SMART_POTION_BUDGET " +
                    $"active={smartBudgetActive.ToString().ToLowerInvariant()} " +
                    $"stage_start_ms={smartStageStartMs} " +
                    $"stage_deadline_ms={smartStageDeadlineMs} " +
                    $"supplemental_deadline_ms={profile.SoftTimeBudgetMilliseconds}");
                selected = MeasureE4Stage(
                    "smart_potion",
                    beforeSmart,
                    _ => AuditSmartPotionUse(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        smartDeadline.Token,
                        cancellationToken,
                        progressCallback,
                        profile,
                        beforeSmart,
                        memoryForecast,
                        interimResultCallback),
                    completedStatus: () =>
                        smartDeadline.IsCancellationRequested
                            ? deadline.IsCancellationRequested
                                ? "deadline"
                                : "degraded_deadline"
                            : "completed");
            }
            if (HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            {
                SolverResult beforeOpening = selected;
                selected = MeasureE4Stage(
                    "opening_power",
                    beforeOpening,
                    stage => AuditOpeningPowerUse(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        progressCallback,
                        profile,
                        stage));
                if (HasReachedAcceptableBattleHpLoss(policy, selected))
                    return selected;
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (activeE4Stage != null && activeE4Before != null && activeStage != null)
            {
                SolverResult? retained = ReferenceEquals(activeStage.Best, activeE4Before) ? null : activeStage.Best;
                bool admitted = retained != null && ShouldAdmitE4SupplementalResult(e4SmartPotionBudgetActive,
                    BuildInterimResult(root, policy, activeE4Before), BuildInterimResult(root, policy, retained));
                if (admitted)
                    selected = retained!;
                LogE4SupplementalValue(
                    activeE4Stage,
                    activeE4Before,
                    retained,
                    activeE4WorkBefore,
                    activeE4StartedMs,
                    status: "deadline",
                    admission: admitted ? "accepted" : retained != null ? "rejected_regression" : "no_result");
                activeE4Stage = null;
                activeE4Before = null;
                activeStage = null;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SUPPLEMENTAL_AUDIT_BUDGET exhausted=true " +
                $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                $"budget_ms={profile.SoftTimeBudgetMilliseconds} " +
                $"selected_potions={selected.PotionCount}");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // A bounded scout cannot replace the full audit, but its already materialized
        // victory can still win against the result left by that audit.
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && earlySmartPotionScout is { BoundaryReason: SearchBoundaryReason.NodeLimit or SearchBoundaryReason.TimeLimit } boundedScout
            && IsBetterPotionPolicyResult(root, policy, boundedScout, selected)
            && TryReuseEarlySmartPotionScout(root, policy, primary, earlySmartPotionBaseline,
                boundedScout, out SolverResult? boundedSelection, allowBoundedVictory: true))
        {
            selected = boundedSelection!;
            interimResultCallback?.Invoke(selected);
        }
        return selected;
    }

    private static SolverResult AuditOpeningPowerUse(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SupplementalAuditStage stage)
    {
        SolverResult primary = stage.Best;
        CancellationToken cancellationToken = stage.Deadline;
        int primaryDeficit = StrategicHpDeficit(root, policy, primary);
        int maximumSmartPotionUses = policy.PotionPolicy == SolverPotionPolicy.Smart
            ? MaximumSmartPotionUses(root, policy, potionFreeWon: true, primaryDeficit)
            : Math.Max(1, primary.PotionCount);
        if (HasReachedProvablePrimaryQualityLowerBound(root, policy, primary)
            || policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                && battleDamage.PotionsUsedSoFar == 0)
            return primary;

        IReadOnlyList<PlanAction> openingPowers = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile)
            .PrepareAudit(static builder => builder.BuildOpeningPowerActions());
        IReadOnlyList<PlanAction> openingPotions = policy.PotionPolicy == SolverPotionPolicy.Disabled
            || maximumSmartPotionUses == 0
            ? []
            : new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .PrepareAudit(static builder => builder.BuildOpeningPotionActions());
        IReadOnlyList<PlanAction> generatedResourcePotions = openingPotions.Count == 0
            ? []
            : new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .PrepareAudit(builder => builder.SelectGeneratedResourcePotionActions(openingPotions));
        IReadOnlyList<PlanAction> openingResources = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile)
            .PrepareAudit(static builder => builder.BuildOpeningResourceActions());
        List<(PlanAction Potion, PlanAction Power)> potionPowerPairs = [];
        foreach (PlanAction openingPotion in openingPotions)
        {
            IReadOnlyList<PlanAction> powers = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .PrepareAudit(builder => builder.BuildPowerActionsAfterPrefix([openingPotion]));
            foreach (PlanAction power in powers)
            {
                potionPowerPairs.Add((openingPotion, power));
                if (potionPowerPairs.Count == 4)
                    break;
            }
            if (potionPowerPairs.Count == 4)
                break;
        }
        if (openingPowers.Count == 0
            && potionPowerPairs.Count == 0
            && generatedResourcePotions.Count == 0
            && openingResources.Count == 0)
            return primary;

        List<SolverResult> searches = [primary];
        foreach (PlanAction openingPower in openingPowers)
        {
            SolverResult posterior = stage.Solve(new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                stage.Cancellation,
                progressCallback,
                profile,
                fixedPrefixActions: [openingPower]));
            if (posterior.ResultScope != SolverResultScope.SearchCompletion)
                return posterior;

            posterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(posterior);
            searches.Add(posterior);
            if (HasReachedAcceptableBattleHpLoss(policy, posterior))
            {
                MergeAuditTotals(posterior, searches.ToArray());
                return posterior;
            }

            bool posteriorWon = posterior.Snapshot.AllEnemiesDead
                && !posterior.Snapshot.PlayerDead
                && posterior.Snapshot.ProjectedPlayerHp > 0;
            int posteriorDeficit = StrategicHpDeficit(root, policy, posterior);
            stage.ConsiderCompleted(root, policy, posterior);
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_POWER_POSTERIOR card={openingPower.CardId} " +
                $"won={posteriorWon} hp_deficit={posteriorDeficit} " +
                $"selected={ReferenceEquals(stage.Best, posterior)}");

            PlanAction? offensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile)
                .PrepareAudit(builder => builder.BuildOpeningPowerOffensiveFollowUp(openingPower));
            if (offensiveFollowUp == null)
                continue;

            SolverResult linkedPosterior = stage.Solve(new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                stage.Cancellation,
                progressCallback,
                profile,
                fixedPrefixActions: [openingPower, offensiveFollowUp]));
            if (linkedPosterior.ResultScope != SolverResultScope.SearchCompletion)
                return linkedPosterior;

            linkedPosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(linkedPosterior);
            searches.Add(linkedPosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, linkedPosterior))
            {
                MergeAuditTotals(linkedPosterior, searches.ToArray());
                return linkedPosterior;
            }

            bool linkedWon = linkedPosterior.Snapshot.AllEnemiesDead
                && !linkedPosterior.Snapshot.PlayerDead
                && linkedPosterior.Snapshot.ProjectedPlayerHp > 0;
            int linkedDeficit = StrategicHpDeficit(root, policy, linkedPosterior);
            stage.ConsiderCompleted(root, policy, linkedPosterior);
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_POWER_LINK_POSTERIOR " +
                $"cards={openingPower.CardId}+{offensiveFollowUp.CardId} " +
                $"won={linkedWon} hp_deficit={linkedDeficit} " +
                $"selected={ReferenceEquals(stage.Best, linkedPosterior)}");
        }

        foreach (PlanAction openingResource in openingResources)
        {
            PlanAction? defensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile)
                .PrepareAudit(builder => builder.BuildOpeningDefensiveFollowUp([openingResource]));
            if (defensiveFollowUp == null)
                continue;

            SolverResult resourceDefensePosterior = stage.Solve(new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                stage.Cancellation,
                progressCallback,
                profile,
                fixedPrefixActions: [openingResource, defensiveFollowUp]));
            if (resourceDefensePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourceDefensePosterior;

            resourceDefensePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(resourceDefensePosterior);
            searches.Add(resourceDefensePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, resourceDefensePosterior))
            {
                MergeAuditTotals(resourceDefensePosterior, searches.ToArray());
                return resourceDefensePosterior;
            }

            stage.ConsiderCompleted(root, policy, resourceDefensePosterior);
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_RESOURCE_DEFENSE_POSTERIOR " +
                $"cards={openingResource.CardId}+{defensiveFollowUp.CardId} " +
                $"won={resourceDefensePosterior.Snapshot.AllEnemiesDead && !resourceDefensePosterior.Snapshot.PlayerDead} " +
                $"hp_deficit={StrategicHpDeficit(root, policy, resourceDefensePosterior)} " +
                $"selected={ReferenceEquals(stage.Best, resourceDefensePosterior)}");
        }

        foreach (PlanAction openingPotion in generatedResourcePotions)
        {
            SolverResult? resourcePosterior = SolveOptionalPotionPosterior(
                new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    stage.Cancellation,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: 1,
                    fixedPrefixActions: [openingPotion]),
                policy,
                $"POTION_RESOURCE_POSTERIOR potion={openingPotion.PotionId}", stage);
            if (resourcePosterior == null)
                continue;
            if (resourcePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourcePosterior;

            resourcePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(resourcePosterior);
            searches.Add(resourcePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, resourcePosterior))
            {
                MergeAuditTotals(resourcePosterior, searches.ToArray());
                return resourcePosterior;
            }

            bool resourceWon = resourcePosterior.Snapshot.AllEnemiesDead
                && !resourcePosterior.Snapshot.PlayerDead
                && resourcePosterior.Snapshot.ProjectedPlayerHp > 0;
            int resourceDeficit = StrategicHpDeficit(root, policy, resourcePosterior);
            stage.ConsiderCompleted(root, policy, resourcePosterior);
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_RESOURCE_POSTERIOR " +
                $"potion={openingPotion.PotionId} card={openingPotion.Choice!.Cards[0].CardId} " +
                $"won={resourceWon} hp_deficit={resourceDeficit} " +
                $"selected={ReferenceEquals(stage.Best, resourcePosterior)}");
        }

        if (HasReachedProvablePrimaryQualityLowerBound(root, policy, stage.Best)
            && stage.Best.PotionCount <= 1)
        {
            MergeAuditTotals(stage.Best, searches.ToArray());
            return stage.Best;
        }

        foreach ((PlanAction openingPotion, PlanAction postPotionPower) in potionPowerPairs)
        {
            PlanAction[] jointPrefix = [openingPotion, postPotionPower];
            SolverResult? jointPosterior = SolveOptionalPotionPosterior(
                new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    stage.Cancellation,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses,
                    fixedPrefixActions: jointPrefix),
                policy,
                $"POTION_POWER_POSTERIOR potion={openingPotion.PotionId} power={postPotionPower.CardId}", stage);
            if (jointPosterior == null)
                continue;
            if (jointPosterior.ResultScope != SolverResultScope.SearchCompletion)
                return jointPosterior;

            jointPosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(jointPosterior);
            searches.Add(jointPosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, jointPosterior))
            {
                MergeAuditTotals(jointPosterior, searches.ToArray());
                return jointPosterior;
            }

            bool jointWon = jointPosterior.Snapshot.AllEnemiesDead
                && !jointPosterior.Snapshot.PlayerDead
                && jointPosterior.Snapshot.ProjectedPlayerHp > 0;
            int jointDeficit = StrategicHpDeficit(root, policy, jointPosterior);
            int comparisonDeficit = StrategicHpDeficit(root, policy, stage.Best);
            stage.ConsiderCompleted(root, policy, jointPosterior);
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"won={jointWon} hp_deficit={jointDeficit} " +
                $"selected={ReferenceEquals(stage.Best, jointPosterior)}");

            if (!jointWon
                || HasReachedProvablePrimaryQualityLowerBound(root, policy, jointPosterior)
                || jointDeficit > comparisonDeficit + 1)
            {
                continue;
            }

            PlanAction? defensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .PrepareAudit(builder => builder.BuildOpeningDefensiveFollowUp(jointPrefix));
            if (defensiveFollowUp == null)
                continue;

            SolverResult? defensivePosterior = SolveOptionalPotionPosterior(
                new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    stage.Cancellation,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses,
                    fixedPrefixActions: [openingPotion, postPotionPower, defensiveFollowUp]),
                policy,
                $"POTION_POWER_DEFENSIVE_POSTERIOR potion={openingPotion.PotionId} " +
                $"power={postPotionPower.CardId} follow_up={defensiveFollowUp.CardId}", stage);
            if (defensivePosterior == null)
                continue;
            if (defensivePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return defensivePosterior;

            defensivePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(defensivePosterior);
            searches.Add(defensivePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, defensivePosterior))
            {
                MergeAuditTotals(defensivePosterior, searches.ToArray());
                return defensivePosterior;
            }

            bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                && !defensivePosterior.Snapshot.PlayerDead
                && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
            int defensiveDeficit = StrategicHpDeficit(root, policy, defensivePosterior);
            stage.ConsiderCompleted(root, policy, defensivePosterior);
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_DEFENSIVE_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"follow_up={defensiveFollowUp.CardId} won={defensiveWon} " +
                $"hp_deficit={defensiveDeficit} selected={ReferenceEquals(stage.Best, defensivePosterior)}");

            if (HasReachedProvablePrimaryQualityLowerBound(root, policy, defensivePosterior))
                break;
        }

        MergeAuditTotals(stage.Best, searches.ToArray());
        return stage.Best;
    }

    private static SolverResult AuditRequiredPotionUse(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SupplementalAuditStage stage)
    {
        SolverResult primary = stage.Best;
        CancellationToken cancellationToken = stage.Deadline;
        if (policy.PotionPolicy != SolverPotionPolicy.RequireAtLeastOne
            || battleDamage.PotionsUsedSoFar > 0
            || primary.PotionCount <= 1)
        {
            return primary;
        }

        policy.Diagnostics.Info(
            $"[CombatSolver/Test] REQUIRED_POTION_AUDIT start potion_count={primary.PotionCount} " +
            $"reported_saved={primary.PotionHpSaved} required={primary.PotionHpRequired}");
        SolverResult potionFree = stage.Solve(new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            stage.Cancellation,
            progressCallback,
            profile,
            SolverPotionPolicy.Disabled));
        if (potionFree.ResultScope != SolverResultScope.SearchCompletion)
            return potionFree;

        potionFree.SingleSessionSearch = true;
        PopulateSingleSessionTotals(potionFree);

        bool potionFreeWon = IsCompleteVictory(potionFree);
        if (!potionFreeWon)
        {
            List<SolverResult> searches = [primary, potionFree];
            IReadOnlyList<PlanAction> openingPotions = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: primary.PotionCount)
                .BuildPreferredOpeningPotionActions();
            foreach (PlanAction openingPotion in openingPotions)
            {
                SolverResult posterior = stage.Solve(new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    stage.Cancellation,
                    progressCallback,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: primary.PotionCount,
                    fixedPrefixActions: [openingPotion]));
                if (posterior.ResultScope != SolverResultScope.SearchCompletion)
                    return posterior;

                posterior.SingleSessionSearch = true;
                PopulateSingleSessionTotals(posterior);
                searches.Add(posterior);
                if (HasReachedAcceptableBattleHpLoss(policy, posterior))
                {
                    MergeAuditTotals(posterior, searches.ToArray());
                    return posterior;
                }

                bool posteriorWon = posterior.Snapshot.AllEnemiesDead
                    && !posterior.Snapshot.PlayerDead
                    && posterior.Snapshot.ProjectedPlayerHp > 0;
                int posteriorDeficit = StrategicHpDeficit(root, policy, posterior);
                stage.ConsiderCompleted(root, policy, posterior);
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] REQUIRED_MULTI_POTION_POSTERIOR " +
                    $"potion={openingPotion.PotionId} target={openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                    $"won={posteriorWon} hp_deficit={posteriorDeficit} " +
                    $"selected={ReferenceEquals(stage.Best, posterior)}");

                if (primary.PotionCount != 2)
                    continue;

                IReadOnlyList<PlanAction> secondPotions = new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        cancellationToken,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount)
                    .PrepareAudit(builder => builder.BuildPreferredPotionActionsAfterPrefix([openingPotion]));
                foreach (PlanAction secondPotion in secondPotions)
                {
                    SolverResult pairPosterior = stage.Solve(new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        stage.Cancellation,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount,
                        fixedPrefixActions: [openingPotion, secondPotion]));
                    if (pairPosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return pairPosterior;

                    pairPosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(pairPosterior);
                    searches.Add(pairPosterior);
                    if (HasReachedAcceptableBattleHpLoss(policy, pairPosterior))
                    {
                        MergeAuditTotals(pairPosterior, searches.ToArray());
                        return pairPosterior;
                    }

                    bool pairWon = pairPosterior.Snapshot.AllEnemiesDead
                        && !pairPosterior.Snapshot.PlayerDead
                        && pairPosterior.Snapshot.ProjectedPlayerHp > 0;
                    int pairDeficit = StrategicHpDeficit(root, policy, pairPosterior);
                    stage.ConsiderCompleted(root, policy, pairPosterior);
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUIRED_POTION_PAIR_POSTERIOR " +
                        $"first={openingPotion.PotionId}:{openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"second={secondPotion.PotionId}:{secondPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"won={pairWon} hp_deficit={pairDeficit} " +
                        $"selected={ReferenceEquals(stage.Best, pairPosterior)}");

                    int selectedDeficit = StrategicHpDeficit(root, policy, stage.Best);
                    if (!pairWon || pairDeficit > selectedDeficit + 1)
                        continue;

                    PlanAction[] pairPrefix = [openingPotion, secondPotion];
                    PlanAction? defensiveFollowUp = new CombatBeamSolver(
                            root,
                            displayNames,
                            battleDamage,
                            policy,
                            cancellationToken,
                            progressCallback,
                            profile,
                            SolverPotionPolicy.RequireAtLeastOne,
                            maximumPotionUses: primary.PotionCount)
                        .PrepareAudit(builder => builder.BuildOpeningDefensiveFollowUp(pairPrefix));
                    if (defensiveFollowUp == null)
                        continue;

                    SolverResult defensivePosterior = stage.Solve(new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        stage.Cancellation,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount,
                        fixedPrefixActions: [openingPotion, secondPotion, defensiveFollowUp]));
                    if (defensivePosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return defensivePosterior;

                    defensivePosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(defensivePosterior);
                    searches.Add(defensivePosterior);
                    if (HasReachedAcceptableBattleHpLoss(policy, defensivePosterior))
                    {
                        MergeAuditTotals(defensivePosterior, searches.ToArray());
                        return defensivePosterior;
                    }

                    bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                        && !defensivePosterior.Snapshot.PlayerDead
                        && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
                    int defensiveDeficit = StrategicHpDeficit(root, policy, defensivePosterior);
                    stage.ConsiderCompleted(root, policy, defensivePosterior);
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUIRED_POTION_PAIR_DEFENSIVE_POSTERIOR " +
                        $"first={openingPotion.PotionId}:{openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"second={secondPotion.PotionId}:{secondPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"follow_up={defensiveFollowUp.CardId} won={defensiveWon} " +
                        $"hp_deficit={defensiveDeficit} " +
                        $"selected={ReferenceEquals(stage.Best, defensivePosterior)}");
                }
            }

            MergeAuditTotals(stage.Best, searches.ToArray());
            policy.Diagnostics.Info(
                "[CombatSolver/Test] REQUIRED_POTION_AUDIT result potion_free_won=False " +
                $"selected={(ReferenceEquals(stage.Best, primary) ? "multi_potion_rescue" : "opening_potion_posterior")}");
            return stage.Best;
        }

        // The candidates this baseline is compared against are ranked on the strategic axis, so the
        // baseline has to be measured on it too; the raw sum here predated healing counting at all.
        PotionFreePolicyBaseline baseline = new(
            Won: true,
            HpDeficit: StrategicHpDeficit(root, policy, potionFree),
            PlayerHp: potionFree.Snapshot.PlayerHp,
            CombatEndedTurn: potionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = potionFree.Snapshot.ProjectedDeathSaveUseCount,
            BattleHpLost = potionFree.ProjectedBattleHpLost,
            EvaluationWindow = potionFree.EvaluationWindow,
            OutstandingStolenResource = potionFree.OutstandingStolenResource,
        };
        SolverResult audited = stage.Solve(new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            stage.Cancellation,
            progressCallback,
            profile,
            SolverPotionPolicy.RequireAtLeastOne,
            baseline,
            maximumPotionUses: 1));
        if (audited.ResultScope != SolverResultScope.SearchCompletion)
            return audited;

        audited.SingleSessionSearch = true;
        PopulateSingleSessionTotals(audited);
        SolverResult auditedSelection = IsBetterPotionPolicyResult(
            root,
            policy,
            audited,
            primary)
                ? audited
                : primary;
        MergeAuditTotals(auditedSelection, primary, potionFree, audited);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] REQUIRED_POTION_AUDIT result potion_free_won=True " +
            $"baseline_hp_deficit={baseline.HpDeficit} " +
            $"selected={(ReferenceEquals(auditedSelection, audited) ? "single_potion_audit" : "primary")} " +
            $"selected_potion_count={auditedSelection.PotionCount} " +
            $"selected_saved={auditedSelection.PotionHpSaved} " +
            $"selected_required={auditedSelection.PotionHpRequired}");
        return auditedSelection;
    }

    private static SolverResult? SearchEarlySmartPotionScout(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        SolverSearchProfile profile,
        SolverResult provisionalPotionFree)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart
            || policy.PotionStrategy.HasForcedDirectives
            || !policy.UseE3FixedPortfolioScheduling
            || provisionalPotionFree.ResultScope != SolverResultScope.SearchCompletion)
        {
            return null;
        }

        bool potionFreeWon = IsCompleteVictory(provisionalPotionFree);
        int potionFreeDeficit = StrategicHpDeficit(root, policy, provisionalPotionFree);
        if (MaximumSmartPotionUses(root, policy, potionFreeWon, potionFreeDeficit) == 0)
            return null;

        PotionFreePolicyBaseline baseline = new(
            potionFreeWon,
            potionFreeDeficit,
            provisionalPotionFree.Snapshot.PlayerHp,
            provisionalPotionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = provisionalPotionFree.Snapshot.ProjectedDeathSaveUseCount,
            BattleHpLost = provisionalPotionFree.ProjectedBattleHpLost,
            EvaluationWindow = provisionalPotionFree.EvaluationWindow,
            OutstandingStolenResource = provisionalPotionFree.OutstandingStolenResource,
        };
        SolverResult? scout = SolveOptionalPotionPosterior(
            new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy.MeasurePhasePerformance ? policy with { RequestMemberKind = SearchRequestMemberKind.SmartPotion } : policy,
                cancellationToken,
                progressCallback: null,
                profile,
                SolverPotionPolicy.RequireAtLeastOne,
                baseline,
                maximumPotionUses: 1,
                minimumPotionUses: 1,
                primaryIncumbent: BuildPrimarySearchIncumbent(
                    root,
                    policy,
                    provisionalPotionFree)),
            policy,
            "E3_CROSS_FAMILY_SCOUT");
        if (scout == null || scout.ResultScope != SolverResultScope.SearchCompletion)
            return scout;

        scout.SingleSessionSearch = true;
        PopulateSingleSessionTotals(scout);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] E3_CROSS_FAMILY_SCOUT result " +
            $"boundary={scout.BoundaryReason} won={IsCompleteVictory(scout)} " +
            $"potions={scout.ExplicitPotionCount} expanded={scout.ExpandedNodes} " +
            $"transitions={scout.TransitionCount} elapsed_ms={scout.Elapsed.TotalMilliseconds:F1}");
        return scout;
    }

    private static bool TryReuseEarlySmartPotionScout(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult finalPotionFree,
        SolverResult? provisionalPotionFree,
        SolverResult? scout,
        out SolverResult? selected,
        bool allowBoundedVictory = false)
    {
        selected = null;
        if (policy.PotionPolicy != SolverPotionPolicy.Smart
            || policy.PotionStrategy.HasForcedDirectives
            || provisionalPotionFree == null
            || scout == null
            || scout.ResultScope != SolverResultScope.SearchCompletion
            || scout.ExplicitPotionCount != 1)
        {
            return false;
        }
        if (scout.BoundaryReason != SearchBoundaryReason.None
            && (!allowBoundedVictory || policy.RoutePolicy != SearchRoutePolicy.SinglePlayerFullRoute
                || root.PlayerCount != 1 || scout.Snapshot.HasRisk || scout.Snapshot.ProjectedDeathSaveUseCount != 0
                || scout.BoundaryReason is not (SearchBoundaryReason.NodeLimit or SearchBoundaryReason.TimeLimit)
                || !IsCompleteVictory(scout)))
            return false;

        // The early member used the provisional no-potion incumbent. Reuse it only
        // when the later no-potion result is at least as strong; otherwise rerun the
        // ordinary Smart audit rather than assuming the early search stayed complete.
        int? baselineQuality = CompareCompletedResultPrimaryQuality(
                root,
                policy,
                finalPotionFree,
                provisionalPotionFree);
        if (baselineQuality == null || baselineQuality > 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] E3_CROSS_FAMILY_REUSE reused=false " +
                $"reason={(baselineQuality == null ? "baseline_window_mismatch" : "baseline_regressed")}");
            return false;
        }

        bool potionFreeWon = IsCompleteVictory(finalPotionFree);
        bool candidateWon = IsCompleteVictory(scout);
        int potionFreeDeficit = StrategicHpDeficit(root, policy, finalPotionFree);
        int candidateDeficit = StrategicHpDeficit(root, policy, scout);
        bool costsComparable = CanComparePotionCosts(policy, scout, finalPotionFree);
        int hpSaved = !costsComparable ? 0 : potionFreeWon
            ? Math.Max(0, potionFreeDeficit - candidateDeficit)
            : candidateWon
                ? Math.Max(0, scout.Snapshot.PlayerHp - finalPotionFree.Snapshot.PlayerHp)
                : 0;
        int hpRequired = SmartPotionHpRequired(root, policy, scout);
        bool protectsLoot = policy.TheftPolicy == SolverTheftPolicy.PreserveResources
            && scout.OutstandingStolenResource < finalPotionFree.OutstandingStolenResource;
        bool acceptable = PotionUsePolicy.CanAcceptSmartAuditVictory(
            potionFreeWon,
            candidateWon,
            hpSaved,
            hpRequired,
            protectsLoot, costsComparable);
        bool improvesSelection = acceptable
            && IsBetterPotionPolicyResult(root, policy, scout, finalPotionFree);
        if (!improvesSelection)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] E3_CROSS_FAMILY_REUSE reused=false reason=revalidation " +
                $"won={candidateWon} saved={hpSaved} required={hpRequired} protects_loot={protectsLoot}");
            return false;
        }

        scout.PotionHpSaved = hpSaved;
        scout.PotionHpRequired = hpRequired;
        selected = scout;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] E3_CROSS_FAMILY_REUSE reused=true saved={hpSaved} " +
            $"required={hpRequired} turn={scout.CombatEndedTurn?.ToString() ?? "-"} boundary={scout.BoundaryReason}");
        return true;
    }

    private static SolverResult AuditSmartPotionUse(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken searchCancellationToken,
        CancellationToken callerCancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return primary;
        try
        {
            return policy.UseE3AdaptivePortfolioScheduling
                ? SearchSmartPotionGradientScheduled(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    searchCancellationToken,
                    callerCancellationToken,
                    profile,
                    primary,
                    memoryForecast,
                    interimResultCallback,
                    adaptive: true)
                : policy.UseE3FixedPortfolioScheduling
                    ? SearchSmartPotionGradientScheduled(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        searchCancellationToken,
                        callerCancellationToken,
                        profile,
                        primary,
                        memoryForecast,
                        interimResultCallback,
                        adaptive: false)
                    : SearchSmartPotionGradient(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    searchCancellationToken,
                    callerCancellationToken,
                    progressCallback,
                    profile,
                    primary,
                    memoryForecast,
                    interimResultCallback);
        }
        catch (PotionPolicyUnsatisfiedException)
            when (policy.PotionPolicy == SolverPotionPolicy.Smart
                && !policy.PotionStrategy.HasForcedDirectives)
        {
            policy.Diagnostics.Info(
                "[CombatSolver/Test] SMART_POTION_AUDIT result optional_route_missing=true selected=primary");
            return primary;
        }
    }

    private sealed class SmartPotionAudit(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult potionFree,
        Action<SolverResult>? publish)
    {
        private readonly SolverResult _potionFree = potionFree;
        private readonly List<SolverResult> _searches = [potionFree];
        internal SolverResult Selected { get; private set; } = potionFree;
        internal bool HasAcceptedLayer { get; private set; }

        internal PotionFreePolicyBaseline Baseline { get; } = new(
            potionFree.Snapshot.AllEnemiesDead && !potionFree.Snapshot.PlayerDead
                && potionFree.Snapshot.ProjectedPlayerHp > 0,
            StrategicHpDeficit(root, policy, potionFree),
            potionFree.Snapshot.PlayerHp,
            potionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = potionFree.Snapshot.ProjectedDeathSaveUseCount,
            BattleHpLost = potionFree.ProjectedBattleHpLost,
            EvaluationWindow = potionFree.EvaluationWindow,
            OutstandingStolenResource = potionFree.OutstandingStolenResource,
        };

        internal bool ReviewCompletedLayer(int potionCount, SolverResult candidate,
            PrimarySearchIncumbent? primaryIncumbent, string? scheduler = null)
        {
            _searches.Add(candidate);
            bool candidateWon = IsCompleteVictory(candidate);
            int candidateDeficit = StrategicHpDeficit(root, policy, candidate);
            bool costsComparable = CanComparePotionCosts(policy, candidate, _potionFree);
            int hpSaved = !costsComparable ? 0 : Baseline.Won
                ? Math.Max(0, Baseline.HpDeficit - candidateDeficit)
                : candidateWon
                    ? Math.Max(0, candidate.Snapshot.PlayerHp - Baseline.PlayerHp)
                    : 0;
            int hpRequired = SmartPotionHpRequired(root, policy, candidate);
            bool protectsLoot = policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                && candidate.OutstandingStolenResource < Baseline.OutstandingStolenResource;
            bool acceptable = PotionUsePolicy.CanAcceptSmartAuditVictory(
                Baseline.Won, candidateWon, hpSaved, hpRequired, protectsLoot, costsComparable);
            bool improvesSelection = acceptable
                && IsBetterPotionPolicyResult(root, policy, candidate, Selected);
            if (improvesSelection)
            {
                candidate.PotionHpSaved = hpSaved;
                candidate.PotionHpRequired = hpRequired;
                Selected = candidate;
                HasAcceptedLayer = true;
                publish?.Invoke(candidate);
            }
            string schedulerSuffix = scheduler == null ? string.Empty : $" scheduler={scheduler}";
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} " +
                $"won={candidateWon} hp_deficit={candidateDeficit} saved={hpSaved} " +
                $"required={hpRequired} protects_loot={protectsLoot} acceptable={acceptable} " +
                $"selected={improvesSelection} expanded={candidate.ExpandedNodes} " +
                $"transitions={candidate.TransitionCount} choice_branches={candidate.ChoiceBranchesEvaluated} " +
                $"elapsed_ms={candidate.Elapsed.TotalMilliseconds:F1} allocated_bytes={candidate.WorkerAllocatedBytes} " +
                $"incumbent_deficit={primaryIncumbent?.StrategicHpDeficit.ToString() ?? "-"} " +
                $"incumbent_turn={primaryIncumbent?.CombatEndedTurn.ToString() ?? "-"} " +
                $"incumbent_pruned={candidate.PrimaryIncumbentBranchesPruned} " +
                $"incumbent_updates={candidate.PrimaryIncumbentUpdates}{schedulerSuffix}");
            return improvesSelection && TheftEncounterStrategy.RecoverySatisfied(
                policy.TheftPolicy, Selected.OutstandingStolenResource);
        }

        internal void MergeTotals() => MergeAuditTotals(Selected, [.. _searches]);
    }

    private static SolverResult SearchSmartPotionGradientScheduled(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken searchCancellationToken,
        CancellationToken callerCancellationToken,
        SolverSearchProfile profile,
        SolverResult potionFree,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback,
        bool adaptive)
    {
        int forcedPotionCount = policy.PotionStrategy.ForcedDirectiveCount;
        if (potionFree.ExplicitPotionCount != forcedPotionCount)
            throw new InvalidOperationException("Smart 梯度搜索必须从仅满足强制用药的结果开始。");

        string scheduler = adaptive ? "e3_adaptive" : "e3_fixed";
        SmartPotionAudit audit = new(root, policy, potionFree, interimResultCallback);
        PotionFreePolicyBaseline baseline = audit.Baseline;
        int maximumOptionalPotionUses = MaximumSmartPotionUses(root, policy, baseline.Won, baseline.HpDeficit);
        if (maximumOptionalPotionUses == 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
                $"stop=no_potion_acceptable hp_deficit={baseline.HpDeficit} maximum=0 scheduler={scheduler}");
            return potionFree;
        }

        bool deadlineExpired = false;
        int firstPotionCount = forcedPotionCount + 1;
        int lastPotionCount = forcedPotionCount + maximumOptionalPotionUses;
        int nextPotionCountToStart = firstPotionCount;
        int nextPotionCountToCommit = firstPotionCount;
        int residentLimit = policy.MemoryPressureSignal.ConservativeParallelismRequired
            ? 1
            : Math.Min(3, maximumOptionalPotionUses);
        PrimarySearchIncumbent? primaryIncumbent = BuildPrimarySearchIncumbent(
            root,
            policy,
            potionFree);

        Dictionary<int, CombatBeamSolver.SearchMemberExecutionSession> active = [];
        Dictionary<int, SolverResult> completed = [];
        HashSet<int> missing = [];
        Dictionary<int, int> slices = [];
        Dictionary<int, double> marginalRates = [];
        Dictionary<int, SolverInterimResult> observedIncumbents = [];
        Dictionary<int, bool> importantCandidates = [];
        Dictionary<int, E3AdaptivePriority> pendingPriorities = [];
        int epoch = 0;

        void StartLayer(int potionCount)
        {
            CombatBeamSolver solver = new(
                root,
                displayNames,
                battleDamage,
                policy.MeasurePhasePerformance ? policy with { RequestMemberKind = SearchRequestMemberKind.SmartPotion } : policy,
                callerCancellationToken,
                progressCallback: null,
                profile,
                SolverPotionPolicy.RequireAtLeastOne,
                baseline,
                maximumPotionUses: potionCount,
                minimumPotionUses: potionCount,
                primaryIncumbent: primaryIncumbent);
            active.Add(potionCount, solver.CreateExecutionSession());
            slices[potionCount] = 0;
            marginalRates[potionCount] = 0d;
            importantCandidates[potionCount] = false;
            pendingPriorities[potionCount] = E3AdaptivePriority.None;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] E3_PORTFOLIO_MEMBER_START kind=smart_potion " +
                $"potion_count={potionCount} resident={active.Count}/{residentLimit} " +
                $"slice_parents={E3FixedMemberAllowance.MaxParentCommits} " +
                $"slice_transitions={E3FixedMemberAllowance.MaxTransitions} scheduler={scheduler}");
        }

        void FillResidentSet()
        {
            while (active.Count < residentLimit && nextPotionCountToStart <= lastPotionCount)
            {
                if (searchCancellationToken.IsCancellationRequested)
                    return;
                StartLayer(nextPotionCountToStart++);
            }
        }

        void ObserveCompletedLayer(
            int potionCount,
            CombatBeamSolver.SearchMemberExecutionSession session,
            SolverResult? result)
        {
            ObserveSmartLayerMemorySample(
                policy,
                memoryForecast,
                result,
                profile,
                potionCount,
                session.AllocatedBytesForScheduling,
                session.TransitionCountForScheduling);
        }

        void ObserveAdaptiveSignal(
            int potionCount,
            CombatBeamSolver.SearchMemberExecutionSession session,
            double exclusiveMilliseconds)
        {
            if (!adaptive)
                return;

            observedIncumbents.TryGetValue(potionCount, out SolverInterimResult? previous);
            SolverInterimResult? current = session.CurrentBestResultForScheduling;
            E3AdaptiveObservation observation = E3AdaptiveBudgetPolicy.Observe(
                previous,
                current,
                marginalRates.GetValueOrDefault(potionCount),
                exclusiveMilliseconds);
            marginalRates[potionCount] = observation.MarginalImprovementRate;
            if (current != null)
                observedIncumbents[potionCount] = current;
            if (observation.ImportantImprovement)
                importantCandidates[potionCount] = true;
            if (observation.Priority > pendingPriorities.GetValueOrDefault(potionCount))
                pendingPriorities[potionCount] = observation.Priority;
        }

        void ObserveSchedulingBoundary()
        {
            if (policy.MemoryPressureSignal.ConservativeParallelismRequired && residentLimit > 1)
            {
                residentLimit = 1;
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] E3_PORTFOLIO_MEMORY " +
                    $"scheduler={scheduler} resident_limit=1 active={active.Count} " +
                    $"remaining_bytes={policy.MemoryPressureSignal.RemainingBytes}");
            }
        }

        SolverResult? StepLayer(int potionCount)
        {
            if (!active.TryGetValue(
                    potionCount,
                    out CombatBeamSolver.SearchMemberExecutionSession? session))
            {
                return null;
            }

            double exclusiveBefore = session.ExclusiveElapsedForScheduling.TotalMilliseconds;
            SearchStepResult step;
            try
            {
                step = session.Step(E3FixedMemberAllowance, callerCancellationToken, searchCancellationToken);
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                ObserveCompletedLayer(potionCount, session, result: null);
                missing.Add(potionCount);
                active.Remove(potionCount);
                session.Dispose();
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} " +
                    $"route_missing=true scheduler={scheduler}");
                return null;
            }

            double exclusiveDelta = Math.Max(
                0d,
                session.ExclusiveElapsedForScheduling.TotalMilliseconds - exclusiveBefore);
            slices[potionCount] = checked(slices.GetValueOrDefault(potionCount) + 1);
            ObserveAdaptiveSignal(potionCount, session, exclusiveDelta);
            ObserveSchedulingBoundary();
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] E3_PORTFOLIO_SLICE kind=smart_potion " +
                $"potion_count={potionCount} status={step.Status} " +
                $"committed_parents={step.CommittedParents} total_parents={step.TotalCommittedParents} " +
                $"expanded={session.ExpandedNodesForScheduling} " +
                $"transitions={session.TransitionCountForScheduling} " +
                $"exclusive_ms={session.ExclusiveElapsedForScheduling.TotalMilliseconds:F3} " +
                $"slice_exclusive_ms={exclusiveDelta:F3} " +
                $"marginal_rate={marginalRates.GetValueOrDefault(potionCount):F6} " +
                $"important={importantCandidates.GetValueOrDefault(potionCount).ToString().ToLowerInvariant()} " +
                $"priority={pendingPriorities.GetValueOrDefault(potionCount)} " +
                $"slices={slices[potionCount]} resident={active.Count}/{residentLimit} scheduler={scheduler}");

            if (step.Status == SearchStepStatus.Canceled)
            {
                callerCancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(callerCancellationToken);
            }
            if (step.Status == SearchStepStatus.BudgetExhausted)
            {
                ObserveCompletedLayer(potionCount, session, result: null);
                active.Remove(potionCount);
                missing.Add(potionCount);
                session.Dispose();
                return null;
            }
            if (step.Status != SearchStepStatus.Completed)
                return null;

            SolverResult candidate = session.Result
                ?? throw new InvalidOperationException("E3 用药成员完成但没有结果。");
            ObserveCompletedLayer(potionCount, session, candidate);
            active.Remove(potionCount);
            session.Dispose();
            if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;
            candidate.SingleSessionSearch = true;
            PopulateSingleSessionTotals(candidate);
            completed.Add(potionCount, candidate);
            return null;
        }

        bool CommitCompletedInOrder()
        {
            while (nextPotionCountToCommit <= lastPotionCount
                && (completed.ContainsKey(nextPotionCountToCommit)
                    || missing.Contains(nextPotionCountToCommit)))
            {
                int potionCount = nextPotionCountToCommit++;
                if (missing.Remove(potionCount))
                    continue;

                SolverResult candidate = completed[potionCount];
                completed.Remove(potionCount);
                if (audit.ReviewCompletedLayer(potionCount, candidate, primaryIncumbent, scheduler))
                    return true;
            }
            return false;
        }

        FillResidentSet();
        try
        {
            while (active.Count > 0)
            {
                epoch++;
                // Hard exploration floor: every resident member gets one transition-bounded slice.
                foreach (int potionCount in active.Keys.OrderBy(value => value).ToArray())
                {
                    if (searchCancellationToken.IsCancellationRequested)
                        break;
                    if (StepLayer(potionCount) is { } takeover)
                        return takeover;
                }

                if (searchCancellationToken.IsCancellationRequested)
                    break;
                if (CommitCompletedInOrder())
                    break;

                if (adaptive && active.Count > 0)
                {
                    E3AdaptiveMemberSignal[] signals = active.Keys
                        .OrderBy(value => value)
                        .Select((potionCount, index) => new E3AdaptiveMemberSignal(
                            potionCount,
                            slices.GetValueOrDefault(potionCount),
                            ExplorationDue: false,
                            ImportantCandidate: importantCandidates.GetValueOrDefault(potionCount),
                            Priority: pendingPriorities.GetValueOrDefault(potionCount),
                            MarginalImprovementRate: marginalRates.GetValueOrDefault(potionCount),
                            TieOrder: index))
                        .ToArray();
                    int? bonusPotionCount = E3AdaptiveBudgetPolicy.SelectBonusMember(signals);
                    if (bonusPotionCount is { } bonus && active.ContainsKey(bonus))
                    {
                        policy.PortfolioTelemetry?.RecordE3AdaptiveBonusSlice();
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] E3_ADAPTIVE_BUDGET epoch={epoch} bonus_member={bonus} " +
                            $"priority={pendingPriorities.GetValueOrDefault(bonus)} " +
                            $"important={importantCandidates.GetValueOrDefault(bonus).ToString().ToLowerInvariant()} " +
                            $"marginal_rate={marginalRates.GetValueOrDefault(bonus):F6} " +
                            $"slices={slices.GetValueOrDefault(bonus)} active={active.Count}");
                        importantCandidates[bonus] = false;
                        pendingPriorities[bonus] = E3AdaptivePriority.None;
                        if (StepLayer(bonus) is { } takeover)
                            return takeover;
                        if (searchCancellationToken.IsCancellationRequested)
                            break;
                        if (CommitCompletedInOrder())
                            break;
                    }
                }

                FillResidentSet();
            }

            if (searchCancellationToken.IsCancellationRequested)
            {
                deadlineExpired = true;
                callerCancellationToken.ThrowIfCancellationRequested();
                foreach (int potionCount in active.Keys.OrderBy(value => value).ToArray())
                    if (StepLayer(potionCount) is { } takeover)
                        return takeover;
            }
        }
        finally
        {
            foreach (CombatBeamSolver.SearchMemberExecutionSession session in active.Values)
                session.Dispose();
            active.Clear();
        }

        callerCancellationToken.ThrowIfCancellationRequested();
        // Once stepping stops, unfinished lower layers cannot block already
        // materialized higher layers. Review them without opening another slice.
        foreach (var (potionCount, candidate) in completed.OrderBy(layer => layer.Key))
            audit.ReviewCompletedLayer(potionCount, candidate, primaryIncumbent, scheduler);
        audit.MergeTotals();
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
            $"stop={(deadlineExpired ? "deadline" : audit.HasAcceptedLayer ? "threshold_met" : "complete")} " +
            $"maximum={lastPotionCount} selected_potions={audit.Selected.PotionCount} scheduler={scheduler}");
        return audit.Selected;
    }

    private static SolverResult SearchSmartPotionGradient(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken searchCancellationToken,
        CancellationToken callerCancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult potionFree,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback)
    {
        int forcedPotionCount = policy.PotionStrategy.ForcedDirectiveCount;
        if (potionFree.ExplicitPotionCount != forcedPotionCount)
            throw new InvalidOperationException("Smart 梯度搜索必须从仅满足强制用药的结果开始。");

        SmartPotionAudit audit = new(root, policy, potionFree, interimResultCallback);
        PotionFreePolicyBaseline baseline = audit.Baseline;
        int maximumOptionalPotionUses = MaximumSmartPotionUses(root, policy, baseline.Won, baseline.HpDeficit);
        if (maximumOptionalPotionUses == 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
                $"stop=no_potion_acceptable hp_deficit={baseline.HpDeficit} maximum=0");
            return potionFree;
        }

        bool deadlineExpired = false;
        // The layer's route remains provisional until the Smart comparison against
        // the no-optional-potion result. Forward work counters during the audit.
        Action<SolverProgress>? auditProgress = progressCallback == null ? null : progress => progressCallback(progress with
        {
            CurrentBestResult = null,
            CurrentTurnPreview = null,
            SpeculativeRoutePreview = null,
            RouteAdoptionSeed = null,
            CurrentTurnAdoptionSeed = null,
            OfficialPublishedOrigin = null,
            OfficialPublishedEvaluationContextId = null,
        });
        for (int optionalPotionCount = 1;
             optionalPotionCount <= maximumOptionalPotionUses;
             optionalPotionCount++)
        {
            int potionCount = forcedPotionCount + optionalPotionCount;
            if (searchCancellationToken.IsCancellationRequested)
            {
                callerCancellationToken.ThrowIfCancellationRequested();
                deadlineExpired = true;
                break;
            }
            try
            {
                ReclaimAtPotionGradientBoundary(
                    policy,
                    searchCancellationToken,
                    progressCallback,
                    profile,
                    potionFree,
                    memoryForecast,
                    potionCount - 1,
                    potionCount);
            }
            catch (OperationCanceledException)
                when (searchCancellationToken.IsCancellationRequested
                    && !callerCancellationToken.IsCancellationRequested)
            {
                deadlineExpired = true;
                break;
            }
            PrimarySearchIncumbent? primaryIncumbent = BuildPrimarySearchIncumbent(
                root,
                policy,
                audit.Selected);
            long layerAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            long layerTransitionsAtStart = policy.RequestWorkTotals?.Snapshot().TransitionCount ?? 0;
            SolverResult? observedLayerResult = null;
            SolverResult? candidate;
            try
            {
                candidate = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy.MeasurePhasePerformance ? policy with { RequestMemberKind = SearchRequestMemberKind.SmartPotion } : policy,
                    callerCancellationToken,
                    auditProgress,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    baseline,
                    maximumPotionUses: potionCount,
                    minimumPotionUses: potionCount,
                    primaryIncumbent: primaryIncumbent).Solve(searchCancellationToken);
                observedLayerResult = candidate;
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} route_missing=true");
                continue;
            }
            catch (OperationCanceledException)
                when (searchCancellationToken.IsCancellationRequested
                    && !callerCancellationToken.IsCancellationRequested)
            {
                deadlineExpired = true;
                break;
            }
            finally
            {
                // Request totals include a solver that failed or was canceled. Use its actual
                // interval, never the selected route's work paired with another layer's bytes.
                ObserveSmartLayerMemory(
                    policy, memoryForecast, layerAllocatedAtStart, layerTransitionsAtStart,
                    observedLayerResult, profile, potionCount);
            }
            if (candidate == null)
            {
                deadlineExpired = true;
                break;
            }
            if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;

            candidate.SingleSessionSearch = true;
            PopulateSingleSessionTotals(candidate);
            if (audit.ReviewCompletedLayer(potionCount, candidate, primaryIncumbent))
                break;
        }

        callerCancellationToken.ThrowIfCancellationRequested();
        audit.MergeTotals();
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
            $"stop={(deadlineExpired ? "deadline" : audit.HasAcceptedLayer ? "threshold_met" : "complete")} " +
            $"maximum={forcedPotionCount + maximumOptionalPotionUses} " +
            $"selected_potions={audit.Selected.PotionCount}");
        return audit.Selected;
    }

    private static bool CanComparePotionCosts(SearchPolicySnapshot policy, SolverResult candidate, SolverResult baseline)
        => !SearchEvaluationPolicy.UsesLocalEvaluationWindows(policy.RoutePolicy)
            || RouteEvaluationWindow.CanCompare(candidate.EvaluationWindow, baseline.EvaluationWindow);

    private static void ObserveSmartLayerMemorySample(
        SearchPolicySnapshot policy,
        SmartLayerMemoryForecast forecast,
        SolverResult? result,
        SolverSearchProfile profile,
        int completedPotionCount,
        long processAllocated,
        long transitions)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return;
        processAllocated = Math.Max(0, processAllocated);
        transitions = Math.Max(0, transitions);
        bool usableSample = result is { ResultScope: SolverResultScope.SearchCompletion }
            && result.BoundaryReason != SearchBoundaryReason.TimeLimit
            && result.Elapsed.TotalMilliseconds < profile.SoftTimeBudgetMilliseconds;
        forecast.Observe(processAllocated, transitions, usableSample);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_LAYER_MEMORY_SAMPLE layer={completedPotionCount} " +
            $"process_allocated_bytes={processAllocated} transitions={transitions} " +
            $"sample_usable={usableSample.ToString().ToLowerInvariant()} " +
            $"boundary={result?.BoundaryReason.ToString() ?? "incomplete"} " +
            $"bytes_per_transition_high_water={forecast.BytesPerTransitionHighWater:F1} " +
            $"prediction_error_high_water={forecast.UnderpredictionHighWater:F3}");
    }

    private static void ObserveSmartLayerMemory(
        SearchPolicySnapshot policy,
        SmartLayerMemoryForecast forecast,
        long processAllocatedAtStart,
        long transitionsAtStart,
        SolverResult? result,
        SolverSearchProfile profile,
        int completedPotionCount)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return;
        long processAllocated = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: false) - processAllocatedAtStart);
        long transitions = Math.Max(
            0,
            (policy.RequestWorkTotals?.Snapshot().TransitionCount ?? 0) - transitionsAtStart);
        // A fixed node budget is a comparable work window for the next layer using this same
        // profile. A timed-out or interrupted layer can understate that window, so keep the
        // optional reset conservative until a complete observation is available again.
        ObserveSmartLayerMemorySample(
            policy,
            forecast,
            result,
            profile,
            completedPotionCount,
            processAllocated,
            transitions);
    }

    private static void ReclaimAtPotionGradientBoundary(
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult totalsCarrier,
        SmartLayerMemoryForecast forecast,
        int completedPotionCount,
        int nextPotionCount)
    {
        SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
        cancellationToken.ThrowIfCancellationRequested();
        SmartLayerMemoryDecision decision = forecast.Decide(
            signal.IsEnabled,
            signal.HasUnexpectedNoGcLoss(),
            signal.AllocatedBytes,
            signal.RemainingBytes,
            signal.AllocationLimitBytes);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] POTION_GRADIENT_MEMORY_DECISION " +
            $"completed_layer={completedPotionCount} next_layer={nextPotionCount} " +
            $"reclaim={decision.ShouldReclaim.ToString().ToLowerInvariant()} reason={decision.Reason} " +
            $"forecast_bytes={decision.ForecastBytes} remaining_bytes={decision.RemainingBytes} " +
            $"observations={forecast.ObservationCount} minimum_transition_growth=2 allocation_safety_factor=1.5");
        if (!decision.ShouldReclaim)
            return;

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        SearchGcLifecycleSnapshot lifecycleBefore = signal.CaptureGcLifecycle();
        Stopwatch stopwatch = Stopwatch.StartNew();
        progressCallback?.Invoke(new SolverProgress(
            totalsCarrier.StartTurnNumber,
            totalsCarrier.StartTurnNumber + Math.Max(0, totalsCarrier.SearchedTurns - 1),
            totalsCarrier.SearchedTurns,
            PlayDepth: 0,
            // A memory reset is a coordinator-owned interval between solvers. Publish a
            // zero-based interval so the request progress accumulator closes the preceding
            // solver exactly once and does not count potionFree again before every layer.
            ExpandedNodes: 0,
            ReviewedWorldlines: 0,
            MaxNodes: profile.MaxExpandedNodes,
            FrontierNodes: 0,
            EndedNodes: 1,
            ElapsedMilliseconds: 0,
            Phase: "切换用药路线，正在整理内存"));
        long pressureBefore = signal.AllocatedBytes;
        long limitBefore = signal.AllocationLimitBytes;
        try
        {
            signal.ReclaimAndContinue(cancellationToken, "smart_potion_layer");
        }
        finally
        {
            // ReclaimWithinSearch can observe a deadline after completing its blocking Gen2.
            // Retain that completed work in request totals even when cancellation then unwinds.
            stopwatch.Stop();
            TimeSpan gcPause = GC.GetTotalPauseDuration() - pauseBefore;
            TimeSpan maxObservedGcPause = signal.LastReclaimMaxObservedGcPause;
            long allocatedBytes = Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
            int gen0Collections = GC.CollectionCount(0) - gen0Before;
            int gen1Collections = GC.CollectionCount(1) - gen1Before;
            int gen2Collections = GC.CollectionCount(2) - gen2Before;
            totalsCarrier.TotalWorkerAllocatedBytes = checked(
                totalsCarrier.TotalWorkerAllocatedBytes
                + allocatedBytes);
            totalsCarrier.TotalGen0Collections += gen0Collections;
            totalsCarrier.TotalGen1Collections += gen1Collections;
            totalsCarrier.TotalGen2Collections += gen2Collections;
            totalsCarrier.TotalGcPauseDuration += gcPause;
            if (maxObservedGcPause > totalsCarrier.TotalMaxObservedGcPause)
                totalsCarrier.TotalMaxObservedGcPause = maxObservedGcPause;
            totalsCarrier.TotalSearchElapsed += stopwatch.Elapsed;
            policy.RequestWorkTotals?.RecordCoordinatorOverhead(
                stopwatch.Elapsed,
                allocatedBytes,
                gen0Collections,
                gen1Collections,
                gen2Collections,
                gcPause,
                maxObservedGcPause);

            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_GRADIENT_MEMORY_RESET " +
                $"completed_layer={completedPotionCount} next_layer={nextPotionCount} " +
                $"allocated_before={pressureBefore} limit_before={limitBefore} " +
                $"allocated_after={signal.AllocatedBytes} limit_after={signal.AllocationLimitBytes} " +
                $"gc_pause_ms={gcPause.TotalMilliseconds:F1} " +
                $"max_observed_gc_pause_ms={maxObservedGcPause.TotalMilliseconds:F1} " +
                signal.CaptureGcLifecycle().DeltaFrom(lifecycleBefore).ToDiagnosticString() + " " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                $"canceled={cancellationToken.IsCancellationRequested.ToString().ToLowerInvariant()}");
        }
    }

    private static SolverResult? SolveOptionalPotionPosterior(
        CombatBeamSolver solver,
        SearchPolicySnapshot policy,
        string diagnostic,
        SupplementalAuditStage? stage = null)
    {
        try
        {
            return stage is null ? solver.Solve() : stage.Solve(solver);
        }
        catch (PotionPolicyUnsatisfiedException)
        {
            policy.Diagnostics.Info($"[CombatSolver/Test] {diagnostic} qualified=false");
            return null;
        }
    }

}
