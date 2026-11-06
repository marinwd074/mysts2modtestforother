namespace CombatSolver;

internal enum SolverPotionDirective
{
    Smart,
    Force,
    Disabled,
}

internal enum PotionStrategyPreset
{
    AllSmart,
    AllProtected,
    AllForced,
    OnlyForced,
}

internal readonly record struct PotionSlotDirective(
    int Slot,
    string PotionId,
    SolverPotionDirective Directive);

internal readonly record struct ForcedPotionUseEvaluation(
    bool AllForcedUsesSatisfied,
    int ForcedUseCount,
    int ForcedStrategicHpCost,
    int ForcedAmbergrisCount);

internal sealed partial class PotionStrategySnapshot
{
    private readonly Dictionary<(int Slot, string PotionId), SolverPotionDirective> _directives;
    private readonly bool _onlyForcedUses;

    public PotionStrategySnapshot(
        SolverPotionPolicy defaultPolicy,
        IEnumerable<PotionSlotDirective> directives)
        : this(defaultPolicy, directives, onlyForcedUses: false)
    {
    }

    private PotionStrategySnapshot(
        SolverPotionPolicy defaultPolicy,
        IEnumerable<PotionSlotDirective> directives,
        bool onlyForcedUses)
    {
        DefaultPolicy = defaultPolicy;
        _onlyForcedUses = onlyForcedUses;
        _directives = directives.ToDictionary(
            directive => (directive.Slot, directive.PotionId),
            directive => directive.Directive);
        Directives = _directives
            .Select(item => new PotionSlotDirective(item.Key.Slot, item.Key.PotionId, item.Value))
            .OrderBy(item => item.Slot)
            .ToArray();
    }

    public SolverPotionPolicy DefaultPolicy { get; }
    public IReadOnlyList<PotionSlotDirective> Directives { get; }
    public bool HasForcedDirectives
        => Directives.Any(directive => directive.Directive == SolverPotionDirective.Force);

    public int ForcedDirectiveCount
        => Directives.Count(directive => directive.Directive == SolverPotionDirective.Force);

    public PotionStrategySnapshot ForForcedBaseline()
        => new(DefaultPolicy, Directives, onlyForcedUses: true);

    public SolverPotionDirective Resolve(int slot, string potionId)
        => _directives.GetValueOrDefault(
            (slot, potionId),
            DefaultPolicy == SolverPotionPolicy.Disabled
                ? SolverPotionDirective.Disabled
                : SolverPotionDirective.Smart);

    public bool AllowsExplicitUse(
        int slot,
        string potionId,
        SolverPotionPolicy effectivePolicy,
        bool forceAllDisabled,
        PotionOrigin origin = PotionOrigin.RootInventory)
    {
        if (forceAllDisabled)
            return false;
        // Slot directives describe the bottles present at root capture. A refill
        // must not inherit a spent bottle's Force/Disabled identity.
        if (origin != PotionOrigin.RootInventory)
            return !_onlyForcedUses && effectivePolicy != SolverPotionPolicy.Disabled;
        if (_onlyForcedUses)
        {
            return _directives.TryGetValue((slot, potionId), out SolverPotionDirective forced)
                && forced == SolverPotionDirective.Force;
        }
        return _directives.TryGetValue((slot, potionId), out SolverPotionDirective directive)
            ? directive != SolverPotionDirective.Disabled
            : effectivePolicy != SolverPotionPolicy.Disabled;
    }

    public ForcedPotionUseEvaluation EvaluateForcedUses(
        IEnumerable<PredictedPotionUse> uses)
    {
        PotionSlotDirective[] forced = Directives
            .Where(directive => directive.Directive == SolverPotionDirective.Force)
            .ToArray();
        int count = 0;
        int strategicCost = 0;
        int ambergrisCount = 0;
        foreach (PotionSlotDirective directive in forced)
        {
            PredictedPotionUse? used = null;
            foreach (PredictedPotionUse candidate in uses)
            {
                if (!candidate.Automatic && candidate.Origin == PotionOrigin.RootInventory
                    && candidate.Slot == directive.Slot && candidate.PotionId == directive.PotionId)
                { used = candidate; break; }
            }
            if (used is not { } matched)
                continue;
            count++;
            strategicCost = checked(strategicCost + matched.StrategicHpCost);
            if (matched.IsPaidAmbergris)
                ambergrisCount++;
        }
        return new ForcedPotionUseEvaluation(
            count == forced.Length,
            count,
            strategicCost,
            ambergrisCount);
    }

    public string DescribeForcedUses()
        => string.Join(", ", Directives
            .Where(directive => directive.Directive == SolverPotionDirective.Force)
            .Select(directive => $"{directive.PotionId}@{directive.Slot}"));
}
