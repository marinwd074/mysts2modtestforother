namespace CombatSolver;

// Value-only suggestions. No prior simulator, score, or execution permission crosses requests.
internal sealed record MultiplayerPlanRefreshRequest(
    string SourceRouteIdentity,
    IReadOnlyList<MultiplayerReplayCandidate> Candidates,
    IReadOnlyList<PlanAction> Route,
    ContinuationRouteReplayBaseline? Baseline)
{
    internal int? EvaluationTurn { get; init; }
    internal bool SourceWasCompleteVictory { get; init; }

    internal static MultiplayerPlanRefreshRequest Capture(SolverResult source)
        => new(source.RouteIdentity,
            Array.AsReadOnly(source.MultiplayerReplayCandidates.Take(3)
                .Where(candidate => candidate.Prefix.Concat(candidate.Route)
                    .All(action => action.ShadowForecast == null && !action.IsForecastOnlyObservation))
                .Select(candidate => candidate with
                {
                    Prefix = CopyActions(candidate.Prefix), Route = CopyActions(candidate.Route),
                    Baseline = candidate.Baseline is { } baseline ? baseline with { ExplicitRouteAdoption = false } : null,
                }).ToArray()),
            source.BestNode.Actions.Any(action => action.ShadowForecast != null)
                ? [] : CopyActions(source.BestNode.Actions),
            CaptureBaseline(source))
        {
            EvaluationTurn = source.Snapshot.Turn,
            SourceWasCompleteVictory = source.Snapshot.AllEnemiesDead && !source.Snapshot.PlayerDead,
        };

    internal static ContinuationRouteReplayBaseline? CaptureBaseline(SolverResult source)
        => source.PredictedFinalState is { } finalState
            ? new(finalState, source.ProjectedBattleHpLost, source.Snapshot.ProjectedDeathSaveUseCount,
                source.Snapshot.GrowthRewards, source.Snapshot.RelicCounters) : null;

    internal static MultiplayerReplayCandidate CaptureCandidate(int rank, SolverResult source)
        => new(rank, CopyActions(source.BestNode.Actions.TakeWhile(action =>
            action.Turn == source.StartTurnNumber && action.Kind == PlanActionKind.PlayCard
            && !action.EndsPlayerTurn && action.IsExecutable).Take(2).ToArray()))
        {
            Route = CopyActions(source.BestNode.Actions), Baseline = CaptureBaseline(source),
        };

    internal static IReadOnlyList<PlanAction> CopyActions(IReadOnlyList<PlanAction> actions)
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
    int RemainingMilliseconds, int RemainingExpandedNodes)
{
    public int? SelectedOriginalRank { get; init; }
    public IReadOnlyList<MultiplayerFullRouteReplayReport> FullRoutes { get; init; } = [];
}

internal sealed record MultiplayerFullRouteReplayReport(int OriginalRank, string Outcome, int AcceptedActions);

internal sealed class SearchRecoveryBudgetExhaustedException()
    : Exception("本次计算预算已用完，尚无可采用的路线。");
