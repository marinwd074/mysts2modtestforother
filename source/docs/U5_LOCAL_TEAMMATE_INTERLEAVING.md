# U5 — 本地与队友关键顺序

状态：**COMPLETE（生产代码、合同、pinned 0.107.1 构建/回归完成；真实 Host/Client U5 专项 smoke 仍 UNVERIFIED）**

实现基线：`gpt/u5-local-teammate-interleave`，代码验收 HEAD `0ac206316e6a4d5914b52dbba1a77c93f8056e4a`。

## 目标

U5 只扩展“本地动作与预测队友动作”的同回合关键交错，不重做已有的队友↔队友 Shadow 交错，也不改变 U4 风险目标。

目标语义仍是同一个模拟转移：

`s_next = F(s, action, rng/history/trigger/death state)`

本地动作和队友预测动作都必须通过现有生产模拟器逐动作推进，不能折算成预估伤害。

## 已实现

1. **新增 forecast-only 观察动作**
   - `PlanActionKind.TeammateForecast` 只表示搜索中的预测队友动作。
   - `IsExecutable=false`，不会进入本地原生部署。
   - `CurrentTurnDecisionKey` 在第一个 forecast 边界停止；边界后的本地动作属于观察后的条件续路，不会泄漏为当前已授权决策。

2. **本地 A → 队友 B → 本地 C**
   - `CombatBeamSolver.MultiplayerInterleaving.cs` 在一个已完成的本地 PlayCard 后建立最多一个 teammate observation。
   - `ShadowTeammatePlanner.BuildTeamSingleActionRoutes` 从完整共享 simulator fork 枚举合法队友单动作。
   - 预测 B 通过现有 `TryPlayCandidateInPlace`、死亡 Power 清扫、action boundary settle、共享 RNG/牌堆/资源/History 语义推进。
   - 预测 B 后继续使用普通搜索核，因此可以形成本地 A → 预测 B → 本地 C。

3. **A→B 与 B→A 都真实推进 F**
   - 前向路线直接执行 A 后 B。
   - `ProbeReverseInterleaveOrder` 从 A 前状态 detached fork，先重放同一 B，再通过生产 `Replay` 重放 A。
   - 若 B 后 A 已不合法、目标消失、牌身份变化或出现不支持边界，则标记 `ReverseUnavailable`，不假装可交换。
   - 两个顺序都完成后，用 `ShadowFutureStateFingerprint` 比较完整 modeled future state。

4. **只有可证明等价才允许顺序合并**
   - `MultiplayerInterleaveOrderPolicy.CanCollapseOrder` 仅接受 `ExactEquivalent`。
   - `OrderSensitive` 与 `ReverseUnavailable` 均禁止精确合并。
   - 含 forecast 路线的 transposition key 改用 `ShadowFutureStateFingerprint`，覆盖 captured player/enemy 状态、牌堆、Power、RNG、History、死亡处理、processed deaths 和 ended-player 等已建模状态。
   - 超过有限 beam 时仍可能发生显式 heuristic pruning；它不被称为精确可交换证明。

5. **不主动等待理想队友**
   - 每个本地回合最多插入 1 个 forecast observation，单观察候选最多保留 4 条。
   - `AllowsProactiveWaitForTeammate=false`。
   - 当前实现没有“空等 N 秒期待队友行动”的调度动作，也没有无限等待。
   - 搜索中的队友先行动顺序只用于 reverse-order 语义/等价探针；真实队友若先行动，live world change 会触发既有观察/重规划流程。

6. **部署在 forecast 边界停止旧后缀**
   - Safe Execute 与普通部署都只取 forecast 之前的本地 executable prefix。
   - Safe Auto 遇到 `kind_teammateforecast` 不永久关闭，而是结束当前授权、重新捕获/搜索。
   - 不会跳过 forecast 节点继续自动打其后的条件本地动作。
   - U3 情景复评遇到 forecast observation 会 fail closed，不把预测 B 固定成当前真实决策的一部分。

## 验证

### Compatibility / contracts

GitHub Actions run `35890994702`：**SUCCESS**。

关键 U5 合同：

- forecast observation 不属于 deployable current decision；
- forecast 后的 contingent local suffix 不进入当前 decision key；
- 每回合只允许 1 个 forecast observation；
- 不存在 proactive teammate wait；
- 仅 `ExactEquivalent` 可做顺序 collapse；
- Safe Auto 在 forecast observation boundary 保持 fresh-search 资格。

### Pinned 0.107.1

GitHub Actions run `35890656903`：**SUCCESS**。

同一次 run 已通过：

- CombatSolver Release，warnings-as-errors；
- U0/U1 production replay；
- U2 single-player degenerate equivalence；
- P0 baseline contracts；
- P0/P1 pinned runtime；
- historical P0 A/B classification。

没有扩大 Beam、节点预算或时间预算来换取通过。

## 仍未验证

真实双端 Host/Client 的 U5 专项 smoke 尚未执行，因此不能声称网络实机已经观察到 forecast boundary。

最小实机补测只需要三类：

1. 本地先施加 Vulnerable，队友攻击，再由本地继续攻击；确认远端动作出现时旧后缀不被直接部署，而是 fresh replan。
2. 一个顺序会先击杀/触发死亡效果，另一个顺序不会；确认日志中的 `reverse_order` 为 `OrderSensitive` 或 `ReverseUnavailable`，不显示 `order_collapsible=true`。
3. 能影响共享 RNG、抽牌或资源的等价测试对；确认不同后态不会被 exact transposition 合并。

该 runtime debt 不阻止 U5 代码阶段收口；进入 U6 前做最终默认迁移/实机闭环时再集中验证。
