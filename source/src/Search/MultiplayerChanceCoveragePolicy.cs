namespace CombatSolver;

internal readonly record struct MultiplayerChanceCoverageCandidate(
    int CandidateIndex,
    int DecisionRank,
    int ScenarioRank,
    bool AlreadyRetained);

/// <summary>
/// Fixed-cap final-only coverage scheduler. DecisionRank is the ordinary final-ranking order;
/// ScenarioRank is probability-first order within that decision. Existing retained scenarios
/// consume their natural round-robin position but are not returned as extra candidates.
/// </summary>
internal static class MultiplayerChanceCoveragePolicy
{
    internal static IReadOnlyList<int> SelectAdditionalCandidateIndices(
        IReadOnlyList<MultiplayerChanceCoverageCandidate> candidates,
        int extraLimit)
    {
        if (extraLimit <= 0 || candidates.Count == 0)
            return Array.Empty<int>();

        var decisions = candidates
            .GroupBy(candidate => candidate.DecisionRank)
            .OrderBy(group => group.Key)
            .Select(group => (IReadOnlyList<MultiplayerChanceCoverageCandidate>)group
                .OrderBy(candidate => candidate.ScenarioRank)
                .ThenBy(candidate => candidate.CandidateIndex)
                .ToArray())
            .ToArray();
        if (decisions.Length == 0)
            return Array.Empty<int>();

        int maximumRound = decisions.Max(group => group.Count);
        List<int> selected = new(Math.Min(extraLimit, candidates.Count));
        for (int round = 0; round < maximumRound && selected.Count < extraLimit; round++)
        {
            foreach (IReadOnlyList<MultiplayerChanceCoverageCandidate> decision in decisions)
            {
                if (round >= decision.Count)
                    continue;

                MultiplayerChanceCoverageCandidate candidate = decision[round];
                if (candidate.AlreadyRetained)
                    continue;

                selected.Add(candidate.CandidateIndex);
                if (selected.Count == extraLimit)
                    break;
            }
        }
        return selected;
    }
}
