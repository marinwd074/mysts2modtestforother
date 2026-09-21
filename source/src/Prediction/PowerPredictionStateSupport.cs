using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PowerPredictionStateSupport
{
    private static readonly FieldInfo PowerInternalDataField =
        typeof(PowerModel).GetField("_internalData", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(PowerModel).FullName, "_internalData");

    private static readonly FieldInfo InterceptCoveredCreaturesField =
        (typeof(InterceptPower).GetNestedType("Data", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(InterceptPower).FullName, "Data"))
        .GetField("coveredCreatures", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(InterceptPower).FullName + ".Data", "coveredCreatures");

    public static SurroundedPower.Direction SurroundedFacing(CombatPredictionSimulator simulator, SurroundedPower power)
        => simulator.StateStore.Peek(power, () => new SurroundedPredictionState(power)).Facing;

    public static int NativeOutbreakPoisonApplications(OutbreakPower power)
        => power.DisplayAmount;

    public static int OutbreakPoisonApplications(
        CombatPredictionSimulator simulator,
        OutbreakPower power)
        => simulator.StateStore
            .Peek(power, () => new CounterPredictionState(NativeOutbreakPoisonApplications(power)))
            .Value;

    public static bool RecordOutbreakPoisonApplication(
        CombatPredictionSimulator simulator,
        OutbreakPower power)
    {
        CounterPredictionState state = simulator.StateStore.Get(
            power,
            () => new CounterPredictionState(NativeOutbreakPoisonApplications(power)));
        state.Value++;
        if (state.Value < OutbreakPower.poisonThreshold)
            return false;
        state.Value %= OutbreakPower.poisonThreshold;
        return true;
    }

    public static IReadOnlyList<Creature> NativeInterceptCoveredCreatures(InterceptPower power)
    {
        object data = PowerInternalDataField.GetValue(power)
            ?? throw new InvalidOperationException("InterceptPower has no native internal data.");
        return InterceptCoveredCreaturesField.GetValue(data) as IReadOnlyList<Creature>
            ?? throw new InvalidOperationException("InterceptPower covered-creature state has an unexpected shape.");
    }

    public static IReadOnlyList<Creature> InterceptCoveredCreatures(
        CombatPredictionSimulator simulator,
        InterceptPower power)
        => simulator.StateStore
            .Peek(power, static () => new InterceptPredictionState())
            .CoveredCreatures;

    public static void ApplyInterceptCoverage(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature interceptor,
        Creature coveredCreature)
    {
        InterceptPower? intercept = combat.GetPower<InterceptPower>(interceptor);
        if (intercept is null || combat.GetAmount<InterceptPower>(interceptor) <= 0)
        {
            combat.Apply<InterceptPower>(interceptor, 1, coveredCreature);
            intercept = combat.GetPower<InterceptPower>(interceptor)
                ?? throw new InvalidOperationException("Applying CoveredPower did not create InterceptPower.");
        }

        simulator.StateStore
            .Get(intercept, static () => new InterceptPredictionState())
            .AddCoveredCreature(coveredCreature);
    }

    public static void CaptureRootState(
        CombatPredictionSimulator simulator,
        PowerModel target,
        PowerModel source)
    {
        switch (target, source)
        {
            case (DarkEmbracePower value, DarkEmbracePower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new DarkEmbracePredictionState(original));
                break;
            case (SkittishPower value, SkittishPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new SkittishPredictionState(original));
                break;
            case (PanachePower value, PanachePower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new PanachePredictionState(original));
                break;
#if !STS2_01071
            case (SoulboundPower value, SoulboundPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new SoulboundPredictionState(original));
                break;
#endif
            case (HellraiserPower value, HellraiserPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new HellraiserPredictionState(original));
                break;
#if !STS2_01071
            case (CacophonyPower value, CacophonyPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new CacophonyPredictionState(original));
                break;
#endif
            case (AutomationPower value, AutomationPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new AutomationPredictionState(original));
                break;
            case (JugglingPower value, JugglingPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new JugglingPredictionState(original));
                break;
            case (OutbreakPower value, OutbreakPower original):
                _ = simulator.StateStore.GetReadOnly(
                    value,
                    () => new CounterPredictionState(NativeOutbreakPoisonApplications(original)));
                break;
            case (InterceptPower value, InterceptPower original):
                _ = simulator.StateStore.GetReadOnly(
                    value,
                    () => new InterceptPredictionState(NativeInterceptCoveredCreatures(original)));
                break;
            case (ChainsOfBindingPower value, ChainsOfBindingPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => ChainsOfBindingPredictionState.CaptureRoot(original));
                break;
            case (SurroundedPower value, SurroundedPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new SurroundedPredictionState(original));
                break;
            case (VoidFormPower value, VoidFormPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new VoidFormPredictionState(original));
                break;
            case (FeralPower value, FeralPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new FeralPredictionState(original));
                break;
            case (HardenedShellPower value, HardenedShellPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new HardenedShellPredictionState(original));
                break;
        }
        // 上面这个 switch 按原版类型写死，第三方登记不进去。克隆会把 _internalData 重置成
        // InitInternalData()，所以靠它保存状态的第三方 Power 同样必须在这里把实机实例的值搬进
        // StateStore，否则模拟一开始读到的就是初值。
        PowerHiddenStateMirrors.CaptureRootState(simulator, target, source);
    }
}


internal sealed class InterceptPredictionState : IPredictionStateForkable
{
    private readonly List<Creature> _coveredCreatures;

    public InterceptPredictionState()
        : this([])
    {
    }

    public InterceptPredictionState(IEnumerable<Creature> coveredCreatures)
    {
        _coveredCreatures = [.. coveredCreatures];
    }

    public IReadOnlyList<Creature> CoveredCreatures => _coveredCreatures;

    public void AddCoveredCreature(Creature creature)
    {
        if (!_coveredCreatures.Contains(creature))
            _coveredCreatures.Add(creature);
    }

    public object Fork(PredictionForkContext context)
        => new InterceptPredictionState(_coveredCreatures);
}
