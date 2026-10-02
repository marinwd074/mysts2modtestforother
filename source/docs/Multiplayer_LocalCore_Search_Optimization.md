# 多人本地核心搜索优化：已实现说明

> 本文件原为 P0–P4 任务书。P0–P4 已关闭，现在只作为**实现地图**。
>
> 当前任务与未完成风险只看 [CODEX_HANDOFF.md](CODEX_HANDOFF.md)。本文不再保存“下一阶段怎么做”的指令。

## 1. 最终结构

默认多人使用 `SearchRoutePolicy.MultiplayerSinglePlayerCore`：共享单人 Beam/Novelty/模拟主核，同时保留多人真实状态、WorldVersion 和仅本地玩家可部署的权限边界。

入口：

- [SolverController.cs](../src/Runtime/SolverController.cs)：根据 session capability 和 `UseMultiplayerPrediction` 选择 `MultiplayerSinglePlayerCore`。
- [MultiplayerLocalCrossTurnContracts.cs](../src/Search/MultiplayerLocalCrossTurnContracts.cs)：定义哪些 route policy 具有本地跨回合投影/复用语义。
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)：协调 replay、Beam portfolio、Smart Potion 和前台结果。
- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs)：只执行本地玩家动作。

主链：

```text
稳定 live root
  → exact continuation
  → bounded refresh / continuation route replay
  → normal local-single-core search
  → early incumbent / foreground
  → final ordering
  → 当前根 SolverResult
  → Safe Execute revalidation
  → native action
```

## 2. P0：候选保留与 bounded refresh

### 已实现内容

最终排序会为本地跨回合模式保留少量值型 replay candidate：

- 最多 3 条；
- 首动作不同；
- 每条只保留当前回合最多 2 个普通 `PlayCard`；
- 不持有 SearchNode/simulator 的长期可变引用。

位置：

- [CombatBeamSolver.FinalPlanOrdering.cs](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs)
  - `FinalPlanOrdering.Select`
  - `replayCandidates` 构造段
- [SolverController.MultiplayerPlanRefresh.cs](../src/Runtime/SolverController.MultiplayerPlanRefresh.cs)
  - `TryBoundedMultiplayerPlanRefresh`
  - `HasSameExecutionIdentity`
  - `DropRetainedPlanForFullRestart`
- [MultiplayerPlanRefreshContracts.cs](../src/Search/MultiplayerPlanRefreshContracts.cs)
  - `IsReplayCompatible`
  - `IsSoftLivingEnemyDurabilityDelta`

### 调用链

```text
FinalPlanOrdering.Select
  → 保存 MultiplayerReplayCandidates
  → Runtime 观察 WorldVersion 变化
  → TryBoundedMultiplayerPlanRefresh
  → MultiplayerPlanRefreshContracts.IsReplayCompatible
  → CombatBeamSolver.ReplayDiagnosticPrefix(candidate.Prefix)
  → 比较 Continue / Reselect / FullRestart
  → 新根固定前缀小搜索重新物化
  → 新 SolverResult
```

bounded refresh 的局部上限仍在 `SolverController.MultiplayerPlanRefresh.cs`：

- Beam 24
- 192 expanded nodes
- 60 ms 小搜索限制

这些限制只属于局部重新物化，不是整个请求的总墙钟。

### 作用

队友只让存活敌人 HP/Block 轻量变化时，可以先验证已有候选；目标死亡、本地资源、牌堆、Power、RNG 或动作身份变化仍走 `FullRestart`。

## 3. P1：跨回合建议重放

精确 continuation 仍是第一优先级。精确失败后，Runtime 可以把旧路线中当前回合的值型动作冻结为 seed，再让 Search 从新根真实 replay。

位置：

- [SolverController.SearchLifecycle.cs](../src/Runtime/SolverController.SearchLifecycle.cs)
  - `RequestSearch`
  - `CaptureContinuationSeedActions`
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)
  - `SolveRequest`
  - `TryReplayContinuationRoute`
- [CombatBeamSolver.R1TransitionHydration.cs](../src/Search/CombatBeamSolver.R1TransitionHydration.cs)
- [CombatBeamSolver.PathDiagnostics.cs](../src/Search/CombatBeamSolver.PathDiagnostics.cs) `ReplayDiagnosticPrefix`

调用链：

```text
旧 continuation 不精确
  → CaptureContinuationSeedActions
  → SearchPolicySnapshot.ContinuationSeedActions
  → 新根 replay
      ├─ 全部合法：完整 seed
      ├─ 中途失效：只保留已验证前缀
      └─ 第一动作失效/不支持：无 seed
  → 正常搜索继续
  → 当前根正式排序
```

作用：旧路线只提供探索起点，不继承旧胜利、旧战损或旧 RNG 结论。RNG 改变可以重新尝试动作，但牌不在手、目标死亡、Choice/身份无法确认时停止。

## 4. P2：独立 incumbent 与枚举提示

P2 没有新建第二套 UI 协议，而是复用已有：

- `SolverProgress`
- `SolverInterimResultOrdering`
- `SolverRouteAdoptionSeed`
- Coordinator foreground/materialization

### 已实现行为

1. replay/修复得到的候选可以成为独立 incumbent，不占用主 Beam retention。
2. 同 root/policy epoch 只提升更优的合格结果。
3. 已有合法 continuation seed 可以给 `MultiplayerSinglePlayerCore` 的下一动作枚举提供优先提示。
4. 提示一旦与实际前缀、回合、Choice、目标或卡实例不一致就停止。
5. 候选集合、Beam retention、最终排序、总预算和 Safe Execute 权限没有因此放宽。

关联位置：

- [SolverController.SearchLifecycle.cs](../src/Runtime/SolverController.SearchLifecycle.cs)：冻结 seed、请求/结果 epoch 所有权。
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)：progress、foreground、takeover 和最终收口。
- [SolverProgress.cs](../src/Runtime/SolverProgress.cs)：`SearchInteractionState`、route adoption/current-turn adoption 的值型 materialization。
- `CombatBeamSolver` 的动作枚举路径读取 continuation seed hint；最终合法性仍由正常模拟决定。

固定输入 P2 对照曾在相同最终路线、质量和 7536 nodes 下，把 `time_to_reference_quality` 的三组样本从约 6.2–7.7s 提前到约 2.1–3.7s。该数字只说明该固定输入的“更早得到参考质量”，不等同于普遍实机倍率。

## 5. P3：Smart 跨家族固定轮转

P3 接入默认 `MultiplayerSinglePlayerCore` 的窄边界：

- Smart Potion；
- 非 TurnSetup；
- 无强制药水指令；
- Novelty 关闭；
- 默认 Beam portfolio 的首个 baseline Beam 与“恰好 1 药”Beam 可恢复轮转。

位置主要在 [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)：

- `RunBeamWidthPortfolioPass`
- `SearchEarlySmartPotionScout`
- `TryReuseEarlySmartPotionScout`
- `RunSupplementalAudits`
- `AuditSmartPotionUse`
- `SearchSmartPotionGradientScheduled`
- `SearchSmartPotionGradient`

底层可恢复成员由 `CombatBeamSolver` 的 resumable search session/phase 机制提供。

调用关系：

```text
SolveRequest
  → primary Beam portfolio
  ↔ early one-potion scout（固定轮转）
  → primary incumbent
  → supplemental Smart Potion audit
  → 正式无药基线/药水资格比较
  → final result
```

作用：药水家族不再必须等主 Beam 很久以后才第一次做真实工作；早期 scout 仍是隐藏候选，最终是否采用药水继续走原有正式质量比较。

## 6. P4：严格等价热点优化

P4 只合入固定工作 A/B 能证明**路线、质量和逻辑工作不变**的优化。

### 6.1 History 计数快路径

原 fingerprint 会反复扫描 prediction history。现在本地主行动玩家可读取 `CombatPredictionHistory` 的增量计数；无法证明 owner/计数适用时回退原扫描。

位置：

- [CombatPredictionHistory.cs](../src/Engine/InCombat/Simulation/CombatPredictionHistory.cs)
- [CombatHistoryCounters.cs](../src/Engine/InCombat/Simulation/CombatHistoryCounters.cs) `After/Scan`
- [SimulatedCombatState.CardEventHistory.cs](../src/Search/SimulatedCombatState.CardEventHistory.cs)
- [SimulatedCombatState.cs](../src/Search/SimulatedCombatState.cs) `AppendFingerprint`
- [CombatBeamSolver.StateEvaluation.cs](../src/Search/CombatBeamSolver.StateEvaluation.cs) `Snapshot/BuildStateKey`

作用：减少每个 Snapshot/fingerprint 的重复 history 扫描，不删除指纹字段，也不做近似键。

### 6.2 单本地玩家 Relic/Potion fingerprint 快路径

默认 local-core 虽然 live roster 有多名玩家，但搜索根只捕获本地可读玩家。单元素集合不再执行无意义排序。

位置：

- [SimulatedCombatState.Potions.cs](../src/Search/SimulatedCombatState.Potions.cs)：`_rootCapturedPlayers.Count == 1` 快路径及玩家 potion fingerprint。
- Relic/potion fingerprint 其它路径仍对多捕获玩家保留稳定排序。

### 6.3 Snapshot 分配削减

Snapshot 热路径把可证明等序的 LINQ `Count/Any/Where+Sum` 改为下标循环，并复用一次取得的 `EffectivePowers()` 只读视图。

位置：

- [CombatBeamSolver.StateEvaluation.cs](../src/Search/CombatBeamSolver.StateEvaluation.cs) `Snapshot`
- [SimulatedCombatState.cs](../src/Search/SimulatedCombatState.cs) `EffectivePowers`
- 相关 Prediction power support 使用相同只读视图语义。

作用：减少临时枚举器/数组和重复 power collection 读取，不改变评分字段或短路语义。

## 7. request-local 普通转移 hydration

P0–P4 之后又实现了 request-local 普通转移 hydration 实验。它不是 P1 的跨根 route seed。

位置：

- [CombatBeamSolver.RequestTransitionHydration.cs](../src/Search/CombatBeamSolver.RequestTransitionHydration.cs)
  - `TryRequestHydrationKey`
  - `TryReadRequestTransitionHydration`
  - `ObserveRequestTransitionHydration`
- Coordinator 在 [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) `Solve` 创建/释放 request cache。

这套缓存只在同请求、严格 parent/action/path/策略上下文首验后复用普通 PlayCard 后态。固定输入中虽然出现命中，但构键、完整校验、Fork 和重新 Snapshot 成本抵消收益，因此生产默认仍关闭。当前状态以 handoff 为准。

## 8. 与执行层的边界

搜索优化只决定“更早得到什么 SolverResult”，不直接放宽执行。

```text
SolverResult
  → SolverController.StartDeployment / DeployCurrentTurn
  → MultiplayerSafeExecutePolicy.Classify*
  → native action enqueue
  → queue/Choice settle
  → MultiplayerSafeExecutePolicy.RevalidateAction
  → Continue / Replan / Abort
```

位置：

- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs)
  - `StartDeployment`
  - `DeployCurrentTurn`
  - `BuildSafeActionRevalidationFacts`
  - `EnqueueAndCaptureActionAsync`
- [MultiplayerSafeExecutePolicy.cs](../src/Runtime/MultiplayerSafeExecutePolicy.cs)
  - `ClassifyStructural`
  - `ClassifyResolved`
  - `RevalidateAction`
  - `ValidateSafeEndTurn`

因此 P0–P4 不会把队友动作变成本地可执行动作，也不会让旧根结果绕过 WorldVersion/后态校验。

## 9. P0–P4 的用途总结

- P0：轻量变化时先重放少量候选，减少全重搜。
- P1：exact continuation 失败后仍能把旧动作当新根 seed。
- P2：让合法好路线更早成为 incumbent/可采用结果。
- P3：减少 Smart Potion 家族等待主 Beam 的时间。
- P4：降低 fingerprint/Snapshot 的严格等价 CPU 和分配成本。

P0–P4 已是当前代码的一部分，不再作为阶段任务推进。新性能工作只在 [CODEX_HANDOFF.md](CODEX_HANDOFF.md) 记录。
