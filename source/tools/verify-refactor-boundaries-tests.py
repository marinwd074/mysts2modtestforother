#!/usr/bin/env python3
"""Exercise the five aligned ownership guards against an isolated source copy.

No game, SDK, third-party Python packages, or PowerShell runtime is required.
PowerShell equivalence here covers these five contracts only, not its full gate.
"""
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def powershell_rules(text):
    rules = set()
    for record in re.findall(r"@\{([^{}]*)\}", text, re.S):
        value = re.search(r"Text\s*=\s*(['\"])(.*?)\1", record, re.S)
        if not value:
            continue
        path = re.search(r"(?:RelativePath|File)\s*=\s*['\"]([^'\"]+)['\"]", record)
        if path:
            relative = path[1]
        else:
            path = re.search(r"Path\s*=\s*\(?Join-Path\s+\$(repositoryRoot|searchRoot)\s+['\"]([^'\"]+)['\"]", record)
            if not path:
                continue
            relative = ("src/Search/" if path[1] == "searchRoot" else "") + path[2]
        rules.add((relative.replace("\\", "/"), value[2]))
    return rules


def check_parity(source):
    shell = (source / "tools/verify-refactor-boundaries.sh").read_text()
    powershell = (source / "tools/verify-refactor-boundaries.ps1").read_text()
    shell_inventory = re.search(r"^expected_beam_files=\((.*?)^\)", shell, re.M | re.S)
    ps_inventory = re.search(r"^\$expectedBeamFiles = @\((.*?)^\)", powershell, re.M | re.S)
    require(shell_inventory is not None and ps_inventory is not None, "Missing explicit partial inventory")
    names = r"CombatBeamSolver(?:\.[A-Za-z0-9]+)*\.cs"
    shell_names = re.findall(names, shell_inventory[1])
    ps_names = re.findall(names, ps_inventory[1])
    require(len(shell_names) == len(set(shell_names)), "Duplicate Bash partial inventory entry")
    require(len(ps_names) == len(set(ps_names)), "Duplicate PowerShell partial inventory entry")
    require(set(shell_names) == set(ps_names), "Bash/PowerShell partial inventories differ")
    required = {
        ("src/Runtime/ContinuationStamp.cs", "ModelPredictionStateMirrors.AppendLiveContinuation(text, state, capturedPlayers)"),
        ("src/Search/CombatBeamSolver.BeamRetentionPolicy.cs", "HasRetainedRoutingChoice: RetainedRoutingChoice(node) != null"),
        ("src/Search/CombatBeamSolver.BeamRanking.cs", "if (values.HasRetainedRoutingChoice)"),
        ("src/Search/CombatBeamSolver.OpeningExpansion.cs", "ApplyFixedPrefix(seed, prefix)"),
        ("src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs", "PrepareExecutionCardPlay(source.Card, source.Play, context);"),
        ("src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs", "CardPlay play = context.RequireRemap(source.Play);"),
    }
    shell_rules = set()
    for line in shell.splitlines():
        if line.startswith("src/") and "\t" in line:
            shell_rules.add(tuple(line.split("\t", 1)))
    for match in re.finditer(r'require_fixed\s+"\$search_root/([^\"]+)"\s+\'([^\']+)\'', shell):
        shell_rules.add(("src/Search/" + match[1], match[2]))
    require(required <= shell_rules, "Bash lost an aligned ownership rule")
    require(required <= powershell_rules(powershell), "PowerShell lost an aligned ownership rule")
    return len(shell_names)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, help="Optional compact JSON evidence file")
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[1]
    repo = source.parent
    inventory_count = check_parity(source)
    checks = ["five-contract Bash/PowerShell static parity"]
    # Copy versioned and newly added source files, not ignored binaries, caches, or game refs.
    files = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "source"], cwd=repo
    ).decode().split("\0")
    with tempfile.TemporaryDirectory(prefix="combatsolver-boundary-tests-") as temporary:
        fixture = Path(temporary)
        for relative in set(filter(None, files)):
            original = repo / relative
            if original.is_file():
                target = fixture / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(original, target)
        script = fixture / "source/tools/verify-refactor-boundaries.sh"

        def run(name, diagnostic=None):
            result = subprocess.run(["bash", str(script)], cwd=fixture, capture_output=True, text=True, timeout=90)
            output = result.stdout + result.stderr
            if diagnostic is None:
                require(result.returncode == 0 and "REFACTOR_BOUNDARIES_OK" in output, name + " failed:\n" + output)
            else:
                require(result.returncode == 1 and diagnostic in output
                        and "Refactor boundary verification failed" in output,
                        name + " did not reject the intended violation:\n" + output)
            checks.append(name)
            print("PASS " + name, flush=True)

        def mutate(name, relative, original, replacement, diagnostic):
            path = fixture / "source" / relative
            data = path.read_text()
            require(data.count(original) == 1, name + " mutation must target one exact production occurrence")
            try:
                path.write_text(data.replace(original, replacement))
                run(name, diagnostic)
            finally:
                path.write_text(data)

        run("current production snapshot accepted")
        mutate("captured player scope cannot disappear", "src/Runtime/ContinuationStamp.cs",
               "ModelPredictionStateMirrors.AppendLiveContinuation(text, state, capturedPlayers)",
               "ModelPredictionStateMirrors.AppendLiveContinuation(text, state)", "missing Fork boundary")
        rogue = fixture / "source/src/Search/CombatBeamSolver.Unreviewed.cs"
        try:
            rogue.write_text("namespace CombatSolver; internal sealed partial class CombatBeamSolver { }\n")
            run("unreviewed partial rejected", "CombatBeamSolver partial file set differs")
        finally:
            rogue.unlink()
        missing = fixture / "source/src/Search/CombatBeamSolver.SearchEfficiencyDiagnostics.cs"
        saved = missing.read_bytes()
        try:
            missing.unlink()
            run("missing reviewed partial rejected", "CombatBeamSolver partial file set differs")
        finally:
            missing.write_bytes(saved)
        mutate("routing fact construction cannot disappear", "src/Search/CombatBeamSolver.BeamRetentionPolicy.cs",
               "HasRetainedRoutingChoice: RetainedRoutingChoice(node) != null", "HasRetainedRoutingChoice: false",
               "ordinary tactical ties must use the existing retained routing semantics")
        mutate("routing positions cannot lose their guard", "src/Search/CombatBeamSolver.BeamRanking.cs",
               "if (values.HasRetainedRoutingChoice)", "if (false)", "ordinary tactical ties must leave routing positions unchanged")
        mutate("opening prefix cannot bypass its replay", "src/Search/CombatBeamSolver.OpeningExpansion.cs",
               "ApplyFixedPrefix(seed, prefix)", "seed", "missing relic policy ownership")
        card = "src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs"
        mutate("manual fork cannot skip shared preparation", card,
               "PrepareExecutionCardPlay(source.Card, source.Play, context);", "/* preparation removed */",
               "missing card continuation ownership boundary")
        mutate("manual fork cannot retain the parent play", card,
               "CardPlay play = context.RequireRemap(source.Play);", "CardPlay play = source.Play;",
               "missing card continuation ownership boundary")
    evidence = {"status": "PASS", "checks": checks, "count": len(checks), "explicitBeamPartials": inventory_count,
                "powershellEvidence": "static parity for five scoped contracts only; runtime not executed"}
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(evidence, indent=2) + "\n")
    print(f"BOUNDARY_RULE_TESTS_OK checks={len(checks)} explicit_beam_partials={inventory_count}")


if __name__ == "__main__":
    main()
