# SearchInteractionChecks

`dotnet run --project source/tools/SearchInteractionChecks -c Release`

Game-free .NET 9 contracts link the production `SearchInteractionState.cs` and
`SolverProgress.cs`, including candidate pairing, display pacing and takeover
materialization. Foreign search/game DTOs are minimal stubs; the interaction
workflow is not replaced by a test implementation.

Covers approval without execution, version/action mismatch rejection, the rendered
selection, background publication after adoption, scope finalization, TurnSetup's
existing seedless path, one-time lazy materialization outside the lock, action-list
ownership, invalidation/reset and stopped-result stamps.
The live progress slot and pacing object stay private to the module. Clearing live
progress does not reset approval, rendered selection or pending takeover. Seeded
cancellation uses the same scope/materialization path without an ordinary result;
seedless complete-result quality fallback remains with Search.

These contracts do not prove real search ranking, actual PlanAction nested-choice
semantics or Host/Client execution. Keep `U0U1PinnedHarness completion-scope` and
`continuation-replay` for pinned integration, and the existing live acceptance
requirements for multiplayer deployment.
