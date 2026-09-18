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
fields = sorted(set(re.findall(r'(?:node\.Snapshot|snapshot)\.(\w+)', score + retained)))
for name in fields + ['OffensiveProgressValue']:
    if not re.search(r'public int ' + name + r'\s*\{', snapshot_source):
        raise RuntimeError(f'Update probe for changed snapshot field: {name}')
classes = '''namespace CombatSolver;
internal sealed class SearchNode { public double Score; public int ActionCount; public required SimulationSnapshot Snapshot; }
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
'''
classes += '\n'.join([score, retained, compare, sort]) + '\n}'
(output / 'Extracted.cs').write_text(classes, encoding='utf-8')
(output / 'Program.cs').write_bytes((repo / 'tools/BeamRankSortChecks/Program.cs').read_bytes())
(output / 'Checks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><Compile Include="''' + escape(str(repo / 'src/Search/SolverWeights.cs')) + '''" /></ItemGroup>
</Project>''', encoding='utf-8')
subprocess.run(['dotnet', 'run', '--project', str(output / 'Checks.csproj'), '-c', 'Release'], cwd=repo, check=True)
