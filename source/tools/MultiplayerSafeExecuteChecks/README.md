# Multiplayer Safe Execute checks

Run `dotnet run --project tools/MultiplayerSafeExecuteChecks/MultiplayerSafeExecuteChecks.csproj -c Release` from `source`.

This is a pure L1 contract project for the dormant MP-2A boundary. It pins the fail-closed
reasons for non-card actions, missing/local ownership, replay/choice/end-turn semantics,
remote or unknown targets, and the one-action-per-deployment cap.

Passing this project does not enable a production multiplayer execution mode. It also
pins the Lab-only gate: the exact `safe-execute-lab` token requires Probe evidence and
an owned ClientCombatSolver instance, while a production-style `safe-execute` token is
rejected. Native action ownership, WorldVersion invalidation, and Host/Client behavior
still require controlled Multiplayer Lab evidence.
