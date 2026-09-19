# Multiplayer root capture checks

Run `dotnet run --project tools/MultiplayerRootCaptureChecks/MultiplayerRootCaptureChecks.csproj -c Release` from `source`.

The check keeps the remote public relic allow-list exact: the audited vanilla
`BurningBlood` type is eligible for omission from a local-player-only Advisor root,
while unknown relic/model types remain unsupported and therefore fail closed in the
runtime root contract. It also covers the captured-player EndTurn boundary and the
native enemy/powered-block early-exit used by multiplayer block scaling.
