namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    internal static int CompareP3FinalQualityForTesting(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        if (IsBetterPotionPolicyResult(root, policy, candidate, current))
            return -1;
        if (IsBetterPotionPolicyResult(root, policy, current, candidate))
            return 1;
        return 0;
    }

    private static SolverResult RunP3CrossFamilyFixedPass(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        Action<SolverResult>? interimResultCallback,
        out SolverResult? earlyPotionScout)
    {
        earlyPotionScout = null;
        if (!policy.UseP3CrossFamilyScheduling)
            throw new InvalidOperationException("P3 cross-family scheduler was not enabled.");
        if (policy.PotionPolicy != SolverPotionPolicy.Smart
            || policy.PotionStrategy.HasForcedDirectives
            || policy.UseNoveltyPortfolio
            || policy.UseBeamWidthPortfolio)
        {
            throw new InvalidOperationException(
                "P3 cross-family experiment requires Smart/no-forced, Novelty off, Beam portfolio off.");
        }

        SearchRequestWallClockBudget sharedBudget = new(
            profile.SoftTimeBudgetMilliseconds,
            profile.MaxExpandedNodes);
        SearchPolicySnapshot memberPolicy = policy with
        {
            P3SharedWallClockBudget = sharedBudget,
        };

        using CombatBeamSolver.SearchMemberExecutionSession beamSession =
            new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                memberPolicy,
                cancellationToken,
                progressCallback,
                profile,
                potionPolicyOverride: SolverPotionPolicy.Disabled)
            .CreateExecutionSession();

        SolverResult? beamResult = null;
        SolverResult? potionResult = null;
        CombatBeamSolver.SearchMemberExecutionSession? potionSession = null;
        bool beamDone = false;
        bool potionDone = false;
        bool potionMissing = false;
        bool potionSchedulingEnabled = false;
        bool potionSchedulingSkippedForColdQuality = false;
        int rounds = 0;

        const int beamSlicesPerPotionSlice = 4;
        int minimumBeamWarmupNodes = Math.Max(1, profile.MaxExpandedNodes / 3);
        long warmupDeadlineMs = Math.Max(
            1,
            profile.SoftTimeBudgetMilliseconds * 3L / 5L);

        SearchStepResult StepBeam()
        {
            SearchStepResult step = beamSession.Step(
                E3FixedMemberAllowance,
                cancellationToken);
            if (step.Status == SearchStepStatus.Completed)
            {
                beamResult = beamSession.Result
                    ?? throw new InvalidOperationException(
                        "P3 Beam member completed without a result.");
                beamDone = true;
            }
            else if (step.Status == SearchStepStatus.Canceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
            else if (step.Status == SearchStepStatus.BudgetExhausted)
            {
                throw new InvalidOperationException(
                    "P3 Beam member exhausted without a result.");
            }
            return step;
        }

        try
        {
            // Quality-first warm-up. A cold process can spend a large fraction of the request
            // on JIT/static initialization. Do not let a second family steal that first request:
            // require meaningful Beam work and a provisional win before starting potion search.
            while (!beamDone
                && sharedBudget.HasExpandedNodeBudgetRemaining
                && !sharedBudget.IsExpired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = StepBeam();

                SolverInterimResult? incumbent =
                    beamSession.CurrentBestResultForScheduling;
                bool enoughBeamWork =
                    sharedBudget.ExpandedNodes >= minimumBeamWarmupNodes;
                bool provisionalWin = incumbent?.Won == true;
                if (enoughBeamWork && provisionalWin)
                {
                    potionSchedulingEnabled = true;
                    break;
                }

                if (sharedBudget.ElapsedMilliseconds >= warmupDeadlineMs)
                {
                    potionSchedulingSkippedForColdQuality = true;
                    break;
                }
            }

            if (beamDone && beamResult != null && IsCompleteVictory(beamResult)
                && sharedBudget.HasExpandedNodeBudgetRemaining
                && !sharedBudget.IsExpired)
            {
                potionSchedulingEnabled = true;
            }

            if (potionSchedulingEnabled)
            {
                potionSession = new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        memberPolicy,
                        cancellationToken,
                        progressCallback: null,
                        profile,
                        potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                        potionFreePolicyBaseline: null,
                        maximumPotionUses: 1,
                        minimumPotionUses: 1)
                    .CreateExecutionSession();
            }
            else
            {
                potionDone = true;
            }

            while (!beamDone || !potionDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rounds = checked(rounds + 1);

                for (int beamSlice = 0;
                     beamSlice < beamSlicesPerPotionSlice && !beamDone;
                     beamSlice++)
                {
                    _ = StepBeam();
                }

                if (!potionDone && potionSession != null)
                {
                    try
                    {
                        SearchStepResult step = potionSession.Step(
                            E3FixedMemberAllowance,
                            cancellationToken);
                        if (step.Status == SearchStepStatus.Completed)
                        {
                            potionResult = potionSession.Result
                                ?? throw new InvalidOperationException(
                                    "P3 potion member completed without a result.");
                            potionDone = true;
                        }
                        else if (step.Status == SearchStepStatus.Canceled)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new OperationCanceledException(cancellationToken);
                        }
                        else if (step.Status == SearchStepStatus.BudgetExhausted)
                        {
                            throw new InvalidOperationException(
                                "P3 potion member exhausted without a result.");
                        }
                    }
                    catch (PotionPolicyUnsatisfiedException)
                    {
                        potionMissing = true;
                        potionDone = true;
                    }
                }

                if (rounds > profile.MaxExpandedNodes + 1024)
                {
                    throw new InvalidOperationException(
                        "P3 cross-family scheduler exceeded its deterministic round guard.");
                }
            }
        }
        finally
        {
            potionSession?.Dispose();
        }

        if (beamResult == null)
            throw new InvalidOperationException("P3 cross-family scheduler produced no Beam result.");

        beamResult.SingleSessionSearch = true;
        PopulateSingleSessionTotals(beamResult);

        if (potionResult != null)
        {
            potionResult.SingleSessionSearch = true;
            PopulateSingleSessionTotals(potionResult);
        }

        int potionFreeDeficit = StrategicHpDeficit(root, policy, beamResult);
        int candidateDeficit = potionResult == null
            ? int.MaxValue
            : StrategicHpDeficit(root, policy, potionResult);
        int hpSaved = 0;
        int hpRequired = int.MaxValue;
        bool scoutWouldQualify = false;

        if (potionResult is { ResultScope: SolverResultScope.SearchCompletion })
        {
            bool potionFreeWon = IsCompleteVictory(beamResult);
            bool candidateWon = IsCompleteVictory(potionResult);
            int maximumOptionalPotionUses = MaximumSmartPotionUses(
                root,
                policy,
                potionFreeWon,
                potionFreeDeficit);
            if (maximumOptionalPotionUses > 0)
            {
                hpSaved = potionFreeWon
                    ? Math.Max(0, potionFreeDeficit - candidateDeficit)
                    : candidateWon
                        ? Math.Max(0, potionResult.Snapshot.PlayerHp - beamResult.Snapshot.PlayerHp)
                        : 0;
                hpRequired = SmartPotionHpRequired(root, policy, potionResult);
                bool protectsLoot = policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                    && potionResult.OutstandingStolenResource < beamResult.OutstandingStolenResource;
                scoutWouldQualify = IsSmartPotionGradientCandidateAcceptable(
                    potionFreeWon,
                    candidateWon,
                    hpSaved,
                    hpRequired,
                    protectsLoot)
                    && (policy.TheftPolicy != SolverTheftPolicy.PreserveResources
                        || IsBetterCompletedResult(root, policy, potionResult, beamResult));
                if (scoutWouldQualify)
                {
                    potionResult.PotionHpSaved = hpSaved;
                    potionResult.PotionHpRequired = hpRequired;
                }
            }
        }

        // P3 is only a scheduling optimization. Keep the formal no-potion result as the
        // primary result and hand the early potion result to the existing Smart audit,
        // which revalidates it against the completed no-potion baseline before reuse.
        earlyPotionScout = potionResult;

        policy.Diagnostics.Info(
            $"[CombatSolver/Test] P3_CROSS_FAMILY_FIXED " +
            $"rounds={rounds} beam_to_potion={beamSlicesPerPotionSlice}:1 " +
            $"warmup_nodes={minimumBeamWarmupNodes} warmup_deadline_ms={warmupDeadlineMs} " +
            $"potion_scheduled={potionSchedulingEnabled.ToString().ToLowerInvariant()} " +
            $"cold_quality_skip={potionSchedulingSkippedForColdQuality.ToString().ToLowerInvariant()} " +
            $"node_budget={sharedBudget.ExpandedNodes}/{sharedBudget.MaxExpandedNodes} " +
            $"elapsed_ms={sharedBudget.ElapsedMilliseconds}/{sharedBudget.BudgetMilliseconds} " +
            $"beam_boundary={beamResult.BoundaryReason} beam_hp={potionFreeDeficit} " +
            $"potion_missing={potionMissing.ToString().ToLowerInvariant()} " +
            $"potion_boundary={potionResult?.BoundaryReason.ToString() ?? "-"} " +
            $"potion_hp={candidateDeficit} saved={hpSaved} required={hpRequired} " +
            $"scout_would_qualify={scoutWouldQualify.ToString().ToLowerInvariant()}");

        return beamResult;
    }
}
