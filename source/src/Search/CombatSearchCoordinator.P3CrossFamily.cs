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
        Action<SolverResult>? interimResultCallback)
    {
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

        using CombatBeamSolver.SearchMemberExecutionSession potionSession =
            new CombatBeamSolver(
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

        SolverResult? beamResult = null;
        SolverResult? potionResult = null;
        bool beamDone = false;
        bool potionDone = false;
        bool potionMissing = false;
        int rounds = 0;

        const int beamSlicesPerPotionSlice = 4;
        while (!beamDone || !potionDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rounds = checked(rounds + 1);

            for (int beamSlice = 0;
                 beamSlice < beamSlicesPerPotionSlice && !beamDone;
                 beamSlice++)
            {
                SearchStepResult step = beamSession.Step(
                    E3FixedMemberAllowance,
                    cancellationToken);
                if (step.Status == SearchStepStatus.Completed)
                {
                    beamResult = beamSession.Result
                        ?? throw new InvalidOperationException("P3 Beam member completed without a result.");
                    beamDone = true;
                }
                else if (step.Status == SearchStepStatus.Canceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(cancellationToken);
                }
                else if (step.Status == SearchStepStatus.BudgetExhausted)
                {
                    throw new InvalidOperationException("P3 Beam member exhausted without a result.");
                }
            }

            if (!potionDone)
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

        if (beamResult == null)
            throw new InvalidOperationException("P3 cross-family scheduler produced no Beam result.");

        beamResult.SingleSessionSearch = true;
        PopulateSingleSessionTotals(beamResult);

        if (potionResult != null)
        {
            potionResult.SingleSessionSearch = true;
            PopulateSingleSessionTotals(potionResult);
        }

        SolverResult selected = beamResult;
        bool potionAccepted = false;
        int potionFreeDeficit = StrategicHpDeficit(root, policy, beamResult);
        int candidateDeficit = potionResult == null
            ? int.MaxValue
            : StrategicHpDeficit(root, policy, potionResult);
        int hpSaved = 0;
        int hpRequired = int.MaxValue;

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
                bool acceptable = IsSmartPotionGradientCandidateAcceptable(
                    potionFreeWon,
                    candidateWon,
                    hpSaved,
                    hpRequired,
                    protectsLoot);
                potionAccepted = acceptable
                    && (policy.TheftPolicy != SolverTheftPolicy.PreserveResources
                        || IsBetterCompletedResult(root, policy, potionResult, beamResult));
                if (potionAccepted)
                {
                    potionResult.PotionHpSaved = hpSaved;
                    potionResult.PotionHpRequired = hpRequired;
                    selected = potionResult;
                }
            }
        }

        if (IsCompleteVictory(beamResult))
            interimResultCallback?.Invoke(beamResult);
        if (potionAccepted && IsCompleteVictory(selected))
            interimResultCallback?.Invoke(selected);

        if (potionResult != null)
            MergeAuditTotals(selected, beamResult, potionResult);
        else
            MergeAuditTotals(selected, beamResult);

        policy.Diagnostics.Info(
            $"[CombatSolver/Test] P3_CROSS_FAMILY_FIXED " +
            $"rounds={rounds} beam_to_potion={beamSlicesPerPotionSlice}:1 " +
            $"node_budget={sharedBudget.ExpandedNodes}/{sharedBudget.MaxExpandedNodes} " +
            $"elapsed_ms={sharedBudget.ElapsedMilliseconds}/{sharedBudget.BudgetMilliseconds} " +
            $"beam_boundary={beamResult.BoundaryReason} beam_hp={potionFreeDeficit} " +
            $"potion_missing={potionMissing.ToString().ToLowerInvariant()} " +
            $"potion_boundary={potionResult?.BoundaryReason.ToString() ?? "-"} " +
            $"potion_hp={candidateDeficit} saved={hpSaved} required={hpRequired} " +
            $"selected={(ReferenceEquals(selected, potionResult) ? "potion" : "beam")}");

        return selected;
    }
}
