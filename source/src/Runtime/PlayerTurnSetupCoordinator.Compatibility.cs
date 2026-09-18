using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

// 0.107.1 does not expose the later native choice-setup interception point.
// Returning false lets normal player-turn flow reach the solver hook.
internal static class PlayerTurnSetupCoordinator
{
    public static bool IsSearching => false;
    public static bool IsStoppingSearch => false;
    public static bool CanApplyCurrentTurn => false;
    public static bool IsApplyingCurrentTurn => false;
    public static bool CanAdoptCurrentRoute => false;
    public static bool IsAdoptingCurrentRoute => false;
    public static SearchMemoryPressureSignal? CurrentMemoryPressureSignal => null;
    public static bool IsDrivingChoiceForRecording => false;

    public static bool IsManaging(CombatState state) => false;
    public static bool HasPendingPlannedChoice(CombatState state) => false;
    public static bool CanTakeOverTurnSetup(CombatState state) => false;
    public static bool TryContinuePlannedChoice(NGame host, CombatState state, bool deployAfterSetup) => false;
    public static bool TryQueueManualRecalculation(CombatState state) => false;
    public static bool TryRecalculatePendingChoice(NGame host, CombatState state) => false;
    public static bool TryStopSearchAtCurrentRoute() => false;
    public static bool StopSearchByUser() => false;
    public static void InvalidateRenderedRouteAdoptionSeed() { }
    public static void ApplyCurrentTurn() { }
    public static void AdoptCurrentRoute() { }
    public static Task Reset(string reason) => Task.CompletedTask;
    public static void CancelForSolverDisabled() { }
}
