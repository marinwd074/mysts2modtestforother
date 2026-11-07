using System.Diagnostics;

namespace CombatSolver;

internal sealed record SearchOptimizationMetrics(int Witnesses, long SharedBranchesPruned,
    long CertifiedBranchesPruned, long SmartBranchesPruned, double? FirstPotionFreeWitnessMs,
    HealingBoundObservations? HealingBounds);

internal sealed record HealingBoundObservations(long Certified, long UncertifiedRoot,
    long RiskOrReleased, long UnclassifiedBranch);

// One request, one exact root. Only finalized, policy-eligible victories become
// cross-member witnesses; speculative frontiers never become shared incumbents.
internal sealed class SearchOptimizationSession(CombatRootSnapshot root, SearchPolicySnapshot policy,
    bool measureBounds = false)
{
    private readonly record struct Objective(
        SearchRoutePolicy RoutePolicy, int? TurnLimit, bool CurrentTurnOnly, bool IncludeTurnSetup,
        SolverTheftPolicy? TheftPolicy, BossHpRelief BossHpRelief,
        bool TeammateForecast, bool ScenarioReevaluation, bool ReviewedMultiplayerCards)
    {
        internal static Objective Capture(CombatRootSnapshot root, SearchPolicySnapshot policy)
            => new(policy.RoutePolicy, SearchEvaluationPolicy.TurnLayerLimit(policy.RoutePolicy,
                policy.LocalCoreTurnLayers), policy.CurrentTurnOnly, policy.IncludeTurnSetup,
                policy.TheftPolicy, ActEndingBossPolicy.ResolveStrategicHpRelief(root.BossHpRelief,
                    policy.ActTransitionBossHpStrategy, policy.FinalBossHpStrategy),
                policy.UseMultiplayerTeammateForecast, policy.UseMultiplayerScenarioReevaluation,
                policy.UseReviewedLocalMultiplayerCards);
    }

    private readonly object _gate = new();
    private readonly Objective _objective = Objective.Capture(root, policy);
    private readonly long? _startedAt = measureBounds ? Stopwatch.GetTimestamp() : null;
    private readonly long[]? _healingBounds = measureBounds ? new long[Enum.GetValues<HealingBoundStatus>().Length] : null;
    private double? _firstPotionFreeWitnessMs;
    private bool _auditedSoloVictoryPublished;
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
                _firstPotionFreeWitnessMs, _healingBounds is { } counts ? new(
                    Interlocked.Read(ref counts[(int)HealingBoundStatus.Certified]),
                    Interlocked.Read(ref counts[(int)HealingBoundStatus.UncertifiedRoot]),
                    Interlocked.Read(ref counts[(int)HealingBoundStatus.RiskOrReleased]),
                    Interlocked.Read(ref counts[(int)HealingBoundStatus.UnclassifiedBranch])) : null);
    }

    internal bool Matches(CombatRootSnapshot candidate, SearchPolicySnapshot candidatePolicy)
        => ReferenceEquals(root, candidate) && _objective == Objective.Capture(candidate, candidatePolicy);

    // Called at a committed work boundary on the coordinator thread.
    internal bool CanPublishAuditedSoloVictory => !_auditedSoloVictoryPublished;
    internal void MarkAuditedSoloVictoryPublished() => _auditedSoloVictoryPublished = true;

    internal void RecordHealingBound(HealingBoundStatus status)
    {
        if (_healingBounds != null)
            Interlocked.Increment(ref _healingBounds[(int)status]);
    }

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
