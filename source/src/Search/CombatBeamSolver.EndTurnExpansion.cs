namespace CombatSolver;

// End-turn materialization is isolated from the general action expansion body;
// ownership transfer and transposition admission remain exactly as before.
internal sealed partial class CombatBeamSolver
{
    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> BuildEndTurnBranches(
        SearchNode node,
        IReadOnlyList<PlanCardChoice> choices)
    {
        PlanAction action = new(
            PlanActionKind.EndTurn,
            node.Turn,
            TurnStartChoices: choices.Count == 0 ? null : choices);
        SimulationSnapshot snapshot = ReplayAction(node, action);
        foreach ((PlanAction resolvedAction, SimulationSnapshot resolvedSnapshot) in
                 ResolveRoundChoiceBranches(node, action, snapshot))
        {
            yield return (resolvedAction, resolvedSnapshot);
        }
    }

    private IEnumerable<SearchNode> BuildAcceptedEndTurnNodes(SearchNode node)
    {
        using ExpansionBatch batch = RentExpansionBatch();
        GenerateRawEndTurnCandidates(node, batch);
        PruneCommittedCrossTurnCandidates(batch.EndTurns, batch);
        if (NeedsCycleExitAdmission(node, [], null, batch.EndTurns))
        {
            AnnotateCycleExitProgress(node, batch.EndTurns);
            _ = MaterializeAdmittedCycleExitObservation(
                batch.EndTurns,
                _run.CycleFamilyLedger);
        }
        foreach (SearchNode endNode in batch.EndTurns)
        {
            if (!TryAcceptTransposition(endNode))
            {
                batch.Release(endNode.Snapshot);
                continue;
            }
            batch.Transfer(endNode.Snapshot);
            yield return endNode;
        }
    }
}
