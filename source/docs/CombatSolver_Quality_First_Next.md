# 多人求解质量：已实现修复索引

> 本文件原名“Quality First Next”，现在只记录已经完成的质量修复及其代码位置。
>
> 它不再定义项目下一步。当前任务和未验证风险统一维护在 [CODEX_HANDOFF.md](CODEX_HANDOFF.md)。

## 1. 生产质量主链

默认多人已经使用单人搜索核心：

```text
SolverController 捕获设置/能力
  → SearchRoutePolicy.MultiplayerSinglePlayerCore
  → CombatSearchCoordinator
  → CombatBeamSolver
  → FinalPlanOrdering
  → 当前回合/完整路线前台结果
  → SolverController.Deployment
  → Safe Execute + native action
```

关键入口：

- [SolverController.cs](../src/Runtime/SolverController.cs)：选择 `MultiplayerSinglePlayerCore`。
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)：主搜索、portfolio、药水审计、foreground。
- [CombatBeamSolver.BeamRetentionPolicy.cs](../src/Search/CombatBeamSolver.BeamRetentionPolicy.cs)：Beam 保留与多人目标 rank。
- [CombatBeamSolver.FinalPlanOrdering.cs](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs)：最终候选选择、情景复评和 replay candidate。
- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs)：本地动作原生部署。

多人专属牌仍保留在真实牌堆/抽牌距离中，但默认不进入主动搜索和自动执行；队友也不会获得部署权限。

## 2. 状态变化不再等于整条路线失效

旧行为会在多人世界变化后很快清掉整个搜索结果。当前实现把问题拆成：

1. live 状态新鲜度；
2. 当前动作是否合法；
3. 当前路线在新根上是否仍值得保留。

入口：

- [SolverController.cs](../src/Runtime/SolverController.cs)
  - `MonitorCombatPresence`
  - `InvalidateMultiplayerSearch`
- [SolverController.MultiplayerPlanRefresh.cs](../src/Runtime/SolverController.MultiplayerPlanRefresh.cs) `TryBoundedMultiplayerPlanRefresh`
- [SolverController.SearchLifecycle.cs](../src/Runtime/SolverController.SearchLifecycle.cs) `RequestSearch/CaptureContinuationSeedActions`
- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) `TryReplayContinuationRoute`

调用链：

```text
MultiplayerClientProbe / WorldVersion 变化
  → MonitorCombatPresence
  → exact continuation 失败
  → bounded refresh 或 route seed replay
  → 新根重新模拟/重新评分
      ├─ 可保留：生成新 SolverResult
      └─ 不可保留：fresh search
```

目标死亡、资源、牌堆、关键 Power、RNG、动作身份等强变化仍 fail closed。

## 3. 本地药水闭环

本地药水已经从“搜索里能看到、执行层拒绝”改成搜索和 Safe Execute 同一闭环。

### 代码位置

- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs)：在 `DeployCurrentTurn` 中解析本地 potion slot、调用原生药水使用并捕获 `UsePotionAction`。
- [MultiplayerSafeExecutePolicy.cs](../src/Runtime/MultiplayerSafeExecutePolicy.cs)：`ClassifyStructural/ClassifyResolved/RevalidateAction` 对本地药水使用与普通出牌共用逐动作安全事务。
- [NativeChoiceRuntime.cs](../src/Runtime/NativeChoiceRuntime.cs)：驱动需要 Choice 的原生选择。
- `PotionModel.EnqueueManualUse`：继续使用游戏原生药水动作入口。
- [ContinuationStamp.cs](../src/Runtime/ContinuationStamp.cs)：本地药水槽位/PotionId 变化进入 continuation 语义比较。

### 调用链

```text
搜索结果 UsePotion
  → DeployCurrentTurn
  → live slot + PotionId 再解析
  → PotionModel.EnqueueManualUse
  → 捕获 UsePotionAction
  → NativeChoiceRuntime（如需要）
  → action settle
  → predicted/live post-state
  → MultiplayerSafeExecutePolicy.RevalidateAction
  → 继续后缀或 fresh replan
```

自动目标首版只允许本地玩家或敌人；没有通过 pinned 语义验证的队友目标不会被搜索执行链静默放开。

Smart / Disabled / RequireAtLeastOne 和 PotionStrategy 继续使用单人语义。多人 potion-free baseline 只以本地玩家损失判断“是否值得喝自己的药”，团队排序只在已通过药水策略的路线之间继续比较。

## 4. 基础最终排序的真实坏例修复

### 4.1 0 费 Anger 被长期成本压过即时进展

历史多人未完成路线会过早比较 `AngerCopiesGenerated`，导致合法的 0 费即时伤害被长期副作用压掉。当前多人未完成路线会先比较确定的敌方 HP 进展，再进入该长期成本。

位置：

- [CombatBeamSolver.FinalPlanOrdering.cs](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs) `FinalPlanOrdering.Select` 的多人未完成候选比较。
- Beam 内相关 rank 仍由 [CombatBeamSolver.BeamRetentionPolicy.cs](../src/Search/CombatBeamSolver.BeamRetentionPolicy.cs) 提供。

### 4.2 当前回合有牌可打却被短 EndTurn tie-break 选走

当局部质量等价时，旧排序可能因动作更短选择 `EndTurn`。当前多人本地核心在对应平局中保留“当前回合有可执行 PlayCard”的候选。

作用：避免手里还有合法收益牌、能量也允许，却因短路线 tie-break 得到空过建议。

## 5. 当前回合质量优先与预测窗

默认 `MultiplayerSinglePlayerCore` 已加入两个与体验直接相关的质量层：

1. **当前回合 scout**：先用同一 Beam/模拟主核寻找固定手牌下的低战损近回合前缀，消耗计入原请求预算。
2. **最终 current-turn priority**：完整搜索结束后，把完整路线的首回合边界与已保留的 current-turn incumbent 用 `SolverInterimResultOrdering` 比较；当前回合 incumbent 严格更优时可物化为 `CurrentTurnAdoption`，下一回合重新从真实根搜索。
3. **本地核心预测窗**：当前回合之后保留有限后续投影，用于避免只看一张牌造成短视，同时不要求每次都把整场长尾算完才给建议。

关联位置：

- [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs)：current-turn seed/foreground/takeover。
- [SolverProgress.cs](../src/Runtime/SolverProgress.cs)：`SearchInteractionState` 和 `CurrentTurnAdoption`。
- [CombatBeamSolver.Phases.cs](../src/Search/CombatBeamSolver.Phases.cs)：成员 progress 与路线预览。
- route policy 和跨回合边界在 [MultiplayerLocalCrossTurnContracts.cs](../src/Search/MultiplayerLocalCrossTurnContracts.cs)。

这里没有“必须花光能量”的硬规则；仍由正式质量比较决定。

## 6. 好路线找到后立即进入 anytime 预览

旧预览要求 `Snapshot.HasSimulator`，导致已经完成、为了释放内存而丢掉 simulator 的优质终局节点直到最终重新物化才显示。

当前实现允许 retained/released snapshot 参与正式 FinalOrdering 预览：

- [CombatBeamSolver.Phases.cs](../src/Search/CombatBeamSolver.Phases.cs) 本地函数 `PublishRoutePreview`
- 正式采用仍重新 materialize/replay，从根重放并重新授权

作用：只提前“已经找到的路线”的可见时间，不改变 Beam、评分、节点预算或候选集合。

## 7. 高血普通队友伤害与斩杀窗口

默认策略不是“敌人 HP 变化全部忽略”。

[MultiplayerCombatObjectivePolicy.cs](../src/Search/MultiplayerCombatObjectivePolicy.cs)：

- `ComputeEnemyDurabilityRatio`
- `IsInLethalRecalculationWindow`
- `ShouldAllowLivingEnemyHpDecreaseReuse`

行为：

```text
敌人仍高血 + 仅存活目标 HP/Block 下降
  → 可继续 local-core continuation / replay

进入斩杀窗口、目标死亡或其它语义变化
  → continuation 拒绝
  → fresh re-root
  → 重新计算斩杀牌序
```

这就是当前“高血主要维持低战损路线，低血重新关注斩杀时机”的实现边界。

## 8. 共享 RNG 与 local-core validity

多人队友动作可能推进共享 RNG。当前 local-core validity 已区分：

- 会改变本地牌序/动作语义、必须重算的 RNG；
- 对当前 local-core 决策无影响、可由新根 replay 重新验证的漂移。

具体状态编码和差异入口在：

- [ContinuationStamp.cs](../src/Runtime/ContinuationStamp.cs)
- [LiveCombatStamp.cs](../src/Runtime/LiveCombatStamp.cs)
- [SolverController.SearchLifecycle.cs](../src/Runtime/SolverController.SearchLifecycle.cs)
- [MultiplayerLocalCrossTurnContracts.cs](../src/Search/MultiplayerLocalCrossTurnContracts.cs)

包含会读取全局历史的牌仍保留更严格比较，不把“共享 RNG 可容忍”扩成通用忽略。

## 9. 战损只以本地玩家作为默认 local-core 目标

默认多人 local-single-core 的生产质量目标已经与“本地单人算法”对齐：队友真实状态仍存在于模拟世界，但本地搜索策略不会把队友战损平均进自己的基本出牌评价。

多人实验 Team Objective / Scenario / Robust 仍位于独立实验预测栈；默认 local-single-core 不依赖它们。

相关位置：

- [SolverController.cs](../src/Runtime/SolverController.cs)：默认 route policy 选择。
- [MultiplayerCombatObjectivePolicy.cs](../src/Search/MultiplayerCombatObjectivePolicy.cs)：默认多人斩杀/耐久策略。
- [CombatBeamSolver.FinalPlanOrdering.cs](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs)：生产最终选择。
- 实验情景层见 [MultiplayerScenarioReevaluationPolicy.cs](../src/Search/MultiplayerScenarioReevaluationPolicy.cs)。

## 10. 特殊牌、遗物和 Choice 的共核

已经落地的原则是“复用单人实现，只改变所有权边界”，而不是复制一套多人算法。

### 遗物

- 策略目标只从本地玩家持有遗物产生；
- 队友遗物的真实被动/触发效果仍保留在多人模拟世界；
- 不把“只优化自己的遗物”错误实现成“假装队友没有遗物”。

### 特殊牌与历史变量

多项原先带单人门槛的状态指纹/计数已经对本地多人搜索开放，使不同真实计数状态不会被错误合并。至亮之炎等已有单人限制继续共享同一模拟和 Fork 逻辑，只按本地动作玩家计算策略额度。

### Choice

Headbutt 等已有单人 Choice 继续走 [NativeChoiceRuntime.cs](../src/Runtime/NativeChoiceRuntime.cs)；没有为多人复制新的选择器。

## 11. Safe Execute：搜索质量与执行正确性分离

即使搜索找到好路线，也必须通过执行层事务。

```text
SolverResult
  → StartDeployment / DeployCurrentTurn
  → ClassifyStructural / ClassifyResolved
  → ReplayDiagnosticPrefix 冻结 predicted 后态
  → native action
  → queue / Choice settle
  → BuildSafeActionRevalidationFacts
  → RevalidateAction
      ├─ Continue
      ├─ MarkAwaitingWorldUpdate / fresh search
      └─ Abort
```

位置：

- [CombatBeamSolver.PathDiagnostics.cs](../src/Search/CombatBeamSolver.PathDiagnostics.cs) `ReplayDiagnosticPrefix`
- [SolverController.Deployment.cs](../src/Runtime/SolverController.Deployment.cs)
- [MultiplayerSafeExecutePolicy.cs](../src/Runtime/MultiplayerSafeExecutePolicy.cs)

因此历史“路线里有牌但没自动打”的问题会区分为搜索/排序问题和部署 gate 问题，不再混成一个“算法差”。

## 12. 局部精确斩杀没有进入生产主链

历史质量阶段曾评估额外 DFS/精确 lethal。现有证据没有证明主 Beam 在固定真实输入中稳定漏掉一条已知合法短斩杀，因此没有加入固定 DFS 开销。

当前斩杀优化使用的是：

- 正常 Beam/portfolio；
- lethal window 触发 fresh re-root；
- current-turn quality/incumbent；
- 正式最终排序。

这是当前代码事实，不是待施工项。
