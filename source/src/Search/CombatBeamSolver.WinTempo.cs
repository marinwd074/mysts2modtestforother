namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // Freeze the raw-loss winner as the reference. In particular, 0 -> 2 -> 4 HP
    // must never become a sequence of acceptable pairwise promotions.
    private const int WinTempoAdditionalHpLimit = 2;

    private bool CanExperimentWithWinTempo()
        => policy.UseBoundedWinTempo
            && _routePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore
            && root.PlayerCount > 1 && root.AllowsLocalPlayerOnlySearch
            && !policy.CurrentTurnOnly && !_includeTurnSetup && !_continuationSeedProbe
            && !policy.UseMultiplayerTeamObjective && !policy.UseMultiplayerTeammateForecast
            && !policy.UseMultiplayerScenarioReevaluation
            && !policy.UseBeamWidthPortfolio && !policy.UseNoveltyPortfolio && policy.NoveltySearch == null
            && _potionPolicy == SolverPotionPolicy.Disabled && root.SearchablePotionCount == 0
            && _minimumPotionUses == 0 && _potionStrategy.Directives.Count == 0
            && !policy.EffectiveHasGrowthTargets && policy.RelicTargets.Count == 0
            && _theftPolicy != SolverTheftPolicy.PreserveResources;

    private static bool IsEarlierWinWithinCost(SimulationSnapshot candidate, SimulationSnapshot baseline,
        int startTurn, int earlierThan)
    {
        long additionalLoss = (long)candidate.CumulativePlayerHpLost - baseline.CumulativePlayerHpLost;
        return additionalLoss is >= 0 and <= WinTempoAdditionalHpLimit
            && candidate.CombatEndedTurn is int turn && turn < earlierThan
            && candidate.EvaluationWindow?.ForTurn(startTurn) is { CompleteVictory: true, CoversTarget: true } candidateWindow
            && RouteEvaluationWindow.CanCompare(candidateWindow, baseline.EvaluationWindow?.ForTurn(startTurn))
            && !candidate.HasRisk && !candidate.PlayerDead && candidate.PlayerHp > 0
            && candidate.ProjectedDeathSaveUseCount == 0 && candidate.PotionUseCount == 0
            && candidate.PlayerMaxHp == baseline.PlayerMaxHp
            && (long)baseline.PlayerHp - candidate.PlayerHp <= WinTempoAdditionalHpLimit
            && candidate.GrowthRewards == baseline.GrowthRewards
            && candidate.RelicCounters == baseline.RelicCounters
            && candidate.RealizedLongTermResourceValue == baseline.RealizedLongTermResourceValue
            && candidate.LongTermResourceValue == baseline.LongTermResourceValue
            && candidate.OutstandingStolenResource == baseline.OutstandingStolenResource;
    }
}
