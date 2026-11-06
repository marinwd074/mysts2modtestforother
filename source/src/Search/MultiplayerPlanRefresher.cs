using System.Diagnostics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal enum MultiplayerPlanRefreshDecision
{
    Continue,
    Reselect,
    FullRestart,
}

internal sealed record MultiplayerPlanRefreshResult(
    MultiplayerPlanRefreshDecision Decision,
    SolverResult? Result,
    int ReplayedCandidates,
    bool FirstActionChanged,
    string Reason)
{
    public MultiplayerPlanRefreshMetrics? Metrics { get; init; }
}

internal sealed record MultiplayerPlanRefreshMetrics(
    int AttemptedCandidates, int RejectedCandidates, int TruncatedCandidates,
    int PrefixTransitions, double PrefixMilliseconds, double MaterializationMilliseconds,
    double TotalMilliseconds);

/// <summary>
/// Evaluates retained local prefixes against one frozen root. Owns no live state,
/// UI, or execution authorization; Runtime alone accepts and publishes the result.
/// </summary>
internal static class MultiplayerPlanRefresher
{
    private readonly record struct BoundedReplayEvaluation(
        MultiplayerReplayCandidate Candidate,
        MultiplayerCombatObjectiveRank Objective,
        double Score);

    private const int BoundedRefreshCandidateLimit = 3;
    private const int BoundedRefreshPrefixActionLimit = 2;
    private const int BoundedRefreshBeamWidth = 24;
    private const int BoundedRefreshExpandedNodeLimit = 192;
    private const int BoundedRefreshTimeLimitMilliseconds = 60;

    internal static MultiplayerPlanRefreshResult Refresh(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot searchPolicy,
        IReadOnlyList<MultiplayerReplayCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch clock = Stopwatch.StartNew();
        int attempted = 0, rejected = 0, truncated = 0, prefixTransitions = 0;
        double prefixMilliseconds = 0, materializationMilliseconds = 0;
        int initialEnemyMaximumHp = Math.Max(
            1,
            root.Enemies.Sum(enemy => Math.Max(0, enemy.MaxHp)));
        CombatBeamSolver replaySolver = new(
            root,
            displayNames,
            battleDamage,
            searchPolicy,
            cancellationToken);

        List<BoundedReplayEvaluation> evaluations = [];
        foreach (MultiplayerReplayCandidate candidate in
                 candidates.Take(BoundedRefreshCandidateLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            if (candidate.Prefix.Count == 0
                || candidate.Prefix.Count > BoundedRefreshPrefixActionLimit
                || candidate.Prefix.Any(action =>
                    action.Kind != PlanActionKind.PlayCard
                    || action.EndsPlayerTurn
                    || !action.IsExecutable
                    || action.Turn != root.StartTurnNumber))
            {
                rejected++;
                continue;
            }

            using (RetainedPrefixReplay replay = replaySolver.ReplayRetainedPrefix(candidate.Prefix))
            {
                prefixTransitions += replay.Transitions;
                if (replay.Snapshot is not { } snapshot)
                {
                    rejected++;
                    continue;
                }
                if (replay.Actions.Count < candidate.Prefix.Count) truncated++;
                bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
                    candidate.Prefix.Count,
                    snapshot.AllEnemiesDead,
                    snapshot.PlayerDead,
                    snapshot.ProjectedPlayerHp);
                double enemyDurabilityRatio =
                    MultiplayerCombatObjectivePolicy.ComputeEnemyDurabilityRatio(
                        snapshot.EnemyDurabilityByCombatId,
                        initialEnemyMaximumHp);
                MultiplayerCombatObjectiveRank objective =
                    MultiplayerCombatObjectiveMath.BuildRank(
                        searchPolicy.MultiplayerCombatObjectiveStrategy,
                        completeVictory,
                        snapshot.AllPlayersAlive,
                        snapshot.TeamLossRatio,
                        snapshot.WorstPlayerLossRatio,
                        enemyDurabilityRatio,
                        searchPolicy.MultiplayerEnemyDurabilityRatio,
                        completeVictory ? snapshot.CombatEndedTurn : null,
                        root.StartTurnNumber);
                evaluations.Add(new BoundedReplayEvaluation(
                    candidate with { Prefix = replay.Actions },
                    objective,
                    snapshot.Score));
            }
        }
        prefixMilliseconds = clock.Elapsed.TotalMilliseconds;

        int currentIndex = evaluations.FindIndex(
            evaluation => evaluation.Candidate.OriginalRank == 0);
        if (currentIndex < 0)
            return Finish(new(MultiplayerPlanRefreshDecision.FullRestart, null,
                evaluations.Count, false, "current_prefix_unreplayable"));

        BoundedReplayEvaluation selected = evaluations[currentIndex];
        foreach (BoundedReplayEvaluation candidate in evaluations)
        {
            int comparison = searchPolicy.UseMultiplayerTeamObjective
                ? MultiplayerCombatObjectiveMath.Compare(candidate.Objective, selected.Objective)
                : selected.Score.CompareTo(candidate.Score);
            if (comparison < 0
                || comparison == 0
                && candidate.Candidate.OriginalRank < selected.Candidate.OriginalRank)
            {
                selected = candidate;
            }
        }

        SolverSearchProfile boundedProfile = searchPolicy.Profile with
        {
            BeamWidth = Math.Min(
                searchPolicy.Profile.BeamWidth,
                BoundedRefreshBeamWidth),
            MaxExpandedNodes = Math.Min(
                searchPolicy.Profile.MaxExpandedNodes,
                BoundedRefreshExpandedNodeLimit),
            SoftTimeBudgetMilliseconds = Math.Min(
                searchPolicy.Profile.SoftTimeBudgetMilliseconds,
                BoundedRefreshTimeLimitMilliseconds),
        };
        CombatBeamSolver materializeSolver = new(
            root,
            displayNames,
            battleDamage,
            searchPolicy,
            cancellationToken,
            searchProfile: boundedProfile,
            fixedPrefixActions: selected.Candidate.Prefix,
            reserveScenarioReevaluationBudget: false);
        SolverResult refreshed = materializeSolver.Solve();
        materializationMilliseconds = clock.Elapsed.TotalMilliseconds - prefixMilliseconds;

        bool prefixPreserved = refreshed.BestNode.Actions.Count >= selected.Candidate.Prefix.Count;
        for (int index = 0; prefixPreserved && index < selected.Candidate.Prefix.Count; index++)
        {
            prefixPreserved = HasSameExecutionIdentity(
                refreshed.BestNode.Actions[index],
                selected.Candidate.Prefix[index]);
        }
        if (!prefixPreserved)
            return Finish(new(MultiplayerPlanRefreshDecision.FullRestart, refreshed,
                evaluations.Count, false, "materialized_prefix_mismatch"));

        MultiplayerPlanRefreshDecision decision =
            selected.Candidate.OriginalRank == 0
                ? MultiplayerPlanRefreshDecision.Continue
                : MultiplayerPlanRefreshDecision.Reselect;
        bool firstActionChanged = decision == MultiplayerPlanRefreshDecision.Reselect;

        return Finish(new(decision, refreshed, evaluations.Count, firstActionChanged,
            decision == MultiplayerPlanRefreshDecision.Continue
                ? "current_prefix_rematerialized"
                : "retained_alternative_rematerialized"));

        MultiplayerPlanRefreshResult Finish(MultiplayerPlanRefreshResult result)
            => result with { Metrics = new(attempted, rejected, truncated, prefixTransitions,
                prefixMilliseconds, materializationMilliseconds, clock.Elapsed.TotalMilliseconds) };
    }

    private static bool HasSameExecutionIdentity(
        PlanAction actual,
        PlanAction expected)
        => actual.Kind == expected.Kind
            && actual.Turn == expected.Turn
            && string.Equals(actual.CardId, expected.CardId, StringComparison.Ordinal)
            && actual.CardOccurrence == expected.CardOccurrence
            && actual.TargetIndex == expected.TargetIndex
            && actual.TargetCombatId == expected.TargetCombatId
            && string.Equals(actual.CardStateKey, expected.CardStateKey, StringComparison.Ordinal)
            && actual.CardStateOccurrence == expected.CardStateOccurrence
            && actual.CardUpgradeLevel == expected.CardUpgradeLevel
            && string.Equals(
                actual.CardEnchantmentId,
                expected.CardEnchantmentId,
                StringComparison.Ordinal)
            && Equals(actual.Choice, expected.Choice)
            && Equals(actual.NestedChoices, expected.NestedChoices);

}
