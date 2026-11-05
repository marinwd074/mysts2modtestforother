# PreCombatWorkerSessionChecks

Run from the repository root:

```bash
dotnet run --project source/tools/PreCombatWorkerSessionChecks -c Release
```

Links the production outer worker, session module, public contracts and protocol DTOs/JSON
options. Foreign game/Mod environment, snapshot, logging and public-adapter defaults use minimal stubs; the outer worker/session/protocol behavior is production source. The actual public completion adapter is covered separately by PreCombatRequestChecks.
Tests use real temporary protocol files, a narrow controlled owned-process
adapter, and two real cloud/local test children to verify exact process ownership.
No game, Steam, network service or user profile is started. Outer-worker seam checks inject the exact controlled session and exercise Stop, Restart, new-request admission, cached close, idle stop and process-exit cleanup.

Covers request/result/ready/cleanup ordering, stale IDs, held ready, cancellation,
malformed output, process death, stop timeout/failure, retained ownership,
responsive status during exit confirmation and idempotent handle release.
`PreCombatRequestChecks` additionally verifies that an unconfirmed exit cannot be
hidden by cancellation or live-state drift at the public completion adapter.

These contracts do not prove the real Windows game worker boot/mirroring or
multiplayer behavior. Keep the existing pinned/runtime validation requirements.
