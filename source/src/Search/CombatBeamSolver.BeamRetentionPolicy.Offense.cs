namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed partial class BeamRetentionPolicy
    {
        // Upstream 5d9a2d92/c9ebae2f. These reuse existing seats; reachable hand
        // value only selects a probe. Exact simulation remains the lethal proof.
        private static SearchNode? FindBestDamagingContinuation(IEnumerable<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (node.IsTerminal || node.Parent == null
                    || node.Action is not { Kind: PlanActionKind.PlayCard, EndsPlayerTurn: false }
                    || node.Snapshot.EnemyBlock >= node.Parent.Snapshot.EnemyBlock
                    || node.Snapshot.EnemyHp > node.Snapshot.ReachableHandValue
                    || (long)node.Snapshot.EnemyHp + node.Snapshot.EnemyBlock
                        >= (long)node.Parent.Snapshot.EnemyHp + node.Parent.Snapshot.EnemyBlock
                    || node.Snapshot.PlayableAttackCount <= 0) continue;
                if (IsBetterOffensive(node, best)) best = node;
            }
            return best;
        }

        private static SearchNode? FindBestSafeDiscardContinuation(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (node.IsTerminal || node.Parent == null
                    || node.Action is not { Kind: PlanActionKind.PlayCard, EndsPlayerTurn: false }
                    || ActionsSinceRetainedRoutingChoice(node) < 1
                    || node.Snapshot.ProjectedPlayerHp < node.Snapshot.PlayerHp) continue;
                if (best == null || node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                    || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                        && (node.Snapshot.OffensiveProgressValue > best.Snapshot.OffensiveProgressValue
                            || node.Snapshot.OffensiveProgressValue == best.Snapshot.OffensiveProgressValue
                                && (node.Snapshot.ReachableHandValue > best.Snapshot.ReachableHandValue
                                    || node.Snapshot.ReachableHandValue == best.Snapshot.ReachableHandValue
                                        && IsBetterOffensive(node, best)))) best = node;
            }
            return best;
        }
    }
}
