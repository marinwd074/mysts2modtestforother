# Multiplayer Safe Execute checks

Run `dotnet run --project tools/MultiplayerSafeExecuteChecks/MultiplayerSafeExecuteChecks.csproj -c Release` from `source`.

This is a pure L1 contract project for the dormant MP-2A boundary. It pins the fail-closed
reasons for non-card actions, missing/local ownership, replay/choice/end-turn semantics,
remote or unknown targets, and the one-action-per-deployment cap.

Passing this project does not enable multiplayer execution. Native action ownership,
network behavior, WorldVersion invalidation, and Host/Client behavior still require
controlled Multiplayer Lab evidence before the capability gate may be made reachable.
