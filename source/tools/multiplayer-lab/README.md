# Multiplayer Phase 0 lab

validate-phase0-results.ps1 is a read-only evidence checker. It does not start
the game, inspect a live session, modify the capability table, or enable
Multiplayer Advisor. A PASS result requires both:

- a Host/Client matrix whose every required check is PASS and has an evidence
  reference;
- at least one valid MultiplayerClientProbe JSONL record with the read-only
  contract intact.

The matrix must be produced from a real Vanilla Host + RitsuLib/CombatSolver
Client run. Synthetic, fake-multiplayer, or single-process observations must
remain UNVERIFIED.

## Matrix shape

Use one JSON file with schemaVersion: 1 and a checks object. Required check
names are:

~~~text
vanillaHostAcceptedClient
hostUnaware
noCustomNetworkPackets
localPlayerIdentity
localHand
localDrawPile
drawAfterDraw
shuffleOrder
localDiscardExhaust
enemyStateSync
multiplayerScaling
remoteWorldDelta
probeReadOnly
singleplayerRegression
localEndTurnSync
localPlayCardSync
fastActionStress
wireModelCompatibility
~~~

Each check needs a status and a path, run id, or log location that an operator
can inspect:

~~~json
{
  "schemaVersion": 1,
  "checks": {
    "vanillaHostAcceptedClient": {
      "status": "PASS",
      "evidence": "phase0/host-client-run-01/lobby.txt"
    }
  }
}
~~~

The example is intentionally incomplete and therefore cannot pass.

## Usage

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-phase0-results.ps1 -MatrixPath .\phase0-matrix.json -ProbePath C:\Users\<user>\Desktop\CombatSolver-BugReports\logs\CombatSolver\multiplayer-probe-<pid>-<run>.jsonl
~~~

Exit codes are 0 for PASS, 1 for a contradiction or invalid Probe record, and
2 for missing or still-unverified evidence. The checker never changes the
runtime gate; source/docs/multiplayer/README.md remains the authoritative phase
boundary.
