# 0.107.1 full battle lifecycle smoke

`COMPAT1071_FULL_BATTLE` passed in the Windows headless game process after the
Batch 5 source changes:

`setup_turn=2; selected=True; deployed=True; next_turn=2; route_reuse=False; combat_in_progress=false; cleanup=search,deployment,turn_setup,gc; lifecycle=2>1; gc_ends=1; gc_losses=0`

The fixture lowers every live enemy to 1 HP and restores the player to full
HP only to make the lifecycle deterministic. It still executes the native
turn-setup choice, the solver's full-auto deployment, and the game's actual
combat-ending action. After `CombatEnded`, the smoke waits for the reference
release barrier and asserts:

- no active search or deployment;
- no full-auto or automatic-search pause state;
- no active turn-setup coordinator/search;
- no active background reclaim or No-GC region.

The outer unattended launcher reports its known
“Game exited without writing a result” wrapper error because this dedicated
compatibility mode writes its own result file instead of the normal unattended
protocol result. That wrapper status is not counted as a regular unattended
pass.
