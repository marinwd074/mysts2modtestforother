using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;

using Registry = MethodMirrorRegistry<AbstractModel, AfterBlockBrokenMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterBlockBroken.
internal static class AfterBlockBrokenMirrors
{
    private static readonly MirrorMethodSpec AfterBlockBroken = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterBlockBroken),
        Sts2HookCompatibility.AfterBlockBrokenParameterTypes);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterBlockBrokenMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterBlockBroken);

        registry.RegisterIgnored<BurrowedPower>();
#if !STS2_01071
        registry.Register<HandDrill>(HandleHandDrill);
#endif

        return registry;
    }

#if !STS2_01071
    private static void HandleHandDrill(HandDrill relic, AfterBlockBrokenMirrorContext context)
    {
        if ((context.Breaker == relic.Owner.Creature || context.Breaker?.PetOwner == relic.Owner) &&
            !context.Target.IsPlayer)
        {
            if (context.CombatState is not ICombatPredictionEffectSink effects)
                throw new InvalidOperationException("破甲钻效果缺少可写的预测状态。");
            effects.ApplyPowerFromSource(
                typeof(VulnerablePower),
                context.Target,
                relic.DynamicVars.Vulnerable.IntValue,
                relic.Owner.Creature,
                cardSource: null);
        }
    }
#endif
}

internal sealed class AfterBlockBrokenMirrorContext : CombatMirrorContext
{
    public required Creature Target { get; init; }

    public required Creature? Breaker { get; init; }
}
