namespace CombatSolver;

internal enum RelicCounterId
{
    HappyFlower, FakeHappyFlower, Pendulum, PollinousCore, PenNib,
    Nunchaku, TuningFork, JossPaper, IronClub, GalacticDust, MeatOnTheBone,
}

internal sealed record RelicCounterRule(RelicCounterId Id, bool Enabled, int Minimum, int Maximum, int HpAllowance, int Priority = 1);
internal readonly record struct RelicCounterTarget(RelicCounterId Id, int Minimum, int Maximum, int HpAllowance, int Period, int Priority = 1);
internal readonly record struct RelicCounterEvaluation(ulong SatisfiedMask, ulong TargetMask, int HpCredit, int Distance)
{
    internal const int PackedValueBits = 4;
    internal const int PackedValueMask = (1 << PackedValueBits) - 1;
    internal const int PackedSlotCapacity = sizeof(ulong) * 8 / PackedValueBits;
    internal const int MaxPackedCounterCount = PackedValueMask;
    internal const int MaxPackedPeriod = 1 << PackedValueBits;

    // Counter values and policy bounds use four-bit slots. SatisfiedPriority also uses
    // four-bit lanes, so cap the catalog at 15 entries to prevent carry between priorities.
    public ulong CounterValues { get; init; }
    public int SatisfiedPriority { get; init; }
    // Actual healing reduces net HP cost, while goal allowances only rank policy rewards.
    public int HealingHpCredit { get; init; }
    public ulong TargetMinimums { get; init; }
    public ulong TargetMaximums { get; init; }
    public int Value(RelicCounterId id)
        => (int)((CounterValues >> ((int)id * PackedValueBits)) & PackedValueMask);
    public int Minimum(RelicCounterId id)
        => (int)((TargetMinimums >> ((int)id * PackedValueBits)) & PackedValueMask);
    public int Maximum(RelicCounterId id)
        => (int)((TargetMaximums >> ((int)id * PackedValueBits)) & PackedValueMask);
    public bool Satisfied => SatisfiedMask == TargetMask;
    public int SatisfiedCount => System.Numerics.BitOperations.PopCount(SatisfiedMask);
}

internal static class RelicCounterPolicy
{
    public static RelicCounterRule[] ValidateAndCopy(IEnumerable<RelicCounterRule> rules)
    {
        var result = rules.ToArray();
        HashSet<RelicCounterId> ids = [];
        foreach (var rule in result)
        {
            if (!Enum.IsDefined(rule.Id) || !ids.Add(rule.Id) || rule.Minimum < 0
                || rule.Maximum < rule.Minimum || rule.Maximum > 1000 || rule.HpAllowance is < 0 or > 1000 || rule.Priority is < 1 or > 3)
                throw new ArgumentException("Invalid relic counter policy.", nameof(rules));
        }
        return result;
    }

    public static RelicCounterEvaluation Add(RelicCounterEvaluation evaluation, RelicCounterTarget target, int value)
    {
        ulong bit = 1UL << (int)target.Id;
        bool satisfied = value >= target.Minimum && value <= target.Maximum;
        bool rewardGoal = satisfied && target.Id != RelicCounterId.MeatOnTheBone;
        int distance = satisfied ? 0 : value < target.Minimum ? target.Minimum - value : target.Period - value + target.Minimum;
        return new(evaluation.SatisfiedMask | (satisfied ? bit : 0), evaluation.TargetMask | bit,
            evaluation.HpCredit + (rewardGoal ? target.HpAllowance : 0), evaluation.Distance + distance)
        {
            SatisfiedPriority = evaluation.SatisfiedPriority
                + (rewardGoal ? 1 << ((target.Priority - 1) * RelicCounterEvaluation.PackedValueBits) : 0),
            CounterValues = (evaluation.CounterValues
                    & ~((ulong)RelicCounterEvaluation.PackedValueMask << ((int)target.Id * RelicCounterEvaluation.PackedValueBits)))
                | ((ulong)value << ((int)target.Id * RelicCounterEvaluation.PackedValueBits)),
            TargetMinimums = (evaluation.TargetMinimums
                    & ~((ulong)RelicCounterEvaluation.PackedValueMask << ((int)target.Id * RelicCounterEvaluation.PackedValueBits)))
                | ((ulong)target.Minimum << ((int)target.Id * RelicCounterEvaluation.PackedValueBits)),
            TargetMaximums = (evaluation.TargetMaximums
                    & ~((ulong)RelicCounterEvaluation.PackedValueMask << ((int)target.Id * RelicCounterEvaluation.PackedValueBits)))
                | ((ulong)target.Maximum << ((int)target.Id * RelicCounterEvaluation.PackedValueBits)),
        };
    }
}
