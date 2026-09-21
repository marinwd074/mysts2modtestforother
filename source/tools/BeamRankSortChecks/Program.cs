using CombatSolver;
using System.Text.Json;

Random random = new(20260912);
int cases = 0, entries = 0;
int[] sizes = [0, 1, 2, 3, 15, 16, 17, 31, 32, 33, 63, 64, 65, 255, 256, 257, 1024, 2048];
double[] extremes = [double.NaN, double.NegativeInfinity, double.PositiveInfinity, -0d, 0d, double.MinValue, double.MaxValue];
foreach (bool boss in new[] { false, true })
foreach (int enemies in new[] { 1, 2 })
foreach (bool negative in new[] { false, true })
{
    Scorer scorer = new(boss, enemies, new Run
    {
        InitialPersistentBuffValue = negative ? -5 : 12,
        InitialEnemyStrengthSuppression = negative ? -1 : 4,
        InitialEnemyWeakTurns = negative ? -9 : 2,
        InitialRetainedAttackValue = negative ? -20 : 6
    });
    foreach (int n in sizes)
    foreach (int pattern in Enumerable.Range(0, 5))
    {
        List<SearchNode> input = [];
        for (int i = 0; i < n; i++)
        {
            if (pattern == 3 && i > 0 && i % 3 == 0)
            {
                input.Add(input[i / 3]); // Same object appears more than once.
                continue;
            }
            int Value() => pattern == 0 ? 0 : random.Next(negative ? -100 : 0, 101);
            input.Add(new SearchNode
            {
                Score = pattern == 4 ? extremes[i % extremes.Length] : pattern == 0 ? 0 : random.Next(-20, 21) * 30000d,
                ActionCount = pattern == 0 ? 0 : random.Next(0, 12),
                Snapshot = new()
                {
                    Energy = Value(), PersistentBuffValue = Value(), LatentSetupValue = Value(),
                    FutureResourceValue = Value(), ReplayPotentialValue = Value(),
                    RetainedAttackValue = Value(), DelayedDamageValue = Value(),
                    SandpitRemaining = Value(), EnemyStrengthSuppression = Value(),
                    EnemyWeakTurns = Value(), OffensiveProgressValue = Value()
                }
            });
        }
        Comparison<SearchNode> original = (a, b) => Scorer.CompareBeamRankOrder(
            scorer.BeamRankScore(a), a.Snapshot.OffensiveProgressValue, a.ActionCount,
            scorer.BeamRankScore(b), b.Snapshot.OffensiveProgressValue, b.ActionCount);
        if (pattern == 1) input.Sort(original);
        if (pattern == 2) { input.Sort(original); input.Reverse(); }
        List<SearchNode> expected = [.. input], actual = [.. input];
        expected.Sort(original);
        scorer.SortByBeamRank(actual);
        if (actual.Count != expected.Count || actual.Where((node, index) => !ReferenceEquals(node, expected[index])).Any())
            throw new InvalidOperationException($"Order mismatch: boss={boss}, enemies={enemies}, n={n}, pattern={pattern}");
        cases++;
        entries += n;
    }
}

SearchNode Retained(
    int stable,
    double score,
    int retention = int.MaxValue,
    int longTerm = int.MaxValue,
    int cycle = int.MaxValue,
    int cycleExit = int.MaxValue,
    int crossTurn = int.MaxValue)
    => new()
    {
        Stable = stable,
        Score = score,
        RetentionRank = retention,
        LongTermResourceRetentionRank = longTerm,
        CycleRetentionRank = cycle,
        CycleExitRetentionRank = cycleExit,
        CrossTurnRetentionRank = crossTurn,
        Snapshot = new()
    };

SearchNode earlierRank = Retained(2, 0, cycle: 4);
SearchNode laterRank = Retained(1, double.MaxValue, cycleExit: 5);
if (Scorer.CompareRetainedOrder(earlierRank, laterRank) >= 0)
    throw new InvalidOperationException("Retention rank no longer precedes score.");

SearchNode higherScore = Retained(2, 10, cycle: 5);
SearchNode lowerScore = Retained(1, 9, cycleExit: 5);
if (Scorer.CompareRetainedOrder(higherScore, lowerScore) >= 0)
    throw new InvalidOperationException("Score no longer breaks equal retention ranks.");

List<SearchNode> stableTie =
[
    Retained(3, 10, cycleExit: 5),
    Retained(1, 10, cycle: 5),
    Retained(2, 10, crossTurn: 5),
];
stableTie.Sort(Scorer.CompareRetainedOrder);
if (!stableTie.Select(node => node.Stable).SequenceEqual([1, 2, 3]))
    throw new InvalidOperationException("Equal retention rank/score lacks deterministic final ordering.");

if (CombatBeamSolver.TryAcceptTranspositionForCheck(
        SearchRouteTraits.None, false,
        SearchRouteTraits.None, false))
    throw new InvalidOperationException("Equivalent transposition label was not dominated.");

if (!CombatBeamSolver.TryAcceptTranspositionForCheck(
        SearchRouteTraits.Scaling, false,
        SearchRouteTraits.Resource, false))
    throw new InvalidOperationException("Incomparable route traits were incorrectly merged.");

if (CombatBeamSolver.TryAcceptTranspositionForCheck(
        SearchRouteTraits.Scaling | SearchRouteTraits.Resource, false,
        SearchRouteTraits.Scaling, false))
    throw new InvalidOperationException("Trait superset no longer dominates a subset.");

if (!CombatBeamSolver.TryAcceptTranspositionForCheck(
        SearchRouteTraits.Scaling, false,
        SearchRouteTraits.Scaling | SearchRouteTraits.Resource, false))
    throw new InvalidOperationException("Trait superset candidate was incorrectly pruned.");

if (CombatBeamSolver.TryAcceptTranspositionForCheck(
        SearchRouteTraits.None, false,
        SearchRouteTraits.None, true))
    throw new InvalidOperationException("Route with more future potion options did not dominate.");

if (!CombatBeamSolver.TryAcceptTranspositionForCheck(
        SearchRouteTraits.None, true,
        SearchRouteTraits.None, false))
    throw new InvalidOperationException("Route with fewer future potion options incorrectly dominated.");

if (!CombatBeamSolver.TryAcceptTranspositionForCheck(
        SearchRouteTraits.None, false,
        SearchRouteTraits.None, false,
        firstProgress: 1, nextProgress: 2)
    || !CombatBeamSolver.TryAcceptTranspositionForCheck(
        SearchRouteTraits.None, false,
        SearchRouteTraits.None, false,
        firstProgress: 2, nextProgress: 1))
    throw new InvalidOperationException("Different combat-progress histories were incorrectly merged.");

Console.WriteLine(JsonSerializer.Serialize(new { status = "Passed", cases, entries, retained_tie_cases = 3, transposition_path_cases = 8, runtime = Environment.Version.ToString(), scope = "Extracted production ranking plus retained-order and path-sensitive transposition contracts" }));
