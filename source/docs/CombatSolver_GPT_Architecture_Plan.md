# CombatSolver：总体架构实现记录

> 本文件曾经同时承担 U0–U6、战前预测和搜索加速任务书。现在改为**实现记录与专项状态索引**。
>
> 不在本文维护“下一步做什么”。当前任务、优先级、未验证边界统一看 [CODEX_HANDOFF.md](CODEX_HANDOFF.md)。

## 1. 当前生产架构

```text
live CombatState
  → SolverController（主线程生命周期/能力/WorldVersion）
  → CombatRootSnapshot（冻结根）
  → CombatSearchCoordinator（请求编排）
  → CombatBeamSolver / Novelty / Smart Potion
  → FinalPlanOrdering
  → SolverResult
  → SolverController.Deployment
  → MultiplayerSafeExecutePolicy
  → 原生动作 / Choice
```

职责：

| 层 | 当前职责 | 主要位置 |
|---|---|---|
| Runtime | live 生命周期、稳定根、请求 epoch、取消、部署授权 | `src/Runtime/SolverController*.cs`、`CombatRootSnapshot.cs` |
| Search | 候选生成、Beam/portfolio、排序、复用、情景复评 | `src/Search/CombatBeamSolver*.cs`、`CombatSearchCoordinator.cs` |
| Engine/Prediction | 可 Fork 模拟、确定性 RNG、卡牌/Power/遗物语义 | `src/Engine`、`src/Prediction` |
| UI | 只读展示和用户交互，不拥有搜索语义 | `src/UI` |

默认多人使用 `MultiplayerSinglePlayerCore`；实验 teammate forecast / Team Objective / Scenario/Robust 由独立开关控制。

## 2. U0：诊断分层已经建立

历史 U0 把问题固定分成四层：

1. 模型/模拟转移；
2. 候选与 Beam 保留；
3. 最终排序/情景复评；
4. Safe Execute / 原生部署。

当前对应诊断入口：

- [CombatBeamSolver.PathDiagnostics.cs](../src/Search/CombatBeamSolver.PathDiagnostics.cs)：路径、重放和 Beam 诊断。
- [CombatBeamSolver.FinalPlanOrdering.cs](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs)：最终候选/质量层。
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)：成员、foreground、Smart Potion、takeover。
- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs)：原生提交/停止原因。
- [MultiplayerSafeExecutePolicy.cs](../src/Runtime/MultiplayerSafeExecutePolicy.cs)：执行 revalidation。

作用：问题包可以先定位“没枚举、Beam 剪掉、Final 选错、Scenario 推翻、还是 Deployment 截断”，避免盲目调权重。

## 3. U1：逐动作 predicted/live 后态校验

搜索路线在部署前不会只靠“牌还在、能量够”判断可继续。

```text
DeployCurrentTurn
  → CombatBeamSolver.ReplayDiagnosticPrefix
  → 冻结 predicted ContinuationStamp / remote fingerprint
  → EnqueueAndCaptureActionAsync
  → 等原生 action / Choice settle
  → BuildSafeActionRevalidationFacts
  → MultiplayerSafeExecutePolicy.RevalidateAction
  → Continue / wait world update / replan / abort
```

位置：

- [CombatBeamSolver.PathDiagnostics.cs](../src/Search/CombatBeamSolver.PathDiagnostics.cs) `ReplayDiagnosticPrefix`
- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs)
  - `DeployCurrentTurn`
  - `BuildSafeActionRevalidationFacts`
  - `EnqueueAndCaptureActionAsync`
- [MultiplayerSafeExecutePolicy.cs](../src/Runtime/MultiplayerSafeExecutePolicy.cs)
  - `TryBeginAction`
  - `BeginRevalidation`
  - `RevalidateAction`
  - `AcceptAction/Abort`
- [ContinuationStamp.cs](../src/Runtime/ContinuationStamp.cs)

作用：本地合法连锁可以继续，真实远端插入或语义漂移会撤销旧后缀；已经提交但结果未知的动作不会被重复提交。

## 4. U2：单人/多人共享搜索主核

`SinglePlayerFullRoute`、`MultiplayerSinglePlayerCore` 和实验 `MultiplayerLocalCrossTurn` 不再各自复制一套 Beam。

位置：

- [SolverController.cs](../src/Runtime/SolverController.cs)：选择 `SearchRoutePolicy`。
- [MultiplayerLocalCrossTurnContracts.cs](../src/Search/MultiplayerLocalCrossTurnContracts.cs)：哪些策略使用完整本地跨回合搜索。
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)
- `CombatBeamSolver.*`

同抽象状态、同目标、同预算的 pinned differential 已验证共享候选、完整路线、终局值、score 和逻辑工作。真实多人差异保留在根状态、目标/权限和网络 revalidation 层，而不是复制搜索器。

## 5. U3：公平情景复评

实验多人预测栈已经实现固定情景集合、公平覆盖和 Unknown/Terminal/Completed 语义。

位置：

- [MultiplayerScenarioReevaluationPolicy.cs](../src/Search/MultiplayerScenarioReevaluationPolicy.cs)
  - `ScenarioSpecs`
  - `ReserveExpandedBranchBudget`
  - `HasCompleteCoverage`
  - `CanRerank`
  - `Aggregate`
  - `Compare`
- [CombatBeamSolver.FinalPlanOrdering.cs](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs) `FinalPlanOrdering.Select`

调用关系：

```text
FinalPlanOrdering.Select
  → 按 CurrentTurnDecisionKey 聚合候选
  → 对每个 decision 使用同一 ScenarioSpec 集合
  → 记录 Completed / Terminal / Unknown
  → 只有满足覆盖合同才 CanRerank
  → scenario rank
  → final selection
```

复评预算来自原请求预算，不额外增加总节点。

## 6. U4：风险解释与目标数学

U4 修复过“伤害越集中到脆弱队员，interim loss-equivalent 反而越好”的反例。

生产 Beam 的入口：

```text
CombatBeamSolver.BeamRetentionPolicy.BuildMultiplayerObjectiveRank
  → MultiplayerCombatObjectiveMath.BuildRank
  → MultiplayerCombatObjectiveMath.Compare
```

位置：

- [CombatBeamSolver.BeamRetentionPolicy.cs](../src/Search/CombatBeamSolver.BeamRetentionPolicy.cs) `BuildMultiplayerObjectiveRank`
- `MultiplayerCombatObjectiveMath.cs`
- [MultiplayerScenarioReevaluationPolicy.cs](../src/Search/MultiplayerScenarioReevaluationPolicy.cs)
  - `MeasureRisk`
  - `CompareStrategies`
  - `CompareByRiskStrategy`
  - `SelectNominalToleranceExperimentIndex`

实验情景层同时能报告 Robust、NominalReference 和 BoundedRisk，但默认 local-single-core 不依赖这一实验层；生产不会把情景等权均值冒充真实队友概率。

## 7. U5：local → remote forecast → local 的顺序语义

实验多人预测支持：

```text
本地动作 A
  → detached teammate forecast B
  → B 后模拟状态
  → 本地动作 C
```

remote forecast 只存在于 detached simulator，不获得部署权限。顺序折叠必须证明完整 future fingerprint 相同；攻击/易伤、提前终局、共享生成 RNG、资源改变和抽牌/牌堆顶都已经有顺序敏感反例。

生产部署遇到 forecast observation 会停止条件后缀，回到真实观察后重新规划，不会跳过 forecast 节点直接执行 C。

主要位置：

- teammate/shadow forecast 的 Search 实现；
- [CombatBeamSolver.FinalPlanOrdering.cs](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs) 情景/决策比较；
- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs) forecast boundary 停止；
- [ContinuationStamp.cs](../src/Runtime/ContinuationStamp.cs) 完整未来状态比较。

## 8. U6：旧执行上限和兼容入口已清理

已经删除的历史边界：

- 固定 32-action ceiling；
- 旧 MP-2A 单动作兼容入口/limit aliases；
- 依赖这些旧入口的 validator。

Safe Execute 的实际动作上限改为已选择路线本身，并继续由逐动作 revalidation 控制。

当前正式边界由：

- [SolverSessionCapabilities.cs](../src/Runtime/SolverSessionCapabilities.cs)
- [MultiplayerSafeExecutePolicy.cs](../src/Runtime/MultiplayerSafeExecutePolicy.cs)
- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs)

共同决定。

## 9. Rolling Horizon、P0–P4 与质量修复已并入主架构

三份历史计划已经转成实现档案：

- [Rolling_Horizon_Reuse_Architecture.md](Rolling_Horizon_Reuse_Architecture.md)：跨请求 continuation、R0/R1、新根 replay、foreground/background、斩杀窗口。
- [Multiplayer_LocalCore_Search_Optimization.md](Multiplayer_LocalCore_Search_Optimization.md)：P0–P4 候选保留、seed replay、incumbent、Smart 调度、热点优化。
- [CombatSolver_Quality_First_Next.md](CombatSolver_Quality_First_Next.md)：坏路线、药水、current-turn、斩杀窗口、排序与 Safe Execute 修复。

## 11. 多人战前预计算与 Boss 通关优先（2026-09-30）

本专项目前**只完成静态可行性审计，没有生产实现**。这里只记录现有基础，不保存施工清单。

已经确认的代码基础：

- [SolverSessionCapabilities.cs](../src/Runtime/SolverSessionCapabilities.cs)：战前 capability 有明确权限入口。
- [PreCombatLiveStateSnapshot.cs](../src/Api/PreCombatLiveStateSnapshot.cs)：已有战前 live snapshot。
- [PreCombatForecastWorker.cs](../src/Api/PreCombatForecastWorker.cs)：已有独立 worker。
- [PreCombatForecastApi.cs](../src/Api/PreCombatForecastApi.cs) / `PreCombatForecastContracts.cs`：已有标量 forecast API/合同。
- [SolverInterimResultOrdering.cs](../src/Search/SolverInterimResultOrdering.cs)、[CombatBeamSolver.FinalPlanOrdering.cs](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs)、[CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)：Boss/战损排序实际分布在多个层。
- [ActEndingBossPolicy.cs](../src/Search/ActEndingBossPolicy.cs)：Act 结束、回血和 Boss 场景价值已有独立语义。

审计结论：已有多人 run-state/replay-state 材料只能证明“有恢复输入”，不能证明当前单人 setup 能正确恢复多人缩放/所有权。没有通过恢复验证的战前多人预测不会作为生产能力。

该专项当前状态由 [CODEX_HANDOFF.md](CODEX_HANDOFF.md) 维护。

## 12. 多人搜索加速（2026-10-01）

本节只记录已经实施的 S0–S3 事实；未实施项不在这里展开。

### S0：请求/成员/阶段成本统计

已实现：

- [SearchPerformanceMetrics.cs](../src/Search/SearchPerformanceMetrics.cs)
- [SearchRequestWorkTotals.cs](../src/Search/SearchRequestWorkTotals.cs)
- [ParallelExpansionWorkProfile.cs](../src/Search/ParallelExpansionWorkProfile.cs)
- Coordinator/Beam ledger 中的成员分类与阶段累计

真实 Host/Client 已能看到 Snapshot、Action、RoundAdvance、Fork、Fingerprint、Prune 等线程累计热点，并区分 baseline、portfolio、Smart Potion、route replay、seed 等成员。

### S1：新根 replay 与公开变化恢复

已实机覆盖的生产链包括：

- Targets-only 新根 route replay；
- 目标死亡后旧 seed 截断、新根恢复；
- 跨回合能量状态来源修正；
- 斩杀窗口公开伤害失效 → fresh search → 新授权部署。

关键入口：

- [SolverController.SearchLifecycle.cs](../src/Runtime/SolverController.SearchLifecycle.cs)
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) `TryReplayContinuationRoute`
- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs)

最新显式 route adoption 的 materialized incumbent scope 修复位于：

- [SolverProgress.cs](../src/Runtime/SolverProgress.cs) `SearchInteractionState.FinalizeWorkerResult`
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) `ResolveTakeoverResult`

当前是否还有实机未验证项只看 handoff。

### S2：request-local 普通转移 hydration

已实现但生产默认关闭：

- [CombatBeamSolver.RequestTransitionHydration.cs](../src/Search/CombatBeamSolver.RequestTransitionHydration.cs)
- request 生命周期由 [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) 创建和释放

首验后可以在同请求共享严格普通 PlayCard 后态，但固定输入中构键、完整校验、Fork、重新 Snapshot 的成本没有形成净收益，因此没有默认启用和扩容。

### S3：已落地的热点优化

当前已经进入代码的严格/受控优化包括：

1. RNG 重建去掉按旧 Counter 的无效推进，随后仍恢复完整 counter/state。
2. calculated-history 根计数预聚合和本地 owner 快路径。
3. StrategicEffects 上下文减少重复关键词读取，并绑定捕获 simulator 的能量/状态来源。
4. ProjectedShuffle 和持续效果上下文复用原生牌的纯值；第三方或可外部修改模型仍走实时计算。
5. Snapshot/Fingerprint 细分诊断仅在性能测量开关下启用，不改变生产评分。
6. request hydration 继续默认关闭，因为命中不等于净收益。

主要位置：

- [CombatBeamSolver.StateEvaluation.cs](../src/Search/CombatBeamSolver.StateEvaluation.cs) `Snapshot/StableShuffleProjection/BuildStateKey`
- [SimulatedCombatState.cs](../src/Search/SimulatedCombatState.cs) `AppendFingerprint/EffectivePowers`
- [SimulatedCombatState.CardEventHistory.cs](../src/Search/SimulatedCombatState.CardEventHistory.cs)
- [CombatPredictionHistory.cs](../src/Engine/InCombat/Simulation/CombatPredictionHistory.cs)
- 相关 StrategicEffects / Preview / COW 支持文件

没有通过当前证据进入生产的方案不在本实现记录中列为任务。当前性能工作状态以 [CODEX_HANDOFF.md](CODEX_HANDOFF.md) 为唯一入口。
