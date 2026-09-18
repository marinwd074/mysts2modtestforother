using System;
using System.Collections.Generic;
using System.Linq;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed partial class BeamRetentionPolicy
    {
        public void AddCyclePortfolio(
            IReadOnlyList<SearchNode> pool,
            List<SearchNode> selected,
            HashSet<SearchNode> selectedSet)
        {
            foreach (SearchNode node in pool)
            {
                if (node.CycleProbeLease is { NextActionIndex: 0 } lease
                    && node.Cycle is { } cycle
                    && cycle.ShapeKey == lease.Tracker.ShapeKey
                    && cycle.SequenceKey == lease.Tracker.SequenceKey
                    && cycle.PeriodActions == lease.Tracker.PeriodActions
                    && !CombatBeamSolver.RequiresBoundedCyclePlanning(node))
                {
                    node.CycleProbeLease = null;
                }
            }
            List<SearchNode> eligible = [];
            foreach (SearchNode node in pool)
            {
                if (node.CycleProbeLease == null && !CombatBeamSolver.RequiresBoundedCyclePlanning(node))
                    continue;
                eligible.Add(node);
            }
            if (eligible.Count == 0)
                return;

            // Compute every candidate's startup key once. The five fixed-size buffers stay on this
            // stack frame, avoiding both repeated setup fingerprint work and per-bucket collections.
            CycleStartupNodeBuckets activeByBucket = default;
            CycleStartupNodeBuckets startupByBucket = default;
            CycleStartupKeyBuckets startupKeysByBucket = default;
            SearchNode? purificationAnchor = null;
            CycleStartupRetentionKey purificationKey = default;
            bool hasFirstDeckShape = false;
            int firstClutter = 0;
            int firstSize = 0;
            bool hasMeaningfulDeckImprovement = false;
            foreach (SearchNode candidate in eligible)
            {
                int healthRiskBucket = CycleStartupHealthRiskBucket(candidate);
                if (candidate.CycleProbeLease is { NextActionIndex: > 0 })
                {
                    SearchNode? active = activeByBucket[healthRiskBucket];
                    if (active == null
                        || CompareCycleProbeCandidates(
                            candidate,
                            active,
                            _initialPlayerMaxHp) < 0)
                    {
                        activeByBucket[healthRiskBucket] = candidate;
                    }
                    continue;
                }
                if (!CanOccupyCycleStartupReserve(candidate.Snapshot.ProjectedPlayerHp))
                    continue;

                CycleStartupRetentionKey key = BuildCycleStartupRetentionKey(candidate);
                SearchNode? startup = startupByBucket[healthRiskBucket];
                if (startup == null
                    || CompareCycleStartupKeys(
                        key,
                        startupKeysByBucket[healthRiskBucket]) < 0)
                {
                    startupByBucket[healthRiskBucket] = candidate;
                    startupKeysByBucket[healthRiskBucket] = key;
                }

                if (!hasFirstDeckShape)
                {
                    hasFirstDeckShape = true;
                    firstClutter = key.LiveDeckClutter;
                    firstSize = key.LiveDeckSize;
                }
                else if (key.LiveDeckClutter != firstClutter
                         || key.LiveDeckSize != firstSize)
                {
                    hasMeaningfulDeckImprovement = true;
                }
                if (purificationAnchor == null
                    || CompareCyclePurificationKeys(key, purificationKey) < 0)
                {
                    purificationAnchor = candidate;
                    purificationKey = key;
                }
            }

            // Each stable health-investment bucket owns one bounded lane. An already-issued
            // mid-period lease settles that bucket's obligation first; otherwise the slot starts
            // the deepest still-survivable setup in the bucket. This merges debt and startup into
            // one hard-capped portfolio instead of letting either reserve grow independently.
            List<SearchNode> leased = new(6);
            for (int healthRiskBucket = 0; healthRiskBucket <= 4; healthRiskBucket++)
            {
                SearchNode? candidate = activeByBucket[healthRiskBucket]
                    ?? startupByBucket[healthRiskBucket];
                if (candidate != null)
                    leased.Add(candidate);
            }
            if (hasMeaningfulDeckImprovement
                && purificationAnchor != null
                && !ContainsReference(leased, purificationAnchor))
            {
                leased.Add(purificationAnchor);
            }
            if (leased.Count > 6)
                throw new InvalidOperationException("循环 startup portfolio 超过 6 条硬上限。");

            HashSet<SearchNode> leasedSet = new(leased, ReferenceEqualityComparer.Instance);
            foreach (SearchNode candidate in pool)
            {
                if (candidate.CycleProbeLease != null && !leasedSet.Contains(candidate))
                    candidate.CycleProbeLease = null;
            }

            int rank = 0;
            foreach (SearchNode candidate in leased)
            {
                if (candidate.CycleProbeLease == null)
                    CombatBeamSolver.StartCycleProbeLease(candidate);
                // Only final portfolio winners reach this point. A retained health-investment
                // lane asks its already-admitted family for the matching staged observation
                // budget; rejected siblings cannot mint an epoch or a family ledger. The issued
                // tracker supplies the exact canonical family without re-hashing the action path.
                RequestRetainedCycleStartupImprovementEpoch(
                    candidate,
                    CycleStartupHealthRiskBucket(candidate));
                candidate.CycleRetentionRank = _profile.BeamWidth + rank++;
                if (!selectedSet.Add(candidate))
                    continue;
                // RankBest mutates ranks for every examined node. A cycle-only admission must
                // remain behind all ordinary and long-term retained routes.
                candidate.RetentionRank = int.MaxValue;
                candidate.LongTermResourceRetentionRank = int.MaxValue;
                selected.Add(candidate);
                _run.CycleCandidatesProtected++;
            }
        }

        private CycleStartupRetentionKey BuildCycleStartupRetentionKey(SearchNode node)
        {
            long healthRisk = CycleStartupHealthRisk(node);
            StateFingerprint actionFingerprint =
                CombatBeamSolver.BuildCycleDeterministicActionFingerprint(node.Action);
            StateFingerprint parentFingerprint = node.Parent?.StateKey ?? default;
            return new CycleStartupRetentionKey(
                CombatBeamSolver.CycleStartupHealthRiskBucket(_initialPlayerHp, healthRisk),
                healthRisk,
                node.PotionStrategicCost,
                node.Snapshot.LiveDeckClutter,
                node.Snapshot.LiveDeckSize,
                CombatBeamSolver.CycleRegionSetupValue(node.Snapshot),
                BuildCycleStartupStableFingerprint(
                    node.StateKey,
                    actionFingerprint,
                    parentFingerprint));
        }

        internal static StateFingerprint BuildCycleStartupStableFingerprint(
            StateFingerprint state,
            StateFingerprint action,
            StateFingerprint parent)
        {
            StateFingerprintBuilder stable = new();
            stable.Add(state.First);
            stable.Add(state.Second);
            stable.Add(action.First);
            stable.Add(action.Second);
            stable.Add(parent.First);
            stable.Add(parent.Second);
            return stable.Finish();
        }

        internal static T? SelectCycleStartupBucketRepresentative<T>(
            IEnumerable<T> candidates,
            int healthRiskBucket,
            Func<T, bool> isEligible,
            Func<T, CycleStartupRetentionKey> keySelector)
            where T : class
        {
            T? best = null;
            CycleStartupRetentionKey bestKey = default;
            foreach (T candidate in candidates)
            {
                if (!isEligible(candidate))
                    continue;
                CycleStartupRetentionKey key = keySelector(candidate);
                if (key.HealthRiskBucket != healthRiskBucket)
                    continue;
                if (best == null || CompareCycleStartupKeys(key, bestKey) < 0)
                {
                    best = candidate;
                    bestKey = key;
                }
            }
            return best;
        }

        internal static T? SelectCyclePurificationAnchor<T>(
            IEnumerable<T> candidates,
            Func<T, bool> isEligible,
            Func<T, CycleStartupRetentionKey> keySelector)
            where T : class
        {
            T? best = null;
            CycleStartupRetentionKey bestKey = default;
            bool hasFirstDeckShape = false;
            int firstClutter = 0;
            int firstSize = 0;
            bool hasMeaningfulDeckImprovement = false;
            foreach (T candidate in candidates)
            {
                if (!isEligible(candidate))
                    continue;
                CycleStartupRetentionKey key = keySelector(candidate);
                if (!hasFirstDeckShape)
                {
                    hasFirstDeckShape = true;
                    firstClutter = key.LiveDeckClutter;
                    firstSize = key.LiveDeckSize;
                }
                else if (key.LiveDeckClutter != firstClutter
                         || key.LiveDeckSize != firstSize)
                {
                    hasMeaningfulDeckImprovement = true;
                }
                if (best == null || CompareCyclePurificationKeys(key, bestKey) < 0)
                {
                    best = candidate;
                    bestKey = key;
                }
            }
            return hasMeaningfulDeckImprovement ? best : null;
        }

        private static int CompareCycleStartupKeys(
            CycleStartupRetentionKey left,
            CycleStartupRetentionKey right)
        {
            int comparison = right.HealthRisk.CompareTo(left.HealthRisk);
            if (comparison != 0)
                return comparison;
            comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
            if (comparison != 0)
                return comparison;
            comparison = left.LiveDeckClutter.CompareTo(right.LiveDeckClutter);
            if (comparison != 0)
                return comparison;
            comparison = left.LiveDeckSize.CompareTo(right.LiveDeckSize);
            if (comparison != 0)
                return comparison;
            comparison = right.SetupValue.CompareTo(left.SetupValue);
            return comparison != 0
                ? comparison
                : CompareCycleStartupStableFingerprints(left, right);
        }

        private static int CompareCyclePurificationKeys(
            CycleStartupRetentionKey left,
            CycleStartupRetentionKey right)
        {
            int comparison = left.LiveDeckClutter.CompareTo(right.LiveDeckClutter);
            if (comparison != 0)
                return comparison;
            comparison = left.LiveDeckSize.CompareTo(right.LiveDeckSize);
            if (comparison != 0)
                return comparison;
            comparison = right.SetupValue.CompareTo(left.SetupValue);
            if (comparison != 0)
                return comparison;
            comparison = left.HealthRisk.CompareTo(right.HealthRisk);
            if (comparison != 0)
                return comparison;
            comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
            return comparison != 0
                ? comparison
                : CompareCycleStartupStableFingerprints(left, right);
        }

        private static int CompareCycleStartupStableFingerprints(
            CycleStartupRetentionKey left,
            CycleStartupRetentionKey right)
        {
            int comparison = left.StableFingerprint.First.CompareTo(
                right.StableFingerprint.First);
            return comparison != 0
                ? comparison
                : left.StableFingerprint.Second.CompareTo(right.StableFingerprint.Second);
        }

        public void AddCycleExitPortfolio(
            IReadOnlyList<SearchNode> pool,
            List<SearchNode> selected,
            HashSet<SearchNode> selectedSet)
        {
            List<SearchNode> eligible = [];
            foreach (SearchNode node in pool)
            {
                if (node.CycleExitProbe is not { RemainingActions: > 0 })
                    continue;
                eligible.Add(node);
            }
            if (eligible.Count == 0)
                return;

            List<SearchNode> leased = new(6);
            List<SearchNode> newestRepresentatives = [];
            for (int healthRiskBucket = 0; healthRiskBucket <= 4; healthRiskBucket++)
            {
                Dictionary<CycleExitProbeFamilyKey, int> familyIndexes = [];
                List<SearchNode> representatives = [];
                foreach (SearchNode node in eligible)
                {
                    if (CycleStartupHealthRiskBucket(node) != healthRiskBucket)
                        continue;
                    CycleExitProbeFamilyKey family = BuildCycleExitProbeFamilyKey(node);
                    if (!familyIndexes.TryGetValue(family, out int index))
                    {
                        familyIndexes.Add(family, representatives.Count);
                        representatives.Add(node);
                        continue;
                    }
                    if (CompareCycleExitFamilyCandidates(
                            node,
                            representatives[index],
                            _initialPlayerMaxHp) < 0)
                    {
                        representatives[index] = node;
                    }
                }
                if (representatives.Count == 0)
                    continue;
                newestRepresentatives.AddRange(representatives);

                // At most one already-issued lookahead debt survives per stable health-risk bucket.
                // New tickets share one global newest slot below, keeping the combined hard cap six.
                SearchNode? inFlight = FindActiveCycleExitCandidate(
                    representatives,
                    leased,
                    _initialPlayerMaxHp,
                    CycleExitCandidateRank.InFlight);
                if (inFlight != null)
                    leased.Add(inFlight);
            }

            SearchNode? newest = FindActiveCycleExitCandidate(
                newestRepresentatives,
                leased,
                _initialPlayerMaxHp,
                CycleExitCandidateRank.Newest);
            if (newest != null && !ContainsReference(leased, newest))
                leased.Add(newest);
            if (leased.Count > 6)
                throw new InvalidOperationException("循环出口 portfolio 超过 6 条硬上限。");

            int rank = 0;
            foreach (SearchNode candidate in leased)
            {
                candidate.CycleExitRetentionRank = _profile.BeamWidth + 4 + rank++;
                if (!selectedSet.Add(candidate))
                    continue;
                candidate.RetentionRank = int.MaxValue;
                candidate.LongTermResourceRetentionRank = int.MaxValue;
                candidate.CycleRetentionRank = int.MaxValue;
                selected.Add(candidate);
            }

            // Ticket settlement is intentionally delayed until the final Prune survivor set is
            // known. Region retention and the in-Prune primary-incumbent bound run after this
            // portfolio; any later destructive filter must invoke the same settlement finalizer.
        }

        private enum CycleExitCandidateRank : byte
        {
            InFlight,
            Newest,
        }

        private static SearchNode? FindActiveCycleExitCandidate(
            IReadOnlyList<SearchNode> representatives,
            IReadOnlyList<SearchNode> bandLeases,
            int bestMaxHp,
            CycleExitCandidateRank rank)
        {
            while (true)
            {
                SearchNode? best = null;
                foreach (SearchNode candidate in representatives)
                {
                    if (ContainsReference(bandLeases, candidate)
                        || candidate.CycleExitProbe is not { } probe
                        || rank == CycleExitCandidateRank.InFlight && !probe.LeaseIssued
                        || rank == CycleExitCandidateRank.Newest && probe.LeaseIssued)
                    {
                        continue;
                    }
                    if (best == null
                        || CompareCycleExitCandidates(candidate, best, bestMaxHp, rank) < 0)
                    {
                        best = candidate;
                    }
                }
                if (best == null || TryLeaseCycleExitCandidate(best))
                    return best;
                // A newer pending generation can supersede siblings created in the same wave.
                // Never let that stale ticket consume one of the two bounded portfolio slots.
            }
        }

        private static bool TryLeaseCycleExitCandidate(SearchNode candidate)
        {
            CycleExitProbeState probe = candidate.CycleExitProbe
                ?? throw new InvalidOperationException("循环出口探测候选缺少票据。");
            // Once a ticket has been issued, every exact simulator child produced from that
            // branch owns an independent bounded continuation. One sibling may reach a terminal
            // or budget boundary before another; settling the tracker generation must not revoke
            // the already-issued lease carried by the latter sibling.
            if (probe.LeaseIssued)
                return true;
            if (!probe.OriginTracker.TryMarkExitProbeIssued(
                    probe.OriginPhaseIndex,
                    probe.ExitActionKey,
                    probe.OriginGeneration))
            {
                candidate.CycleExitProbe = null;
                return false;
            }
            candidate.CycleExitProbe = probe with { LeaseIssued = true };
            return true;
        }

        private static int CompareCycleExitFamilyCandidates(
            SearchNode left,
            SearchNode right,
            int bestMaxHp)
        {
            // A freshly issued ticket is already an in-flight obligation even before its first
            // expansion consumes horizon. Prefer it over pending siblings from the same exact
            // family so the per-bucket debt lane cannot silently discard a live ticket.
            int leftLeasePriority = left.CycleExitProbe is { LeaseIssued: true } ? 0 : 1;
            int rightLeasePriority = right.CycleExitProbe is { LeaseIssued: true } ? 0 : 1;
            int comparison = leftLeasePriority.CompareTo(rightLeasePriority);
            if (comparison != 0)
                return comparison;
            comparison = (left.CycleExitProbe?.RemainingActions ?? int.MaxValue)
                .CompareTo(right.CycleExitProbe?.RemainingActions ?? int.MaxValue);
            if (comparison != 0)
                return comparison;
            comparison = CombatBeamSolver.CycleHealthRisk(left, bestMaxHp)
                .CompareTo(CombatBeamSolver.CycleHealthRisk(right, bestMaxHp));
            if (comparison != 0)
                return comparison;
            comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
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

        private static int CompareCycleExitCandidates(
            SearchNode left,
            SearchNode right,
            int bestMaxHp,
            CycleExitCandidateRank rank)
            => rank switch
            {
                CycleExitCandidateRank.InFlight => CompareCycleExitInFlightCandidates(
                    left,
                    right,
                    bestMaxHp),
                CycleExitCandidateRank.Newest => CompareCycleExitNewestCandidates(
                    left,
                    right,
                    bestMaxHp),
                _ => throw new ArgumentOutOfRangeException(nameof(rank), rank, null),
            };

        private static int CompareCycleExitInFlightCandidates(
            SearchNode left,
            SearchNode right,
            int bestMaxHp)
        {
            int comparison = (left.CycleExitProbe?.RemainingActions ?? int.MaxValue)
                .CompareTo(right.CycleExitProbe?.RemainingActions ?? int.MaxValue);
            if (comparison != 0)
                return comparison;
            comparison = CombatBeamSolver.CycleHealthRisk(left, bestMaxHp)
                .CompareTo(CombatBeamSolver.CycleHealthRisk(right, bestMaxHp));
            if (comparison != 0)
                return comparison;
            comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
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

        private static int CompareCycleExitNewestCandidates(
            SearchNode left,
            SearchNode right,
            int bestMaxHp)
        {
            int comparison = (right.CycleExitProbe?.OriginNode.ActionCount ?? 0)
                .CompareTo(left.CycleExitProbe?.OriginNode.ActionCount ?? 0);
            if (comparison != 0)
                return comparison;
            comparison = (right.CycleExitProbe?.OriginGeneration ?? 0)
                .CompareTo(left.CycleExitProbe?.OriginGeneration ?? 0);
            if (comparison != 0)
                return comparison;
            comparison = CombatBeamSolver.CycleHealthRisk(left, bestMaxHp)
                .CompareTo(CombatBeamSolver.CycleHealthRisk(right, bestMaxHp));
            if (comparison != 0)
                return comparison;
            comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
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

        internal static CycleExitProbeFamilyKey BuildCycleExitProbeFamilyKey(SearchNode node)
        {
            CycleExitProbeState probe = node.CycleExitProbe
                ?? throw new InvalidOperationException("循环出口探测候选缺少族证据。");
            return new CycleExitProbeFamilyKey(
                probe.OriginShapeKey,
                probe.OriginSequenceKey,
                probe.OriginPeriodActions,
                probe.OriginPhaseIndex,
                probe.OriginTracker,
                probe.OriginGeneration,
                probe.ExitActionKey);
        }

        internal static CycleExitProbeTicketKey BuildCycleExitProbeTicketKey(SearchNode node)
        {
            CycleExitProbeState probe = node.CycleExitProbe
                ?? throw new InvalidOperationException("循环出口探测候选缺少票据。");
            return new CycleExitProbeTicketKey(
                probe.OriginTracker,
                probe.OriginPhaseIndex,
                probe.ExitActionKey,
                probe.OriginGeneration);
        }

        private static int CompareCycleProbeCandidates(
            SearchNode left,
            SearchNode right,
            int bestMaxHp)
        {
            // Finish an already-issued exact phase lease before rotating to another family.
            // The lease remains bounded by the repetition budget and never affects final quality.
            int comparison = CombatBeamSolver.CycleHealthRisk(left, bestMaxHp)
                .CompareTo(CombatBeamSolver.CycleHealthRisk(right, bestMaxHp));
            if (comparison != 0)
                return comparison;
            comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
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
            comparison = (right.Cycle?.TotalStructuralRepetitions ?? 0)
                .CompareTo(left.Cycle?.TotalStructuralRepetitions ?? 0);
            if (comparison != 0)
                return comparison;
            comparison = right.Score.CompareTo(left.Score);
            return comparison != 0
                ? comparison
                : CombatBeamSolver.CompareCycleCandidateDeterministicFingerprints(left, right);
        }

        internal CycleProbeFamilyKey BuildCycleProbeFamilyKey(SearchNode node)
        {
            if (node.CycleProbeLease is { } lease)
            {
                return new CycleProbeFamilyKey(
                    node.Turn,
                    lease.Tracker.ShapeKey,
                    lease.Tracker.SequenceKey,
                    lease.Tracker.PeriodActions,
                    CycleStartupHealthRiskBucket(node),
                    lease.Tracker);
            }
            CycleSearchState cycle = node.Cycle
                ?? throw new InvalidOperationException("循环探测候选缺少族证据。");
            return new CycleProbeFamilyKey(
                node.Turn,
                cycle.ShapeKey,
                cycle.SequenceKey,
                cycle.PeriodActions,
                CycleStartupHealthRiskBucket(node),
                null);
        }

        private long CycleStartupHealthRisk(SearchNode node)
            => CombatBeamSolver.CycleHealthRisk(node, _initialPlayerMaxHp);

        private int CycleStartupHealthRiskBucket(SearchNode node)
            => CombatBeamSolver.CycleStartupHealthRiskBucket(
                _initialPlayerHp,
                CycleStartupHealthRisk(node));

        private void RequestRetainedCycleStartupImprovementEpoch(
            SearchNode candidate,
            int healthRiskBucket)
        {
            if (healthRiskBucket <= 0
                || candidate.CycleProbeLease is not { } lease)
            {
                return;
            }
            _ = CombatBeamSolver.TryRequestCycleFamilyImprovementEpochAtLeast(
                _run.CycleFamilyLedger,
                lease.Tracker.FamilyKey,
                healthRiskBucket);
        }
    }
}
