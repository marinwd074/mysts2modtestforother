"""Compare the exact production strategic context with the pre-optimization algorithm."""
from pathlib import Path
import argparse
import subprocess
import sys
from xml.sax.saxutils import escape
repo=Path(__file__).resolve().parents[2]
out=repo/'.local/strategic-keyword-checks';out.mkdir(parents=True,exist_ok=True)
source=(repo/'src/Search/StrategicEffectModel.cs').read_text(encoding='utf-8')
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--native', action='store_true', help='run native keyword/context and branch isolation contracts')
parser.add_argument('--out', type=Path)
parser.add_argument('--steam-root', type=Path)
args=parser.parse_args()
if args.native:
 native=source[source.index('internal readonly record struct StrategicEffectContext('):source.index('internal static class StrategicEffectModel')]
 native=native.replace('StrategicEffectContext','MaterializedKeywordContext')
 native=native.replace('card.HasKeyword(predictionState, keyword)', 'card.GetKeywords(predictionState).Contains(keyword)')
 oracle=out/'MaterializedKeywordContext.cs'
 oracle.write_text(source[:source.index('[Flags]')]+native, encoding='utf-8')
 harness=repo/'tools/U0U1PinnedHarness/U0U1PinnedHarness.csproj'
 build=['dotnet','build',str(harness),'-c','Release',f'-p:StrategicContextOracle={oracle}']
 if args.steam_root:build.append(f'-p:SteamRoot={args.steam_root.as_posix()}')
 subprocess.run(build,cwd=repo,check=True)
 subprocess.run(['dotnet',str(harness.parent/'bin/Release/net9.0/U0U1PinnedHarness.dll'),
  'strategic-context','--out',str(args.out or out/'native')],cwd=repo,check=True)
 sys.exit(0)
start=source.index('[Flags]');end=source.index('internal static class StrategicEffectModel')
using=source[:source.index('[Flags]')]
(out/'Production.cs').write_text(using+source[start:end], encoding='utf-8')
# Reverse keyword demand reduction to keep the baseline's remaining formula in sync.
baseline=source[source.index('    public static StrategicEffectContext Build('):end].rstrip()
baseline=baseline[:baseline.rfind('}')]
baseline=baseline.replace('''            bool exhaustsAsSkill = skillsExhaust && cardType == CardType.Skill;
            bool? hasExhaustKeyword = needsExhaustCount && !exhaustsAsSkill
                ? HasKeyword(predicted, predictionState, CardKeyword.Exhaust) : null;
            bool exhaustsOnPlay = exhaustsAsSkill || hasExhaustKeyword == true;''','''            bool exhaustsOnPlay = card.Keywords.Contains(CardKeyword.Exhaust)
                || skillsExhaust && cardType == CardType.Skill;''')
baseline=baseline.replace('''                    hasExhaustKeyword ??= HasKeyword(predicted, predictionState, CardKeyword.Exhaust);
                    if (!hasExhaustKeyword.Value) reusableShivCount++;''','''                    if (!card.Keywords.Contains(CardKeyword.Exhaust)) reusableShivCount++;''')
baseline=baseline.replace('''                    if (generated > 0 && (cardType == CardType.Power
                        || skillsExhaust && cardType == CardType.Skill
                        || (hasExhaustKeyword ??= HasKeyword(predicted, predictionState, CardKeyword.Exhaust)) == true))''','''                    if (generated > 0 && (cardType == CardType.Power || exhaustsOnPlay))''')
if 'hasExhaustKeyword' in baseline or baseline.count('bool exhaustsOnPlay = card.Keywords.Contains')!=1:
 raise RuntimeError('Update the baseline transformation for the changed production algorithm')
(out/'Baseline.cs').write_text(using+'internal static class Baseline {\n'+baseline+'\n}', encoding='utf-8')
for name in ['Program.cs','Stubs.cs']:(out/name).write_bytes((repo/'tools/StrategicKeywordChecks'/name).read_bytes())
(out/'Checks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Production.cs" /><Compile Include="Baseline.cs" /><Compile Include="Program.cs" /><Compile Include="Stubs.cs" />'''+''.join('<Compile Include="'+escape(str(repo/p))+'" />' for p in ['src/Strategy/CardMechanismFacts.cs','src/Search/SolverWeights.cs'])+'''</ItemGroup></Project>''', encoding='utf-8')
subprocess.run(['dotnet','run','--project',str(out/'Checks.csproj'),'-c','Release'],cwd=repo,check=True)
