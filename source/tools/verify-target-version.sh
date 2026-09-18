#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"
target_path="$repository_root/build-target.json"

command -v jq >/dev/null 2>&1 || { echo 'verify-target-version.sh: jq is required' >&2; exit 2; }
command -v rg >/dev/null 2>&1 || { echo 'verify-target-version.sh: rg is required' >&2; exit 2; }

game_version="$(jq -er '.game_version' "$target_path")"
ritsu_version="$(jq -er '.ritsu_lib_target_version' "$target_path")"
symbol="$(jq -er '.compatibility_symbol' "$target_path")"
manifest_version="$(jq -er '.min_game_version' "$repository_root/CombatSolver.json")"

[[ "$game_version" == "$ritsu_version" ]] || { echo 'game and RitsuLib target versions differ' >&2; exit 1; }
[[ "$manifest_version" == "$game_version" ]] || { echo 'manifest target differs from build-target.json' >&2; exit 1; }
[[ "$symbol" =~ ^STS2_[0-9]+$ ]] || { echo 'compatibility_symbol has an unexpected form' >&2; exit 1; }

active_files=(
    CombatSolver.csproj
    Directory.Build.props
    Directory.Build.targets
    local.props.example
    README.md
    tools/OfflineSearchHarness/OfflineSearchHarness.csproj
    tools/CoverageCatalog/CoverageCatalog.csproj
    tools/CoverageCatalog/Program.cs
    tools/AdaptedOnPlayChecks/AdaptedOnPlayChecks.csproj
    tools/headless-runtime.ps1
    tools/run-unattended-test.ps1
    tools/run-unattended-test.sh
)
for relative_path in "${active_files[@]}"; do
    file_path="$repository_root/$relative_path"
    [[ -f "$file_path" ]] || { echo "missing active target file: $relative_path" >&2; exit 1; }
    if rg -n --fixed-strings '0.111.0' "$file_path" >/dev/null; then
        echo "stale 0.111.0 reference in active target file: $relative_path" >&2
        exit 1
    fi
done

rg -n "<CombatSolverTargetGameVersion[^>]*>$game_version</CombatSolverTargetGameVersion>" "$repository_root/Directory.Build.props" >/dev/null
rg -n "<CombatSolverTargetRitsuLibVersion[^>]*>$ritsu_version</CombatSolverTargetRitsuLibVersion>" "$repository_root/Directory.Build.props" >/dev/null
rg -n "<CombatSolverCompatibilityConstant[^>]*>$symbol</CombatSolverCompatibilityConstant>" "$repository_root/Directory.Build.props" >/dev/null

printf 'TARGET_VERSION_PASS game=%s ritsu=%s symbol=%s\n' "$game_version" "$ritsu_version" "$symbol"
