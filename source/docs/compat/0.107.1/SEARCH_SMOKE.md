# 0.107.1 search smoke

The first-turn smoke passed at source commit `bdbf51c`:

- native host `0.107.1` and RitsuLib `0.6.2` loaded;
- CombatSolver's 63 patches applied with no patch failures;
- first-turn search produced a card-play route with 20 actions;
- incremental verification was enabled;
- live and predicted RNG stream counters matched.

Historical raw runtime evidence is available from Git history; this file retains the durable verification conclusion.

The outer launcher status in that directory is `launcher_failed` because this
special smoke writes its own result and intentionally quits the game. The
smoke result, not the absent normal request protocol result, is the assertion
under test and the distinction is recorded in its metadata.

