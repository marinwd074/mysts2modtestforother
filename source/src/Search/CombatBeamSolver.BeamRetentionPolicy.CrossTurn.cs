using System;
using System.Collections.Generic;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed partial class BeamRetentionPolicy
    {
        private readonly record struct CrossTurnProbeFamilyKey(
            StateFingerprint ShapeKey,
            StateFingerprint SemanticStateKey,
            int PotionCount,
            CrossTurnProbeTracker? Tracker);

        public void AddCrossTurnPortfolio(
            IReadOnlyList<SearchNode> pool,
            List<SearchNode> selected,
            HashSet<SearchNode> selectedSet)
        {
            List<SearchNode> eligible = [];
            int bestMaxHp = int.MinValue;
            foreach (SearchNode node in pool)
            {
                if (node.CrossTurnProbe == null && !RequiresCrossTurnPlanning(node))
                    continue;
                eligible.Add(node);
                bestMaxHp = Math.Max(bestMaxHp, node.Snapshot.PlayerMaxHp);
            }
            if (eligible.Count == 0)
                return;

            long minimumHealthRisk = long.MaxValue;
            foreach (SearchNode node in eligible)
                minimumHealthRisk = Math.Min(
                    minimumHealthRisk,
                    CombatBeamSolver.CycleHealthRisk(node, bestMaxHp));
            List<SearchNode> retained = [];
            foreach (bool investmentBand in new[] { false, true })
            {
                bool InBand(SearchNode node)
                    => (CombatBeamSolver.CycleHealthRisk(node, bestMaxHp) > minimumHealthRisk)
                        == investmentBand;

                Dictionary<CrossTurnProbeFamilyKey, int> inFlightIndexes = [];
                List<SearchNode> inFlight = [];
                Dictionary<CrossTurnProbeFamilyKey, int> newFamilyIndexes = [];
                List<SearchNode> newFamilies = [];
                foreach (SearchNode node in eligible)
                {
                    if (!InBand(node))
                        continue;
                    if (node.CrossTurnProbe != null)
                    {
                        AddCrossTurnFamilyBest(
                            inFlightIndexes,
                            inFlight,
                            node,
                            bestMaxHp);
                    }
                    else
                    {
                        AddCrossTurnFamilyBest(
                            newFamilyIndexes,
                            newFamilies,
                            node,
                            bestMaxHp);
                    }
                }

                List<SearchNode> band = [];
                SearchNode? continuing = FindBestCrossTurnCandidate(inFlight, bestMaxHp);
                if (continuing != null)
                    band.Add(continuing);
                SearchNode? newest = FindBestCrossTurnCandidate(newFamilies, bestMaxHp);
                if (newest != null)
                    band.Add(newest);

                SearchNode? fallbackFirst = null;
                SearchNode? fallbackSecond = null;
                foreach (SearchNode candidate in inFlight)
                {
                    AddCrossTurnFallbackCandidate(
                        candidate,
                        band,
                        bestMaxHp,
                        ref fallbackFirst,
                        ref fallbackSecond);
                }
                foreach (SearchNode candidate in newFamilies)
                {
                    AddCrossTurnFallbackCandidate(
                        candidate,
                        band,
                        bestMaxHp,
                        ref fallbackFirst,
                        ref fallbackSecond);
                }
                if (band.Count < 2 && fallbackFirst != null)
                    band.Add(fallbackFirst);
                if (band.Count < 2 && fallbackSecond != null)
                    band.Add(fallbackSecond);
                foreach (SearchNode candidate in band)
                    retained.Add(candidate);
            }

            HashSet<SearchNode> retainedSet = new(retained, ReferenceEqualityComparer.Instance);
            foreach (SearchNode node in pool)
            {
                if (node.CrossTurnProbe != null && !retainedSet.Contains(node))
                    node.CrossTurnProbe = null;
            }

            int rank = 0;
            foreach (SearchNode candidate in retained)
            {
                if (candidate.CrossTurnProbe == null)
                    StartCrossTurnProbe(candidate);
                candidate.CrossTurnRetentionRank = _profile.BeamWidth + 8 + rank++;
                if (!selectedSet.Add(candidate))
                    continue;
                candidate.RetentionRank = int.MaxValue;
                candidate.LongTermResourceRetentionRank = int.MaxValue;
                candidate.CycleRetentionRank = int.MaxValue;
                candidate.CycleExitRetentionRank = int.MaxValue;
                selected.Add(candidate);
            }
        }

        private static void AddCrossTurnFamilyBest(
            Dictionary<CrossTurnProbeFamilyKey, int> familyIndexes,
            List<SearchNode> familyBest,
            SearchNode candidate,
            int bestMaxHp)
        {
            CrossTurnProbeFamilyKey family = BuildCrossTurnProbeFamilyKey(candidate);
            if (!familyIndexes.TryGetValue(family, out int index))
            {
                familyIndexes.Add(family, familyBest.Count);
                familyBest.Add(candidate);
                return;
            }
            if (CompareCrossTurnCandidates(candidate, familyBest[index], bestMaxHp) < 0)
                familyBest[index] = candidate;
        }

        private static SearchNode? FindBestCrossTurnCandidate(
            IReadOnlyList<SearchNode> candidates,
            int bestMaxHp)
        {
            SearchNode? best = null;
            foreach (SearchNode candidate in candidates)
            {
                if (best == null || CompareCrossTurnCandidates(candidate, best, bestMaxHp) < 0)
                    best = candidate;
            }
            return best;
        }

        private static void AddCrossTurnFallbackCandidate(
            SearchNode candidate,
            IReadOnlyList<SearchNode> alreadySelected,
            int bestMaxHp,
            ref SearchNode? first,
            ref SearchNode? second)
        {
            if (ContainsReference(alreadySelected, candidate))
                return;
            if (first == null || CompareCrossTurnCandidates(candidate, first, bestMaxHp) < 0)
            {
                second = first;
                first = candidate;
            }
            else if (second == null
                     || CompareCrossTurnCandidates(candidate, second, bestMaxHp) < 0)
            {
                second = candidate;
            }
        }

        private static int CompareCrossTurnCandidates(
            SearchNode left,
            SearchNode right,
            int bestMaxHp)
        {
            int comparison = CombatBeamSolver.CycleHealthRisk(left, bestMaxHp)
                .CompareTo(CombatBeamSolver.CycleHealthRisk(right, bestMaxHp));
            if (comparison != 0)
                return comparison;
            comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
            if (comparison != 0)
                return comparison;
            bool leftChanged = left.CrossTurnProbe?.LastTurnChangedSemanticState
                ?? left.CrossTurnSemanticStateChanged;
            bool rightChanged = right.CrossTurnProbe?.LastTurnChangedSemanticState
                ?? right.CrossTurnSemanticStateChanged;
            comparison = rightChanged.CompareTo(leftChanged);
            if (comparison != 0)
                return comparison;
            int leftConsecutiveChanges =
                left.CrossTurnProbe?.ConsecutiveSemanticStateChangeTransitions
                    ?? (left.CrossTurnSemanticStateChanged ? 1 : 0);
            int rightConsecutiveChanges =
                right.CrossTurnProbe?.ConsecutiveSemanticStateChangeTransitions
                    ?? (right.CrossTurnSemanticStateChanged ? 1 : 0);
            comparison = rightConsecutiveChanges.CompareTo(leftConsecutiveChanges);
            if (comparison != 0)
                return comparison;
            int leftChanges = left.CrossTurnProbe?.SemanticStateChangeTransitions
                ?? (left.CrossTurnSemanticStateChanged ? 1 : 0);
            int rightChanges = right.CrossTurnProbe?.SemanticStateChangeTransitions
                ?? (right.CrossTurnSemanticStateChanged ? 1 : 0);
            comparison = rightChanges.CompareTo(leftChanges);
            if (comparison != 0)
                return comparison;
            comparison = (right.CrossTurnProbe?.CompletedTurnTransitions ?? 0)
                .CompareTo(left.CrossTurnProbe?.CompletedTurnTransitions ?? 0);
            if (comparison != 0)
                return comparison;
            comparison = right.CombatProgress.TurnsWithoutProgress.CompareTo(
                left.CombatProgress.TurnsWithoutProgress);
            if (comparison != 0)
                return comparison;
            comparison = (right.CrossTurnProbe?.BestKnownProgressMagnitude ?? 0)
                .CompareTo(left.CrossTurnProbe?.BestKnownProgressMagnitude ?? 0);
            if (comparison != 0)
                return comparison;
            comparison = left.Turn.CompareTo(right.Turn);
            if (comparison != 0)
                return comparison;
            comparison = left.ActionCount.CompareTo(right.ActionCount);
            if (comparison != 0)
                return comparison;
            comparison = right.Snapshot.ProjectedPlayerHp.CompareTo(
                left.Snapshot.ProjectedPlayerHp);
            if (comparison != 0)
                return comparison;
            comparison = right.Score.CompareTo(left.Score);
            return comparison != 0
                ? comparison
                : CombatBeamSolver.CompareCycleCandidateDeterministicFingerprints(left, right);
        }

        private static CrossTurnProbeFamilyKey BuildCrossTurnProbeFamilyKey(SearchNode node)
            => node.CrossTurnProbe is { } probe
                ? new CrossTurnProbeFamilyKey(
                    probe.Tracker.OriginShapeKey,
                    probe.Tracker.OriginNode.StateKey,
                    node.PotionCount,
                    probe.Tracker)
                : new CrossTurnProbeFamilyKey(
                    node.Snapshot.CycleShapeKey,
                    node.StateKey,
                    node.PotionCount,
                    null);

        private void StartCrossTurnProbe(SearchNode node)
        {
            if (node.CrossTurnProbe != null)
                return;
            node.CrossTurnProbe = new CrossTurnProbeState(
                new CrossTurnProbeTracker(node, node.Snapshot.CycleShapeKey),
                0,
                node.CrossTurnSemanticStateChanged ? 1 : 0,
                node.CrossTurnSemanticStateChanged ? 1 : 0,
                0,
                false,
                node.CrossTurnSemanticStateChanged);
            _run.CrossTurnCandidatesProtected++;
        }

        public static bool RequiresCrossTurnPlanning(SearchNode node)
        {
            if (node.IsTerminal
                || node.BoundaryReason != SearchBoundaryReason.None
                || node.CycleExitProbe != null
                || node.Outcome == null)
            {
                return false;
            }
            return node.CombatProgress.TurnsWithoutProgress > 0
                || node.CrossTurnSemanticInvisibleToModeledQuality;
        }
    }
}
