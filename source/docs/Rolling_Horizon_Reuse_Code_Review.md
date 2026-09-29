# 滚动搜索复用：代码审查与修复清单

审查基线：`b91a47e`。本文与 [滚动搜索复用架构计划](Rolling_Horizon_Reuse_Architecture.md) 并列，记录该基线的缺陷、优化点及验收条件，不替代主计划。以下项目均待修复；后续执行前须核对当前源码，避免重复施工。

## 1. P1：R0 终局缓存携带旧路径评分

**位置：** [CombatTransitionMemo.cs](../src/Search/CombatTransitionMemo.cs) 的 `TryReadTerminal`；[CombatBeamSolver.StateEvaluation.cs](../src/Search/CombatBeamSolver.StateEvaluation.cs) 的 `Snapshot`；[CombatPlan.cs](../src/Search/CombatPlan.cs) 的 `CloneValueOnlyForTransitionMemo`。

缓存命中直接复制旧 Snapshot，保留 `Score`。评分包含 `actionCount * ActionPenalty`，而缓存匹配的父状态、动作、策略及父状态文本不包含搜索路径的累计动作数。同一战斗中缓存跨请求保留，因此状态相同不代表路径评分可以直接复用。

触发场景：第一次搜索经过第 1 回合动作，预测第 2 回合某动作直接获胜并缓存终局；实际到达同一第 2 回合状态后重新建根，动作数从零计算，却可能取回包含旧路径动作惩罚的终局评分，影响候选排序。

**修复要求：**

- 分离可复用的状态转移结果与依赖当前路径的评价字段。
- 命中后按当前父节点和动作上下文重算评分；同时核对累计战损等路径字段，不能只修正一个分数字段便认定完整。
- 可参考 R1 hydration 使用当前路径上下文重新生成 Snapshot 的方式，但不得破坏 R0 值缓存的对象生命周期和隔离约束。
- 不以增加哈希校验代替路径上下文处理，也不通过清空跨回合缓存回避复用问题。

**验收：** 固定同一父战斗状态、动作和策略，分别使用不同累计动作数；缓存命中结果与各自新鲜回放的评分、质量字段一致。至少覆盖跨回合重新建根场景。

## 2. P2：更早斩杀的比较规则没有贯穿所有入口

**位置：** [SolverInterimResultOrdering.cs](../src/Search/SolverInterimResultOrdering.cs) 的 `IsBetter`、`CanPromoteDisplayedResult`、`ComparePrimaryQuality`；[CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) 的药水成员比较及 E4 补充结果准入。

新的 rolling-horizon 排序优先比较原始战损，再比较胜利及结束回合；前台替换仍先检查“战损减成长收益”，跨成员比较也保留了不同的优先级。因此不同入口会对同一对路线给出不一致结论。

已用实际排序源码的纯函数探针复现：

| 属性 | 新路线 | 当前路线 |
| --- | --- | --- |
| 存活且获胜 | 是 | 是 |
| rolling-horizon loss-first | 开启 | 开启 |
| 战损 | 0 | 0 |
| 结束回合 | 2 | 3 |
| 成长收益 | 0 | 5 |
| StrategicHpDeficit | 0 | -5 |

结果：`IsBetter(新, 当前) = true`，但 `CanPromoteDisplayedResult(新, 当前) = false`；反向提升也为 false，跨成员主质量比较仍偏向当前路线。更好路线可能已经算出，却无法更新前台；E4 也可能把两者分类为等价或不可比较。

**修复要求：** 为该模式统一质量比较顺序，并让前台替换、药水成员选择、完成结果协调和 E4 准入使用一致规则。保留死亡救济、资源策略等已有语义；其他模式不得随之无意改变。不要只删除一个条件而留下其他入口分歧。

**验收：** 上表的新路线在各相关入口均优先；同结束回合时仍按既定成长/资源规则比较；更高战损不能仅凭更早斩杀越过 loss-first 规则。

## 3. P2：Smart 药水搜索超时丢失已完成结果

**位置：** [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) 的 `SearchSmartPotionGradientScheduled`，尤其是 `StepLayer`、`CommitCompletedInOrder` 和 `deadlineExpired` 退出分支。

成员完成后先进入 `completed`，只有 `CommitCompletedInOrder` 才会发布并参与选择。主循环发现超时会在提交之前退出，最终返回旧 `selected`。

触发场景：同轮一药成员完成，随后二药成员耗尽剩余预算；已经完成且本可按顺序提交的一药结果没有被发布或选择。搜索工作被浪费，也可能丢掉更好的可用路线。

**修复要求：** 预算到期后停止推进成员，但在返回前按原顺序处理可提交的已完成结果，复用现有 Smart 资格判断、发布和审计逻辑。不得重新启动搜索、绕过顺序缺口或放宽准入。用户主动取消应与预算到期分别处理。

**验收：** 用可控调度夹具令一药成员先完成、二药成员随后超时，最终选择包含符合资格的一药结果；不合格结果仍被拒绝；用户主动取消仍符合原取消契约。

## 4. 性能：R0 应先判断终局准入，再生成状态文本

**位置：** [CombatBeamSolver.R0TransitionMemo.cs](../src/Search/CombatBeamSolver.R0TransitionMemo.cs) 的 `StoreR0TerminalTransition`；[CombatTransitionMemo.cs](../src/Search/CombatTransitionMemo.cs) 的 `StoreTerminal`、`IsSafeTerminalOutput`。

当前流程先生成完整 `ContinuationStamp.StateText`，再扫描动作历史判定纯转移，最后才在缓存内部拒绝非终局输出。符合动作条件的普通非终局扩展也会支付这些成本，却不可能写入终局缓存。

**修复要求：** 将现有廉价终局准入判断前移，并复用同一判定来源；只有可能入缓存时才生成文本和扫描历史。保持原有正确性条件、预算和并发，不增加重复哈希验证。

**验收：** 普通非终局输出不执行存储侧完整文本生成和历史扫描；终局缓存行为保持一致。用代表性固定输入核对调用次数或分配变化，不预设性能提升百分比。

## 执行与验证边界

建议顺序：R0 评分正确性 → 比较规则统一 → 超时结果回收 → R0 前置筛选。正确性修复与性能调整尽量分开，优先扩展现有测试入口，每次默认不超过 10 项；不得削减搜索质量、预算、并发或正确性保障来简化实现。

本轮审查未修改生产代码、未构建、未启动游戏。第 2 项已有纯函数复现；第 1、3、4 项依据调用链静态审查，尚未完成运行回归或性能测量。纯函数探针首次因测试夹具缺少导入而编译失败，补齐后成功，不属于项目构建失败。

本文落库仅需定向链接检查及 `git diff --check`，不触发上述修复验收。修复完成后直接更新对应项目状态和证据摘要，不另建重复报告。
