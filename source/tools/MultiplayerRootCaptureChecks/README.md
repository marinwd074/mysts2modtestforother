# Multiplayer root capture checks

Run `dotnet run --project tools/MultiplayerRootCaptureChecks/MultiplayerRootCaptureChecks.csproj -c Release` from `source`.

This is a pure L1 contract project: it links the production multiplayer boundary predicates
and supplies only the minimal native type shapes required by those predicates. It therefore
runs on CI without a local Slay the Spire 2 installation. Native lifecycle and real Host/Client
behavior remain Multiplayer Lab responsibilities.

The check keeps the remote public relic allow-list exact: the audited vanilla
`BurningBlood` type is eligible for omission from a local-player-only Advisor root,
while unknown relic/model types remain unsupported and therefore fail closed in the
runtime root contract. It also covers the captured-player EndTurn boundary and the
enemy/powered-block early-exit used by multiplayer block scaling.
