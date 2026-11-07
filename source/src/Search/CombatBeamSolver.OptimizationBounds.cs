using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private bool CanUseOptimizationBounds => policy.UseUpstreamSearchOptimizations
        && !_hasGrowthTargets && !policy.UseMultiplayerTeamObjective
        && !SearchEvaluationPolicy.UsesRawLossFirst(policy.RoutePolicy)
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
        // A zero-use witness only bounds members whose future explicit use is closed.
        bool zeroUseClosed = _forceAllPotionsDisabled || _potionPolicy == SolverPotionPolicy.Disabled
            || _maximumPotionUses == 0;
        PrimarySearchIncumbent? shared = zeroUseClosed ? session?.PotionFreeBound : null;
        PrimarySearchIncumbent? local = root.HasPinnedHealingCertificate ? _primaryIncumbent : null;
        int? ceiling = policy.PotionPolicy == SolverPotionPolicy.Smart
            && root.HasPinnedHealingCertificate && _maximumPotionUses == _minimumPotionUses
            // A Brew can supply zero-cost uses. The per-use root-price lower
            // bound is not a proof for this request, even before it is consumed.
            && !root.SearchablePotions.Any(potion => potion.PotionId == "ENTROPIC_BREW")
            && root.MinimumSearchablePotionStrategicCost is { } cost
                ? SmartEligibilityCeiling(_potionFreePolicyBaseline, _minimumPotionUses, cost, _strategicBossHpRelief)
                : null;
        if (shared == null && local == null && ceiling == null)
            return retained;
        List<SearchNode>? bounded = null;
        for (int index = 0; index < retained.Count; index++)
        {
            SearchNode node = retained[index];
            bool prune = false;
            if (!node.IsTerminal && !node.Snapshot.HasRisk)
            {
                int lower = OptimisticHpLowerBound(node.Snapshot);
                // Strict comparisons preserve equal-HP growth, turn and potion opportunities.
                if (shared is { } witnessed && lower > witnessed.StrategicHpDeficit
                    && ExplicitPotionUseCount(node) == 0)
                {
                    prune = true;
                    if (session != null) Interlocked.Increment(ref session.SharedBranchesPruned);
                }
                if (!prune && local is { } incumbent && lower > incumbent.StrategicHpDeficit)
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
