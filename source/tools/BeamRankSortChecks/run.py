"""Run an isolated order contract against the current production ranking methods."""
from pathlib import Path
import re
import subprocess
from xml.sax.saxutils import escape

repo = Path(__file__).resolve().parents[2]
output = repo / '.local/beam-rank-sort-checks'
output.mkdir(parents=True, exist_ok=True)
retention_source = (repo / 'src/Search/CombatBeamSolver.BeamRetentionPolicy.cs').read_text(encoding='utf-8')
ranking_source = (repo / 'src/Search/CombatBeamSolver.BeamRanking.cs').read_text(encoding='utf-8')
snapshot_source = (repo / 'src/Search/CombatPlan.cs').read_text(encoding='utf-8')
retained_source = (repo / 'src/Search/CombatBeamSolver.Retention.cs').read_text(encoding='utf-8')
transposition_source = (repo / 'src/Search/CombatBeamSolver.Transpositions.cs').read_text(encoding='utf-8')

def block(source, signature):
    start = source.index(signature)
    cursor = source.index('{', start)
    depth = 1
    end = cursor + 1
    while depth:
        if source[end] == '{': depth += 1
        elif source[end] == '}': depth -= 1
        end += 1
    return source[start:end]

score = block(retention_source, 'private double BeamRankScore(SearchNode node)').replace('private double', 'public double', 1)
retained = retention_source[retention_source.index('private int RetainedAttackGrowth(SimulationSnapshot snapshot)'):]
retained = retained[:retained.index(';') + 1]
compare = block(ranking_source, 'internal static int CompareBeamRankOrder(').replace('internal static', 'public static', 1)
sort = block(retention_source, 'private void SortByBeamRank(List<SearchNode> ranked)').replace('private void', 'public void', 1)
retained_compare = block(retained_source, 'private static int CompareRetainedOrder(').replace('private static', 'public static', 1)
route_traits = block(snapshot_source, 'internal enum SearchRouteTraits')
fields = sorted(set(re.findall(r'(?:node\.Snapshot|snapshot)\.(\w+)', score + retained)))
for name in fields + ['OffensiveProgressValue']:
    if not re.search(r'public int ' + name + r'\s*\{', snapshot_source):
        raise RuntimeError(f'Update probe for changed snapshot field: {name}')
classes = '''namespace CombatSolver;
''' + route_traits + '''
internal sealed class SearchNode {
public double Score;
public int ActionCount;
public required SimulationSnapshot Snapshot;
public int RetentionRank = int.MaxValue;
public int LongTermResourceRetentionRank = int.MaxValue;
public int CycleRetentionRank = int.MaxValue;
public int CycleExitRetentionRank = int.MaxValue;
public int CrossTurnRetentionRank = int.MaxValue;
public int Stable;
public int PotionCount;
public int PotionStrategicCost;
public int FutureSoldHp;
public int CumulativePlayerHpLost;
public SearchRouteTraits Traits;
public bool HasNonPotionAction;
}
internal sealed class SimulationSnapshot {
'''
classes += '\n'.join(f'public int {name} {{ get; init; }}' for name in fields + ['OffensiveProgressValue'])
classes += '''
}
internal sealed class Run { public int InitialPersistentBuffValue, InitialEnemyStrengthSuppression, InitialEnemyWeakTurns, InitialRetainedAttackValue; }
internal sealed class SolverSearchProfile { public bool BaseScoreOnly { get; init; } }
internal sealed class Scorer(bool boss, int enemies, Run initial) {
private readonly bool _isActEndingBoss = boss;
private readonly int _initialEnemyCount = enemies;
private readonly Run _run = initial;
private readonly SolverSearchProfile _profile = new();
private static int CompareCycleCandidateDeterministicFingerprints(SearchNode left, SearchNode right)
    => left.Stable.CompareTo(right.Stable);
'''
classes += '\n'.join([score, retained, compare, sort, retained_compare]) + '\n}'
(output / 'Extracted.cs').write_text(classes, encoding='utf-8')
(output / 'Transpositions.cs').write_text(transposition_source, encoding='utf-8')
(output / 'TranspositionProbe.cs').write_text('''namespace CombatSolver;
internal sealed partial class CombatBeamSolver
{
    internal static bool TryAcceptTranspositionForCheck(
        SearchRouteTraits firstTraits,
        bool firstHasNonPotionAction,
        SearchRouteTraits nextTraits,
        bool nextHasNonPotionAction)
    {
        TranspositionLabel first = new(
            0, 0, 0, 0, 1, 10, firstTraits, firstHasNonPotionAction);
        TranspositionLabel next = new(
            0, 0, 0, 0, 1, 10, nextTraits, nextHasNonPotionAction);
        return new TranspositionFrontier(first).TryAccept(next);
    }
}
''', encoding='utf-8')
(output / 'Program.cs').write_bytes((repo / 'tools/BeamRankSortChecks/Program.cs').read_bytes())
(output / 'Checks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><Compile Include="''' + escape(str(repo / 'src/Search/SolverWeights.cs')) + '''" /></ItemGroup>
</Project>''', encoding='utf-8')
subprocess.run(['dotnet', 'run', '--project', str(output / 'Checks.csproj'), '-c', 'Release'], cwd=repo, check=True)
