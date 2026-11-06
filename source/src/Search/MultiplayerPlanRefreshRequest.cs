namespace CombatSolver;

// Value-only suggestions. No prior simulator, score, or execution permission crosses requests.
internal sealed record MultiplayerPlanRefreshRequest(
    string SourceRouteIdentity,
    IReadOnlyList<MultiplayerReplayCandidate> Candidates,
    IReadOnlyList<PlanAction> Route,
    ContinuationRouteReplayBaseline? Baseline)
{
    internal static MultiplayerPlanRefreshRequest Capture(SolverResult source)
        => new(source.RouteIdentity,
            Array.AsReadOnly(source.MultiplayerReplayCandidates.Take(3)
                .Where(candidate => candidate.Prefix.All(action => action.ShadowForecast == null))
                .Select(candidate => candidate with { Prefix = CopyActions(candidate.Prefix) }).ToArray()),
            source.BestNode.Actions.Any(action => action.ShadowForecast != null)
                ? [] : CopyActions(source.BestNode.Actions),
            source.PredictedFinalState is { } finalState
                ? new(finalState, source.ProjectedBattleHpLost, source.Snapshot.ProjectedDeathSaveUseCount,
                    source.Snapshot.GrowthRewards, source.Snapshot.RelicCounters) : null);

    private static IReadOnlyList<PlanAction> CopyActions(IReadOnlyList<PlanAction> actions)
        => Array.AsReadOnly(actions.Select(action => action with
        {
            Choice = action.Choice is { } choice ? CopyChoice(choice) : null,
            NestedChoices = action.NestedChoices == null ? null
                : Array.AsReadOnly(action.NestedChoices.Select(CopyChoice).ToArray()),
            TurnStartChoices = action.TurnStartChoices == null ? null
                : Array.AsReadOnly(action.TurnStartChoices.Select(CopyChoice).ToArray()),
            RelicEffects = null,
            AutoPlayedCards = null,
        }).ToArray());

    private static PlanCardChoice CopyChoice(PlanCardChoice choice)
        => choice with { Cards = Array.AsReadOnly(choice.Cards.ToArray()) };
}

internal sealed record MultiplayerPlanRefreshReport(
    string SourceRouteIdentity, MultiplayerPlanRefreshDecision Decision, string Reason,
    bool WholeRouteReplayed, MultiplayerPlanRefreshMetrics? PrefixMetrics,
    long RecoveryMilliseconds, long RecoveryExpandedNodes,
    int RemainingMilliseconds, int RemainingExpandedNodes);

internal sealed class SearchRecoveryBudgetExhaustedException()
    : Exception("本次计算预算已用完，尚无可采用的路线。");
