# Post-MP1 fixed-work single-player comparison

- Time: 2026-09-20 (Asia/Shanghai)
- Game / RitsuLib: `0.107.1` / `0.107.1`
- Source commit: `a6ab8ffa289bb37034d7625ad4e785c93257c522` (`docs: record host recreate lifecycle evidence`)
- Compatibility build: `dotnet build source/CombatSolver.csproj -c Release -p:CompatibilitySmoke=true -p:CopyModOnBuild=false --no-restore --verbosity minimal`
- Compatibility DLL SHA-256: `864EC2A8276845B0C412106259D75AE2D30333826D415BE03A7371FD3A3B474F`
- Fixture: `COMPAT1071_PERFORMANCE_BASELINE`, IRONCLAD, `NIBBITS_NORMAL`, run seed `COMPAT1071`
- Search settings: effective Medium, beam `60`, DOP `1`, fixed budget `5000 ms`, Smart potion policy, Beam Width portfolio enabled, Novelty portfolio disabled
- Samples: `post-mp1-current-01`, `post-mp1-current-02`, `post-mp1-current-03`; each used a separate owned Windows headless instance

## Result

All three current samples produced `expanded=3528`, `transitions=10156`, `boundary=None`, and the same route/result identity as the historical Batch 7 baseline. All had `gen2=0`, `framesOver50Ms=0`, and `framesOver100Ms=0`.

| group | samples | average elapsed | average allocated | average bytes/transition |
|---|---:|---:|---:|---:|
| historical Batch 7 baseline | 3 | 3169.203 ms | 370,614,600 B | 36492.182 |
| current post-MP1 | 3 | 2961.759 ms | 373,990,581 B | 36824.594 |

Against the historical baseline, the current spot sample is `-6.546%` elapsed and `+0.911%` allocated. The samples were not interleaved with a rebuilt pre-MP1 binary, so this is a fixed-work no-regression spot comparison, not a stable speedup claim. The route/work/result identity match is the acceptance signal.

The outer unattended launcher reports the expected `Game exited without writing a result ... exit_code=0` for this dedicated compatibility mode; the three JSON files here are the authoritative smoke outputs. No account data, tokens, cookies, saves, crash dumps, or game installation files are included.
