# 0.107.1 turn-setup launch failure evidence

- Timestamp: 2026-09-18 14:22:30 Asia/Shanghai
- Game target: Slay the Spire 2 `0.107.1`
- Source commit used: `7a73732`
- Mode: `COMPAT1071_TURN_SETUP`
- Context: isolated headless instance `compat-turn-20260918`

The launcher was admitted, but snapshot creation stopped before the game
started because the C: volume did not have enough free space for the frozen
game image. No game result was produced and this run is not a pass. The same
mode was then rerun with an explicitly configured D: headless root; its passing
evidence is in `20260918-compatibility-turn-setup-d`.
