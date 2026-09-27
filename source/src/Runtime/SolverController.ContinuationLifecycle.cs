namespace CombatSolver;

internal static partial class SolverController
{
    private static bool TryCaptureReusableContinuation(
        CombatState state,
        SolverSessionCapabilitySet capabilities,
        out ContinuationStamp? continuationStamp,
        out string rejectReason)
    {
        continuationStamp = null;
        rejectReason = "none";

        if (!capabilities.CanCrossTurnReuse
            || _combat.ContinuationSource == null)
        {
            rejectReason = "continuation_unavailable";
            return false;
        }

        ContinuationStamp candidate = ContinuationStamp.CaptureLive(state);
        if (!ContinuationLifecycleGuard.CanReuse(
                _combat.ContinuationSource,
                candidate,
                out rejectReason))
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] CONTINUATION_REJECT reason={rejectReason}");
            return false;
        }

        continuationStamp = candidate;
        return true;
    }
}
