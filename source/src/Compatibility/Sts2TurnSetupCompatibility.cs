using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;

namespace CombatSolver;

// Version-specific reflection and invocation arguments stay at the runtime boundary.
// The turn-setup coordinator only sees the stable operation shape.
internal static class Sts2TurnSetupCompatibility
{
#if !STS2_01071
    private static readonly Type CombatTurnStateType = typeof(CombatManager).Assembly.GetType(
        "MegaCrit.Sts2.Core.Combat.CombatTurnState",
        throwOnError: true)!;
#endif

#if STS2_01071
    internal static readonly MethodInfo SetupPlayerTurnMethod = typeof(CombatManager).GetMethod(
        "SetupPlayerTurn",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [typeof(Player), typeof(HookPlayerChoiceContext)],
        modifiers: null)
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "SetupPlayerTurn");

    internal static readonly MethodInfo RunAutoPrePlayPhaseMethod = typeof(CombatManager).GetMethod(
        "RunAutoPrePlayPhase",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [typeof(HookPlayerChoiceContext), typeof(Task), typeof(Player)],
        modifiers: null)
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "RunAutoPrePlayPhase");

    internal static object?[] BuildSetupPlayerTurnArguments(
        object? turnState,
        Player player,
        HookPlayerChoiceContext choiceContext)
        => [player, choiceContext];

    internal static object?[] BuildRunAutoPrePlayPhaseArguments(
        object? turnState,
        HookPlayerChoiceContext choiceContext,
        Task setupTask,
        Player player)
        => [choiceContext, setupTask, player];
#else
    internal static readonly MethodInfo SetupPlayerTurnMethod = typeof(CombatManager).GetMethod(
        "SetupPlayerTurn",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [CombatTurnStateType, typeof(Player), typeof(HookPlayerChoiceContext)],
        modifiers: null)
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "SetupPlayerTurn");

    internal static readonly MethodInfo RunAutoPrePlayPhaseMethod = typeof(CombatManager).GetMethod(
        "RunAutoPrePlayPhase",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [CombatTurnStateType, typeof(HookPlayerChoiceContext), typeof(Task), typeof(Player)],
        modifiers: null)
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "RunAutoPrePlayPhase");

    internal static object?[] BuildSetupPlayerTurnArguments(
        object? turnState,
        Player player,
        HookPlayerChoiceContext choiceContext)
        => [turnState, player, choiceContext];

    internal static object?[] BuildRunAutoPrePlayPhaseArguments(
        object? turnState,
        HookPlayerChoiceContext choiceContext,
        Task setupTask,
        Player player)
        => [turnState, choiceContext, setupTask, player];
#endif
}
