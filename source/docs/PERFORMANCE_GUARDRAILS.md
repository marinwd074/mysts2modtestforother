# Performance and quality guardrails

These rules apply to compatibility work and future refactors. Functional
correctness and route quality take priority over a smaller elapsed-time number.

## Required comparison

Compare the same source/build, game and RitsuLib versions, encounter, seed,
run state, search budget, Beam settings, DOP, NoGC setting, and OS class. Use
at least three clean-process samples in ABBA order and report the median. Keep
the route/result fingerprint and exact expanded-node/transition counts beside
the timing.

Record search milliseconds, expanded nodes, transitions, allocations, Gen2
collections, GC pause, P95/P99/max frame gap, and peak working set where the
runner can measure them. A change is not a performance win if it changes the
route, action sequence, RNG, or required search work.

## Regression threshold

For a quality-preserving change, the primary metric must not regress by more
than 2% against the fixed-work baseline. A result outside that threshold is a
regression or an explicitly documented measurement uncertainty; it is not
rounded away. This repository currently documents the rule but does not yet
enforce it as an automated CI gate.

## Prohibited shortcuts

- Do not lower the search budget, Beam width, node limit, or DOP to make a
  smoke pass.
- Do not disable or alter GC/NoGC behavior only for the candidate run.
- Do not replace a real native choice with a synthetic shortcut while claiming
  native compatibility.
- Do not accept a route/result mismatch because elapsed time improved.
- Do not use a single noisy run as a baseline or infer visible-Steam results
  from headless timing.

The 0.107.1 compatibility smokes are functional records only and therefore do
not establish a performance baseline.
