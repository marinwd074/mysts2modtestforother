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

The full STS2 build is intentionally not claimed by GitHub CI because it
requires the locally installed game and RitsuLib assemblies. CI runs the
target consistency guard and project XML checks only; the local build command
remains:

```powershell
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false --nologo
```

