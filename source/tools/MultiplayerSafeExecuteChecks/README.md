# Multiplayer Safe Execute checks

Run `dotnet run --project tools/MultiplayerSafeExecuteChecks/MultiplayerSafeExecuteChecks.csproj -c Release` from `source`.

This is a pure L1 contract project for the MP-2 Safe Execute boundary. It pins the
fail-closed reasons for non-card actions, missing/local ownership, replay/choice/end-turn
semantics, remote or unknown targets, and the one-action-per-deployment cap.

Passing this project does not make Safe Execute the multiplayer default. The explicit
`safe-execute` token opts into the conservative formal capability; the separate
`safe-execute-lab` token remains restricted to Probe evidence and an owned
ClientCombatSolver instance. Native action ownership, WorldVersion invalidation, and
Host/Client behavior still require controlled Multiplayer Lab evidence.
