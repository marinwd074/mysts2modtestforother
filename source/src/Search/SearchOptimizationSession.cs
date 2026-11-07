using System.Diagnostics;

namespace CombatSolver;

internal sealed record SearchOptimizationMetrics(int Witnesses, long SharedBranchesPruned,
    long CertifiedBranchesPruned, long SmartBranchesPruned, double? FirstPotionFreeWitnessMs);

// One request, one exact root. Only finalized, policy-eligible victories become
// cross-member witnesses; speculative frontiers never become shared incumbents.
internal sealed class SearchOptimizationSession(CombatRootSnapshot root, bool measureFirstWitness = false)
{
    private readonly object _gate = new();
    private readonly long? _startedAt = measureFirstWitness ? Stopwatch.GetTimestamp() : null;
    private double? _firstPotionFreeWitnessMs;
    private PrimarySearchIncumbent? _potionFreeBound;
    private SolverResult? _potionFreeWitness;
    internal long SharedBranchesPruned;
    internal long CertifiedBranchesPruned;
    internal long SmartBranchesPruned;
    internal int Witnesses;
    internal SearchOptimizationMetrics Capture()
    {
        lock (_gate)
            return new(Witnesses, Interlocked.Read(ref SharedBranchesPruned),
                Interlocked.Read(ref CertifiedBranchesPruned), Interlocked.Read(ref SmartBranchesPruned),
                _firstPotionFreeWitnessMs);
    }

    internal bool Matches(CombatRootSnapshot candidate) => ReferenceEquals(root, candidate);

    internal PrimarySearchIncumbent? PotionFreeBound
    {
        get { lock (_gate) return _potionFreeBound; }
    }

    internal SolverResult? PotionFreeWitness
    {
        get { lock (_gate) return _potionFreeWitness; }
    }

    internal void Observe(SolverResult result, SearchPolicySnapshot policy, int deficit)
    {
        if (result.ResultScope != SolverResultScope.SearchCompletion || result.PotionCount != 0
            || result.Snapshot.HasRisk || result.Snapshot.ProjectedDeathSaveUseCount != 0
            || !result.Snapshot.AllEnemiesDead || result.Snapshot.PlayerDead || result.Snapshot.PlayerHp <= 0
            || result.CombatEndedTurn is not { } turn || policy.EffectiveHasGrowthTargets
            || policy.RelicTargets.Count != 0 || policy.UseMultiplayerTeamObjective
            || policy.TheftPolicy == SolverTheftPolicy.PreserveResources || policy.PotionStrategy.HasForcedDirectives
            || policy.PotionPolicy is not (SolverPotionPolicy.Disabled or SolverPotionPolicy.Smart))
            return;
        lock (_gate)
        {
            if (_potionFreeBound is { } current && (deficit > current.StrategicHpDeficit
                || deficit == current.StrategicHpDeficit && turn >= current.CombatEndedTurn)) return;
            _potionFreeBound = new(deficit, turn);
            _potionFreeWitness = result;
            Witnesses++;
            if (_startedAt is { } startedAt)
                _firstPotionFreeWitnessMs ??= Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }
    }
}
