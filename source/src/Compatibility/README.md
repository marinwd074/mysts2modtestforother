# Compatibility boundary

`source/` is the canonical tracked source tree. The target facts live in
`source/build-target.json` and are checked by `source/tools/verify-target-version.ps1`.

Version-specific native API shape belongs here when it can be isolated without
adding a per-node abstraction. The migrated boundaries are
`Sts2TurnSetupCompatibility`, which owns the 0.107.1-versus-legacy reflection
signatures and invocation argument arrays used by the turn-setup runtime patch,
`Sts2CardHookCompatibility`, which owns the card-result hook names, reflection
signatures, and result-location conversion, and `Sts2HookCompatibility`, which
owns the version-specific `AfterBlockBroken` parameter list used by its mirror.
The Harmony prefix signatures remain conditional in `Runtime/PlayerTurnSetupPatches.cs`
because Harmony must compile against the native parameter shape for the selected
game version.

Migration order for later boundaries:

1. Hook signatures and lifecycle.
2. Card, potion, and result-pile APIs.
3. Power lifecycle and RNG access.
4. Native choice and state-snapshot APIs.

Compatibility helpers must stay static, non-reflective on search-node paths, and
allocation-neutral relative to the code they replace. Do not introduce an
interface or service object solely to reduce a preprocessor count. `SearchGcPolicy`
remains intentionally unchanged until a fixed-work benchmark proves a safe
replacement.

To audit the remaining boundary, use:

```powershell
rg -n "#if (!)?STS2_01071" source/src
```
