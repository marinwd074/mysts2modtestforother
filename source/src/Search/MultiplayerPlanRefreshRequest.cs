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
    internal bool IsCrossTurn { get; init; }

    internal static MultiplayerPlanRefreshRequest Capture(SolverResult source)
        => CaptureAtTurn(source, source.StartTurnNumber, isCrossTurn: false);

    private static MultiplayerPlanRefreshRequest CaptureAtTurn(SolverResult source, int currentTurn, bool isCrossTurn)
        => new(source.RouteIdentity,
            CaptureRemainingCandidates(source.MultiplayerReplayCandidates, currentTurn),
            CaptureRemainingRoute(source.BestNode.Actions, currentTurn),
            CaptureBaseline(source))
        {
            EvaluationTurn = source.Snapshot.Turn,
            SourceWasCompleteVictory = source.Snapshot.AllEnemiesDead && !source.Snapshot.PlayerDead,
            IsCrossTurn = isCrossTurn,
        };

    // Called only after Runtime's existing cross-turn lineage/RNG admission. This
    // slices suggestions, not an old simulator or authorization. Battle HP is an
    // absolute ceiling; fresh replay accounts only remaining actions from the new root.
    internal static MultiplayerPlanRefreshRequest CaptureContinuation(SolverResult source, int currentTurn)
    {
        if (currentTurn < source.StartTurnNumber)
            throw new ArgumentOutOfRangeException(nameof(currentTurn), "Continuation cannot precede the source turn.");
        // Repeated invalidation may revisit the source turn; retain the existing
        // independent-continuation allowance without another copy of every route.
        return CaptureAtTurn(source, currentTurn, isCrossTurn: true);
    }

    internal static IReadOnlyList<PlanAction> CaptureRemainingRoute(IReadOnlyList<PlanAction> actions, int currentTurn)
        => actions.Any(action => action.ShadowForecast != null || action.IsForecastOnlyObservation)
            ? [] : CopyActions(actions.Where(action => action.Turn >= currentTurn).ToArray());

    internal static IReadOnlyList<MultiplayerReplayCandidate> CaptureRemainingCandidates(
        IReadOnlyList<MultiplayerReplayCandidate> candidates, int currentTurn)
        => Array.AsReadOnly(candidates.Take(3)
            .Where(candidate => candidate.Prefix.Concat(candidate.Route)
                .All(action => action.ShadowForecast == null && !action.IsForecastOnlyObservation))
            .Select(candidate =>
            {
                IReadOnlyList<PlanAction> route = CaptureRemainingRoute(candidate.Route, currentTurn);
                return candidate with
                {
                    Route = route,
                    Prefix = route.Count > 0 ? CapturePrefix(route, currentTurn)
                        : CopyActions(candidate.Prefix.Where(action => action.Turn == currentTurn).ToArray()),
                    Baseline = candidate.Baseline is { } baseline ? baseline with { ExplicitRouteAdoption = false } : null,
                };
            }).ToArray());

    private static IReadOnlyList<PlanAction> CapturePrefix(IReadOnlyList<PlanAction> actions, int currentTurn)
        => CopyActions(actions.TakeWhile(action => action.Turn == currentTurn
            && action.Kind == PlanActionKind.PlayCard && !action.EndsPlayerTurn && action.IsExecutable).Take(2).ToArray());

    internal static ContinuationRouteReplayBaseline? CaptureBaseline(SolverResult source)
        => source.PredictedFinalState is { } finalState
            ? new(finalState, source.ProjectedBattleHpLost, source.Snapshot.ProjectedDeathSaveUseCount,
                source.Snapshot.GrowthRewards, source.Snapshot.RelicCounters) : null;

    internal static MultiplayerReplayCandidate CaptureCandidate(int rank, SolverResult source)
        => new(rank, CapturePrefix(source.BestNode.Actions, source.StartTurnNumber))
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
    public bool IsCrossTurn { get; init; }
    public IReadOnlyList<MultiplayerFullRouteReplayReport> FullRoutes { get; init; } = [];
}

internal sealed record MultiplayerFullRouteReplayReport(int OriginalRank, string Outcome, int AcceptedActions);

internal sealed class SearchRecoveryBudgetExhaustedException()
    : Exception("本次计算预算已用完，尚无可采用的路线。");
