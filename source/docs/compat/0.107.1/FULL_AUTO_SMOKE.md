# 0.107.1 full-auto smoke

`COMPAT1071_FULLAUTO` passed at source commit `7a73732` under the production
default search budget:

`setup_turn=2; selected=True; deployed=True; next_turn=3; route_reuse=True; combat_in_progress=True`

The smoke confirms automatic takeover of the native turn-setup choice,
deployment, continuation into turn 3, and route reuse. It deliberately stops
after route reuse is observed; it does not claim a complete battle victory or
a visible Steam layout result.

Historical raw runtime evidence is available from Git history; this file retains the durable verification conclusion.

The complete battle lifecycle is covered separately by
[`COMPAT1071_FULL_BATTLE`](FULL_BATTLE_SMOKE.md).
