# 0.107.1 build verification

## Target contract

- Game: `0.107.1`
- RitsuLib target: `0.107.1`; runtime dependency tested at `0.6.2`
- Compatibility symbol: `STS2_01071`
- Machine-readable source of truth: `source/build-target.json`
- Guard: `source/tools/verify-target-version.ps1`

The guard passed during the version-unification stage with
`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`.

## Local build records

The Release build with `CompatibilitySmoke=true` passed at source commit
`7a73732`: 0 errors and 2 pre-existing `CS9113` warnings for unused hook
parameters. The same build produced the DLL used by the turn-setup and
full-auto runtime smokes.

A pinned GitHub Release-build verification is now available at
`.github/workflows/pinned-release-build.yml`. It intentionally does **not**
run on every source push: it runs when the workflow file itself changes or by
manual `workflow_dispatch`.

The first pinned run passed at source commit
`3ba64352d602d043a3edc389d46a0d6ce0a8c718` with **0 warnings / 0 errors**.
That run fetched only the repository-pinned `sts2.dll`, `0Harmony.dll` and
`GodotSharp.dll`, verified the existing 0.107.1 `sts2.dll` SHA-256, then
downloaded the official RitsuLib `0.107.1 / 0.6.2` compatibility archive and
verified its published SHA-256 before building `CombatSolver.csproj -c Release`.

The equivalent local build command remains:

```powershell
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false --nologo
```

