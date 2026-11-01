namespace CombatSolver;

internal sealed record SolverInterimResult(
    bool Won,
    int OutstandingStolenResource,
    int ProjectedBattleHpLost,
    int StrategicHpDeficit,
    int PotionStrategicCost,
    int ProjectedBattlePotionCount,
    int EnemyHp,
    double Score,
    int? CombatEndedTurn = null)
{
    public SolverTheftPolicy? TheftPolicy { get; init; }
    public bool Survives { get; init; }
    public int DeathSaveUseCount { get; init; }
    public int GrowthHpCredit { get; init; }
    public int GrowthRewardCount { get; init; }
    public bool RollingHorizonLossFirst { get; init; }
}

internal sealed record SolverFrontierTurn(
    int Turn,
    IReadOnlyList<PlanAction> Actions,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded)
{
    public IReadOnlyList<PlanCardChoice> TurnStartChoices { get; init; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<string>> KillsAfterAction { get; init; }
        = new Dictionary<int, IReadOnlyList<string>>();

    public static IReadOnlyList<SolverFrontierTurn> FromResult(SolverResult result)
        => result.BestNode.Actions
            .Select((action, index) => (Action: action, Index: index))
            .GroupBy(item => item.Action.Turn)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var indexed = group.ToArray();
                Dictionary<int, IReadOnlyList<string>> kills = [];
                for (int localIndex = 0; localIndex < indexed.Length; localIndex++)
                {
                    if (result.KillsAfterAction.TryGetValue(
                            indexed[localIndex].Index,
                            out IReadOnlyList<string>? actionKills)
                        && actionKills.Count > 0)
                    {
                        kills[localIndex] = actionKills;
                    }
                }
                return new SolverFrontierTurn(
                    group.Key,
                    indexed.Select(item => item.Action).ToArray(),
                    result.HpLostByTurn.GetValueOrDefault(group.Key),
                    result.HpRecoveredByTurn.GetValueOrDefault(group.Key),
                    result.EnemyHpLostByTurn.GetValueOrDefault(group.Key),
                    result.EnergyLeftByTurn.GetValueOrDefault(group.Key),
                    result.CombatEndedTurn == group.Key)
                {
                    TurnStartChoices = TurnStartChoicePreviewPolicy.ChoicesForTurn(
                        group.Key,
                        result.StartTurnNumber,
                        result.WasReused ? [] : result.TurnSetupChoices,
                        result.BestNode.Actions),
                    KillsAfterAction = kills,
                };
            })
            .ToArray();
}

internal sealed record SolverCurrentTurnPreview(
    int CandidateVersion,
    int Turn,
    IReadOnlyList<PlanAction> Actions,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded,
    IReadOnlyList<SolverFrontierTurn>? FrontierTurns = null)
{
    public IReadOnlyList<PlanCardChoice> TurnStartChoices { get; init; } = [];

    public static SolverCurrentTurnPreview FromResult(
        SolverResult result,
        int candidateVersion = 0)
        => new(
            candidateVersion,
            result.StartTurnNumber,
            result.BestNode.Actions
                .Where(action => action.Turn == result.StartTurnNumber)
                .ToArray(),
            result.HpLostByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.HpRecoveredByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.EnemyHpLostByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.EnergyLeftByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.CombatEndedTurn == result.StartTurnNumber,
            SolverFrontierTurn.FromResult(result))
        {
            TurnStartChoices = result.WasReused ? [] : result.TurnSetupChoices,
        };
}

internal sealed record SolverSpeculativeRoutePreview(
    int CandidateVersion,
    int StartTurnNumber,
    int ProjectedBattlePotionCount,
    int ProjectedBattleHpLost,
    bool CombatEnded,
    bool OnlyDeathRoutesFound,
    bool HasRisk,
    IReadOnlyList<SolverFrontierTurn> Turns)
{
    public static SolverSpeculativeRoutePreview FromResult(
        SolverResult result,
        int candidateVersion = 0)
        => new(
            candidateVersion,
            result.StartTurnNumber,
            result.ProjectedBattlePotionCount,
            result.ProjectedBattleHpLost,
            result.CombatEndedTurn.HasValue,
            result.OnlyDeathRoutesFound,
            result.Snapshot.HasRisk,
            SolverFrontierTurn.FromResult(result));
}

internal sealed record SolverProgress(
    int StartTurnNumber,
    int CurrentTurnNumber,
    int CompletedTurnLayers,
    int PlayDepth,
    int ExpandedNodes,
    long ReviewedWorldlines,
    int MaxNodes,
    int FrontierNodes,
    int EndedNodes,
    long ElapsedMilliseconds,
    string Phase,
    SolverInterimResult? CurrentBestResult = null,
    SolverCurrentTurnPreview? CurrentTurnPreview = null,
    SolverSpeculativeRoutePreview? SpeculativeRoutePreview = null,
    SolverRouteAdoptionSeed? RouteAdoptionSeed = null,
    int RequestBudgetMilliseconds = 0)
{
    public SolverRouteAdoptionSeed? CurrentTurnAdoptionSeed { get; init; }

    // Set only when a member has finished the full production final ordering (including
    // scenario rerank and deterministic block-potion insertion) but is still flattening
    // replay/annotation data. The coordinator records Published only if this route actually
    // wins the global displayed-result ordering.
    public CandidateOrigin? OfficialPublishedOrigin { get; init; }
    public string? OfficialPublishedEvaluationContextId { get; init; }

    public bool HasApprovedForegroundRoute
        => OfficialPublishedOrigin != null
            && !string.IsNullOrWhiteSpace(OfficialPublishedEvaluationContextId)
            && SpeculativeRoutePreview != null;

    public int ApprovedForegroundCandidateVersion
        => SpeculativeRoutePreview?.CandidateVersion ?? -1;
}
