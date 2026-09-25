using System.Runtime.CompilerServices;

namespace CombatSolver;

// This project intentionally compiles the production transposition frontier in isolation.
// Minimal value types keep the test focused on the frontier contract rather than pulling in
// the complete game/runtime dependency graph.
[Flags]
internal enum SearchRouteTraits
{
    None = 0,
    Scaling = 1 << 0,
    Resource = 1 << 1,
    Control = 1 << 2,
}

internal enum SearchBoundaryReason
{
    None,
    NodeLimit,
    TimeLimit,
}

internal readonly record struct PredictionGap(string Code);

internal readonly record struct CombatProgressState(int Marker);

internal sealed partial class CombatBeamSolver
{
    private static int _checks;

    public static void Main()
    {
        VerifyE6SafetyDimensions();

        // Fixed seed; compare every acceptance decision to a literal independent
        // implementation of the current Pareto-label contract.
        Random random = new(64937);
        for (int sequence = 0; sequence < 4000; sequence++)
        {
            TranspositionLabel first = Next(random);
            TranspositionFrontier actual = new(first);
            Baseline expected = new(first);
            for (int step = 0; step < 48; step++)
            {
                TranspositionLabel next = step % 8 == 0 ? first : Next(random);
                Require(
                    actual.TryAccept(next) == expected.TryAccept(next),
                    "Transposition frontier decision differs from the independent baseline.");
            }
        }

        TranspositionLabel middle = Label(
            potionCount: 3,
            potionStrategicCost: 3,
            futureSoldHp: 3,
            cumulativePlayerHpLost: 3,
            actionCount: 3,
            score: 3);
        TranspositionFrontier changing = new(middle);
        Require(
            changing.TryAccept(middle with { PotionCount = 2, Score = 2 }),
            "A true Pareto tradeoff was rejected.");
        Require(
            changing.TryAccept(Label(score: 10)),
            "A dominating label was rejected.");
        Require(
            !changing.TryAccept(middle),
            "A dominated label survived frontier collapse.");
        Require(
            changing.TryAccept(Label(potionCount: 1, score: 11)),
            "The frontier could not expand after collapsing to one label.");

        for (int i = 0; i < 1000; i++)
        {
            _ = new Baseline(middle);
            _ = new TranspositionFrontier(middle);
        }
        const int count = 100_000;
        object[] retained = new object[count];
        long baselineBytes = Allocate(retained, middle, baseline: true);
        long candidateBytes = Allocate(retained, middle, baseline: false);
        GC.KeepAlive(retained);
        Require(
            candidateBytes < baselineBytes,
            "Single-label frontier storage did not reduce retained allocation.");

        Console.WriteLine(
            $"Passed {_checks} transposition checks; " +
            $"{count} retained singleton allocations: {baselineBytes} -> {candidateBytes} bytes.");
    }

    private static void VerifyE6SafetyDimensions()
    {
        TranspositionLabel exact = Label(
            actionCount: 1,
            score: 10,
            combatProgress: new CombatProgressState(7));

        Require(
            !new TranspositionFrontier(exact).TryAccept(exact),
            "E6 exact-equivalent labels must collapse instead of preserving an action-order duplicate.");

        Require(
            new TranspositionFrontier(exact).TryAccept(
                exact with { BoundaryReason = SearchBoundaryReason.NodeLimit }),
            "E6 must not merge labels across different search boundaries.");
        Require(
            new TranspositionFrontier(exact).TryAccept(
                exact with { PlayerDead = true }),
            "E6 must not merge live and dead-player terminal semantics.");
        Require(
            new TranspositionFrontier(exact).TryAccept(
                exact with { AllEnemiesDead = true }),
            "E6 must not merge live-combat and victory terminal semantics.");
        Require(
            new TranspositionFrontier(exact).TryAccept(
                exact with { PredictionGaps = [new PredictionGap("rng-or-hook-gap")] }),
            "E6 must not merge labels with different prediction-gap histories.");
        Require(
            new TranspositionFrontier(exact).TryAccept(
                exact with { CombatProgress = new CombatProgressState(8) }),
            "E6 must not merge labels with different combat-progress histories.");

        TranspositionLabel worse = Label(
            cumulativePlayerHpLost: 4,
            allPlayersAlive: true,
            teamLossRatio: 0.4,
            worstPlayerLossRatio: 0.5,
            actionCount: 3,
            score: 4);
        TranspositionLabel better = worse with
        {
            CumulativePlayerHpLost = 2,
            TeamLossRatio = 0.2,
            WorstPlayerLossRatio = 0.3,
            ActionCount = 2,
            Score = 6,
        };
        TranspositionFrontier objective = new(worse);
        Require(
            objective.TryAccept(better),
            "A strictly better objective label should replace a worse exact-state representative.");
        Require(
            !objective.TryAccept(worse),
            "A worse objective label must remain pruned after replacement.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Allocate(object[] retained, TranspositionLabel label, bool baseline)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < retained.Length; i++)
            retained[i] = baseline ? new Baseline(label) : new TranspositionFrontier(label);
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static TranspositionLabel Next(Random random)
    {
        double score = random.Next(0, 80) switch
        {
            0 => double.NaN,
            1 => double.PositiveInfinity,
            2 => double.NegativeInfinity,
            _ => random.Next(-8, 9),
        };
        PredictionGap[] gaps = random.Next(0, 4) switch
        {
            0 => [],
            1 => [new PredictionGap("choice")],
            2 => [new PredictionGap("rng")],
            _ => [new PredictionGap("choice"), new PredictionGap("rng")],
        };
        return Label(
            potionCount: random.Next(-2, 7),
            potionStrategicCost: random.Next(-2, 7),
            futureSoldHp: random.Next(-2, 7),
            cumulativePlayerHpLost: random.Next(-2, 7),
            allPlayersAlive: random.Next(0, 2) == 0,
            teamLossRatio: random.Next(-2, 7) / 10d,
            worstPlayerLossRatio: random.Next(-2, 7) / 10d,
            actionCount: random.Next(-2, 7),
            score: score,
            traits: (SearchRouteTraits)random.Next(0, 8),
            hasNonPotionAction: random.Next(0, 2) == 0,
            boundaryReason: (SearchBoundaryReason)random.Next(0, 3),
            playerDead: random.Next(0, 2) == 0,
            allEnemiesDead: random.Next(0, 2) == 0,
            predictionGaps: gaps,
            combatProgress: new CombatProgressState(random.Next(0, 4)));
    }

    private static TranspositionLabel Label(
        int potionCount = 0,
        int potionStrategicCost = 0,
        int futureSoldHp = 0,
        int cumulativePlayerHpLost = 0,
        bool allPlayersAlive = true,
        double teamLossRatio = 0,
        double worstPlayerLossRatio = 0,
        int actionCount = 0,
        double score = 0,
        SearchRouteTraits traits = SearchRouteTraits.None,
        bool hasNonPotionAction = false,
        SearchBoundaryReason boundaryReason = SearchBoundaryReason.None,
        bool playerDead = false,
        bool allEnemiesDead = false,
        IReadOnlyList<PredictionGap>? predictionGaps = null,
        CombatProgressState combatProgress = default)
        => new(
            potionCount,
            potionStrategicCost,
            futureSoldHp,
            cumulativePlayerHpLost,
            allPlayersAlive,
            teamLossRatio,
            worstPlayerLossRatio,
            actionCount,
            score,
            traits,
            hasNonPotionAction,
            boundaryReason,
            playerDead,
            allEnemiesDead,
            predictionGaps ?? [],
            combatProgress);

    private static void Require(bool condition, string message)
    {
        _checks++;
        if (!condition)
            throw new InvalidOperationException(message);
    }

    // Independent literal comparator: do not call production Dominates here.
    private sealed class Baseline(TranspositionLabel first)
    {
        private readonly List<TranspositionLabel> _labels = [first];

        public bool TryAccept(TranspositionLabel next)
        {
            foreach (TranspositionLabel current in _labels)
            {
                if (Dominates(current, next))
                    return false;
            }
            _labels.RemoveAll(current => Dominates(next, current));
            _labels.Add(next);
            return true;
        }

        private static bool Dominates(TranspositionLabel left, TranspositionLabel right)
            => left.PotionCount <= right.PotionCount
                && left.PotionStrategicCost <= right.PotionStrategicCost
                && left.FutureSoldHp <= right.FutureSoldHp
                && left.CumulativePlayerHpLost <= right.CumulativePlayerHpLost
                && (left.AllPlayersAlive || !right.AllPlayersAlive)
                && left.TeamLossRatio <= right.TeamLossRatio
                && left.WorstPlayerLossRatio <= right.WorstPlayerLossRatio
                && left.ActionCount <= right.ActionCount
                && left.Score >= right.Score
                && (left.Traits & right.Traits) == right.Traits
                && (!left.HasNonPotionAction || right.HasNonPotionAction)
                && left.BoundaryReason == right.BoundaryReason
                && left.PlayerDead == right.PlayerDead
                && left.AllEnemiesDead == right.AllEnemiesDead
                && left.PredictionGaps.SequenceEqual(right.PredictionGaps)
                && left.CombatProgress == right.CombatProgress;
    }
}
