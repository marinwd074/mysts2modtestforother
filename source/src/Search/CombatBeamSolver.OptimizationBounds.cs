using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private bool CanUseOptimizationBounds => policy.UseUpstreamSearchOptimizations
        && !_hasGrowthTargets && !policy.UseMultiplayerTeamObjective
        && !MultiplayerLocalCrossTurnContracts.UsesRollingHorizonLossFirstQuality(policy.RoutePolicy)
        && _theftPolicy != SolverTheftPolicy.PreserveResources && !_potionStrategy.HasForcedDirectives;

    private int OptimisticHpLowerBound(SimulationSnapshot snapshot)
    {
        int healing = PinnedHealingBound.RemainingHealing(root, snapshot);
        int headroom = Math.Max(0, snapshot.PlayerMaxHp - snapshot.PlayerHp);
        return ActEndingBossPolicy.StrategicHpDeficit(snapshot.CumulativePlayerHpLost, 0,
            snapshot.RecoveredPlayerHp + Math.Min(headroom, healing), _strategicBossHpRelief,
            snapshot.DeathSaveHpRestored);
    }

    internal static int? SmartEligibilityCeiling(PotionFreePolicyBaseline? baseline,
        int exactUses, int minimumPotionCost, BossHpRelief relief)
        => baseline is { Won: true, DeathSaveUseCount: 0 } value && exactUses > 0 && minimumPotionCost >= 0
            ? (int)Math.Clamp((long)value.HpDeficit - PotionUsePolicy.SmartRequiredHpSaved(
                (int)Math.Min(int.MaxValue, (long)exactUses * minimumPotionCost), relief), int.MinValue, int.MaxValue)
            : null;

    private List<SearchNode> ApplyUpstreamOptimizationBounds(List<SearchNode> retained)
    {
        if (!CanUseOptimizationBounds) return retained;
        SearchOptimizationSession? session = policy.OptimizationSession is { } candidate && candidate.Matches(root)
            ? candidate : null;
        PrimarySearchIncumbent? shared = session?.PotionFreeBound;
        // A zero-use witness only bounds members whose future explicit use is closed.
        bool zeroUseClosed = _forceAllPotionsDisabled || _potionPolicy == SolverPotionPolicy.Disabled
            || _maximumPotionUses == 0;
        int? ceiling = policy.PotionPolicy == SolverPotionPolicy.Smart
            && _maximumPotionUses == _minimumPotionUses
            && root.HasPinnedHealingCertificate && root.MinimumSearchablePotionStrategicCost is { } cost
                ? SmartEligibilityCeiling(_potionFreePolicyBaseline, _minimumPotionUses, cost, _strategicBossHpRelief)
                : null;
        List<SearchNode>? bounded = null;
        for (int index = 0; index < retained.Count; index++)
        {
            SearchNode node = retained[index];
            bool prune = false;
            if (!node.IsTerminal && !node.Snapshot.HasRisk)
            {
                int lower = OptimisticHpLowerBound(node.Snapshot);
                // Strict comparisons preserve equal-HP growth, turn and potion opportunities.
                if (zeroUseClosed && ExplicitPotionUseCount(node) == 0
                    && shared is { } witnessed && lower > witnessed.StrategicHpDeficit)
                {
                    prune = true;
                    if (session != null) Interlocked.Increment(ref session.SharedBranchesPruned);
                }
                if (!prune && _primaryIncumbent is { } local && root.HasPinnedHealingCertificate
                    && lower > local.StrategicHpDeficit)
                {
                    prune = true;
                    if (session != null) Interlocked.Increment(ref session.CertifiedBranchesPruned);
                }
                if (!prune && ceiling is { } smart && lower > smart)
                {
                    prune = true;
                    if (session != null) Interlocked.Increment(ref session.SmartBranchesPruned);
                }
            }
            if (prune)
            {
                bounded ??= retained.GetRange(0, index);
                _run.PrimaryIncumbentBranchesPruned++;
            }
            else bounded?.Add(node);
        }
        return bounded ?? retained;
    }
}
