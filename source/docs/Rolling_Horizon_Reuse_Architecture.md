# Rolling Horizon Reuse：已实现架构说明

> 本文件是**实现档案**，不是执行计划。当前任务、未完成风险和下一步只看 [CODEX_HANDOFF.md](CODEX_HANDOFF.md)。
>
> 历史阶段名 A–H 只用于把 Git 历史与当前代码对应起来；本文只记录已经落地的行为、代码位置、作用和调用链。

## 1. 已落地结果

| 历史阶段 | 已实现行为 | 作用 | 主要位置 |
|---|---|---|---|
| A | 搜索请求、成员、阶段和发布耗时/工作量可观测 | 区分“重算太多”和“单次搜索太慢” | [SearchPerformanceMetrics.cs](../src/Search/SearchPerformanceMetrics.cs) `Begin/End/CapturePhases`；[SearchRequestWorkTotals.cs](../src/Search/SearchRequestWorkTotals.cs) `Record/CapturePhases`；[ParallelExpansionWorkProfile.cs](../src/Search/ParallelExpansionWorkProfile.cs) `Record` |
| B | 延迟影响、跨回合结果和比较范围显式进入值对象 | 不用固定“看 N 回合”代替语义闭合 | [CombatPlan.cs](../src/Search/CombatPlan.cs) `DeferredImpactOutcome.Capture`、`DeferredImpactCoverage.Capture`、`CombatProgressState.Capture/Advance` |
| C | 战斗级 R0 终局精确转移缓存 | 同战斗、相同父状态/动作/策略下跳过重复终局模拟，同时重新构造路径相关评分 | [CombatTransitionMemo.cs](../src/Search/CombatTransitionMemo.cs) `MayContain/TryReadTerminal/StoreTerminal`；[CombatBeamSolver.R0TransitionMemo.cs](../src/Search/CombatBeamSolver.R0TransitionMemo.cs) |
| D | 新根路线重放、R1 seed/hydration、严格失败回退 | WorldVersion/RNG/目标轻微变化后优先验证旧动作建议，而不是无条件冷启动 | [SolverController.SearchLifecycle.cs](../src/Runtime/SolverController.SearchLifecycle.cs) `RequestSearch/CaptureContinuationSeedActions`；[CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) `TryReplayContinuationRoute`；[CombatBeamSolver.R1TransitionHydration.cs](../src/Search/CombatBeamSolver.R1TransitionHydration.cs) |
| E | 前台合格路线与后台补充搜索分离 | 已经找到的好路线可以先显示；后台更差结果不能抖掉前台 incumbent | [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) `ShouldPreferForegroundAtCompletion`、`ShouldAdmitE4SupplementalResult`、`RunSupplementalAudits` |
| F0 | 情景预热只做 shadow 命中测量 | 统计预测后态是否会成为下一真实根，不改变生产搜索和部署 | [ScenarioPrewarmShadowTracker.cs](../src/Search/ScenarioPrewarmShadowTracker.cs) `ObserveRoot/RecordPrediction` |
| H | 固定输入质量/工作回归 + 真实多人“高血复用→斩杀窗口重算”闭环 | 证明 Rolling Horizon 的生产边界没有靠放宽正确性换速度 | [MultiplayerCombatObjectivePolicy.cs](../src/Search/MultiplayerCombatObjectivePolicy.cs) `IsInLethalRecalculationWindow/ShouldAllowLivingEnemyHpDecreaseReuse` |

历史 F1、G SSD 冷存储和 D3.5 整 frontier/subtree 恢复没有进入当前生产实现。它们不构成当前依赖，也不在本文保留后续施工步骤。

## 2. Runtime 主调用链

### 2.1 从真实战斗变化到结果发布

```text
SolverDispatcher
  → SolverController.MonitorCombatPresence
  → SolverController.RequestSearch
  → CombatRootSnapshot.Capture
  → 精确 continuation / bounded refresh / route replay 尝试
  → CombatSearchCoordinator.Solve
  → CombatSearchCoordinator.SolveRequest
  → CombatBeamSolver / portfolio / Smart Potion
  → foreground progress
  → SolverController.CompleteSearchCore
  → UI result / 可选 Deployment
```

关键所有权：

- [SolverDispatcher.cs](../src/Runtime/SolverDispatcher.cs) 每帧驱动 `SolverController.MonitorCombatPresence` 和搜索进度刷新。
- [SolverController.cs](../src/Runtime/SolverController.cs) `MonitorCombatPresence` 负责观察 live combat；`InvalidateMultiplayerSearch` 管理真实世界变化后的旧请求失效。
- [SolverController.SearchLifecycle.cs](../src/Runtime/SolverController.SearchLifecycle.cs) `RequestSearch` 在主线程捕获稳定根、创建请求、冻结策略并启动 worker。
- [CombatRootSnapshot.cs](../src/Runtime/CombatRootSnapshot.cs) 保存 worker 可读的冻结根；后台搜索不直接读取变化中的 live combat。
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) `Solve/SolveRequest` 负责搜索成员编排、早期 replay、portfolio、补充审计和最终收口。

### 2.2 精确 continuation 与新根 replay

```text
旧 SolverResult / Continuation
  → live ContinuationStamp
  → exact continuation 可用？
      ├─ 是：直接复用
      └─ 否：
          SolverController.SearchLifecycle
            → CaptureContinuationSeedActions
            → SearchPolicySnapshot.ContinuationSeedActions /
              ContinuationRouteReplayActions
            → CombatSearchCoordinator.TryReplayContinuationRoute
            → 新根逐动作 replay + 重新 Snapshot/评分
              ├─ 合格：0 Beam expanded 的新 SolverResult
              └─ 不合格：正常 fresh search
```

这里复用的是**动作建议或精确后态**，不是旧 SearchNode 的部署权。

- [ContinuationStamp.cs](../src/Runtime/ContinuationStamp.cs) `CaptureLive/CapturePredicted/DescribeDifferences` 是跨回合状态比较的语义文本。
- [LiveCombatStamp.cs](../src/Runtime/LiveCombatStamp.cs) 为 Runtime 提供 live / local-core validity 视图。
- [SolverController.SearchLifecycle.cs](../src/Runtime/SolverController.SearchLifecycle.cs) `CaptureContinuationSeedActions` 从旧路线冻结值型动作。
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) `TryReplayContinuationRoute` 从**新根**重新执行动作，成功后得到新 route identity 和新授权依据。
- [CombatBeamSolver.PathDiagnostics.cs](../src/Search/CombatBeamSolver.PathDiagnostics.cs) `ReplayDiagnosticPrefix` 是 bounded refresh、验证和部分 replay 共用的生产模拟入口。

### 2.3 高血阶段与斩杀窗口

默认多人 local-single-core 允许一种明确的策略性兼容：敌人仍存活、只发生 HP/Block 向下漂移，而且没有进入斩杀窗口时，旧路线可以进入 continuation/replay 流程。

```text
remote enemy HP decrease
  → MultiplayerCombatObjectivePolicy.ComputeEnemyDurabilityRatio
  → IsInLethalRecalculationWindow
  → ShouldAllowLivingEnemyHpDecreaseReuse
      ├─ 高血：允许 local-core 兼容复用/重放
      └─ 斩杀窗口：拒绝宽松 continuation
                   → fresh root → fresh search → 新授权
```

位置：

- [MultiplayerCombatObjectivePolicy.cs](../src/Search/MultiplayerCombatObjectivePolicy.cs)
  - `ComputeEnemyDurabilityRatio`
  - `IsInLethalRecalculationWindow`
  - `ShouldAllowLivingEnemyHpDecreaseReuse`
- [MultiplayerLocalCrossTurnContracts.cs](../src/Search/MultiplayerLocalCrossTurnContracts.cs) 保存 local-core continuation / replay 的模式合同。
- [SolverController.SearchLifecycle.cs](../src/Runtime/SolverController.SearchLifecycle.cs) 将实际 rejection reason、seed admission 和 replay 请求接入搜索。

作用：普通高血队友伤害不再自动造成一整轮冷搜索；跨入斩杀线、目标死亡、资源/RNG/牌堆等真实语义变化仍重新定根。

## 3. R0：战斗级终局转移缓存

### 3.1 调用链

```text
CombatBeamSolver expansion
  → TryReadR0TerminalTransition
  → CombatTransitionMemo.MayContain
  → CombatTransitionMemo.TryReadTerminal
      ├─ hit：按当前路径重建评价
      └─ miss：正常模拟
               → StoreR0TerminalTransition
               → CombatTransitionMemo.StoreTerminal
```

具体位置：

- [CombatBeamSolver.R0TransitionMemo.cs](../src/Search/CombatBeamSolver.R0TransitionMemo.cs)
  - `CanUseR0TransitionMemoForReplay`
  - `TryReadR0TerminalTransition`
  - `StoreR0TerminalTransition`
  - `CaptureR0TerminalEvaluationContext`
- [CombatTransitionMemo.cs](../src/Search/CombatTransitionMemo.cs)
  - `CapturePolicyIdentity`
  - `MayContain`
  - `TryReadTerminal`
  - `StoreTerminal`
- [CombatPlan.cs](../src/Search/CombatPlan.cs) `SimulationSnapshot.CloneValueOnlyForTransitionMemo`

终局后态可以相同，但路径动作数、累计战损、回血、历史计数和玩家最大 HP 上下文可能不同。因此 R0 保存安全值型输出与评价上下文，命中后按当前路径重建评分；策略身份或语义不兼容时拒绝。

## 4. R1：新根 seed 与 request-local hydration

### 4.1 seed probe / replay

```text
Runtime 旧路线
  → CaptureContinuationSeedActions
  → SearchPolicySnapshot
  → CombatBeamSolver seed probe
  → 新根逐动作真实 replay
  → 合法完整 seed / 合法前缀 / 拒绝
  → 正常 Beam 与 seed 共同进入当前根比较
```

RNG 改变时允许重新尝试动作建议，但不复用旧抽牌结果；目标死亡或动作身份无法解析时前缀自然截断。

### 4.2 hydration

[CombatBeamSolver.R1TransitionHydration.cs](../src/Search/CombatBeamSolver.R1TransitionHydration.cs) 提供：

- `CanUseR1TransitionHydration`
- `R1TransitionHydrationKey`
- `TryReadR1TransitionHydration`
- `ObserveR1TransitionHydration`
- `StoreR1TransitionHydration`

首个相同 parent/action 仍真实模拟并校验；通过验证的非终局普通 PlayCard 后态才可以在同请求尾部被再次 Fork。冲突按 key fail closed，不把整个搜索状态当作近似相等。

## 5. E：前台与后台分离

主要函数均在 [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)：

- foreground promotion：首个合格结果或严格改善时发布。
- `ShouldPreferForegroundAtCompletion`：最终成员结束时保护已经更优的前台结果。
- `ClassifyE4SupplementalRelation` / `ShouldAdmitE4SupplementalResult`：后台结果分类并决定是否接管 incumbent。
- `ComputeE4*DeadlineMilliseconds`：只约束已批准前台之后的 supplemental 长尾，不缩主 Beam。
- `RunSupplementalAudits`、`SearchSmartPotionGradientScheduled`：继续执行药水等补充成员。
- `ResolveTakeoverResult`：处理用户显式采用和 current-turn takeover 的结果范围。

作用是让好路线更早可见，同时避免更差后台结果覆盖；Smart Potion 等长尾仍能继续改善最终结果。

## 6. F0：只测量，不行为复用

[ScenarioPrewarmShadowTracker.cs](../src/Search/ScenarioPrewarmShadowTracker.cs) 的 `ObserveRoot` 与 `RecordPrediction` 只记录上一个实验情景预测的 `ContinuationStamp` 与下一 live root 是否 exact match。

它不保存 SearchNode、分数、动作或部署权限，`behavioral_reuse=false`。默认 local-single-core 不依赖 F0。

## 7. 与多人本地核心优化的关系

Rolling Horizon 负责“真实世界变化以后，哪些成果还能安全继续用”；P0–P4 负责“local-single-core 如何更早拿到好候选、如何减少单次搜索成本”。

交叉点：

- [CombatBeamSolver.FinalPlanOrdering.cs](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs) 保存 replay candidates。
- [SolverController.MultiplayerPlanRefresh.cs](../src/Runtime/SolverController.MultiplayerPlanRefresh.cs) `TryBoundedMultiplayerPlanRefresh` 对轻量 HP/Block drift 做新根 bounded replay。
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) `TryReplayContinuationRoute` 对更一般的新根动作建议做正式 route replay。
- 两者最终都必须生成**当前根的新 SolverResult**；旧结果不能只更新 WorldVersion 后直接执行。

P0–P4 的完整实现地图见 [Multiplayer_LocalCore_Search_Optimization.md](Multiplayer_LocalCore_Search_Optimization.md)。

## 8. 验证状态

长期有效的结论：

- H1 固定输入保持路线、质量、expanded nodes 和 transitions 一致，未发现固定工作回归。
- H2 真实 Host/Client 已覆盖高血普通敌人 HP 下降复用，以及跨入斩杀窗口后 fresh re-root。
- R0/R1、route replay、目标死亡、RNG 漂移、显式 route adoption 的合同入口集中在 [U0U1PinnedHarness](../tools/U0U1PinnedHarness/Program.cs)。
- 最新未闭合的运行时边界统一由 [CODEX_HANDOFF.md](CODEX_HANDOFF.md) 维护。
