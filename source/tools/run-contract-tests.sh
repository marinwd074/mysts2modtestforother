#!/usr/bin/env bash
set -uo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pass_count=0
fail_count=0
skip_count=0

run_dotnet_contract() {
    local name="$1"
    local project="$2"
    printf 'RUN: %s\n' "$name"
    if (cd "$repository_root" && dotnet run --project "$project" -c Release); then
        pass_count=$((pass_count + 1))
    else
        fail_count=$((fail_count + 1))
    fi
}

run_dotnet_contract 'StateFingerprintChecks' 'tools/StateFingerprintChecks/StateFingerprintChecks.csproj'
run_dotnet_contract 'CardHookReceiverChecks' 'tools/CardHookReceiverChecks/CardHookReceiverChecks.csproj'
run_dotnet_contract 'TurnPhaseMirrorChecks' 'tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj'
run_dotnet_contract 'PredictionStateStoreChecks' 'tools/PredictionStateStoreChecks/PredictionStateStoreChecks.csproj'

python_command=''
if command -v python3 >/dev/null 2>&1; then
    python_command='python3'
elif command -v python >/dev/null 2>&1; then
    python_command='python'
fi

if [[ -z "$python_command" ]]; then
    skip_count=$((skip_count + 1))
    printf 'SKIP: BeamRankSortChecks (Python is unavailable)\n'
else
    printf 'RUN: BeamRankSortChecks\n'
    if (cd "$repository_root" && "$python_command" tools/BeamRankSortChecks/run.py); then
        pass_count=$((pass_count + 1))
    else
        fail_count=$((fail_count + 1))
    fi
fi

printf 'PASS: %d FAIL: %d SKIP: %d\n' "$pass_count" "$fail_count" "$skip_count"
if ((fail_count > 0)); then
    exit 1
fi
