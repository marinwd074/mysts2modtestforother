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

### Pinned 0.107.1 order-sensitive production replay

新增到 `U0U1PinnedHarness` 的 U5 离线固定场景已直接使用生产
`CombatBeamSolver.ReplayDiagnosticPrefix` 和
`ShadowFutureStateFingerprint.Capture` 执行两种顺序：

- `BASH → STRIKE_IRONCLAD`：enemy HP = **39**，future fingerprint =
  `7D7857814B038E3D:4A6951B304DF0382`
- `STRIKE_IRONCLAD → BASH`：enemy HP = **42**，future fingerprint =
  `93D0F278205774EB:8DB470E2DA385E5E`

两顺序都能从同一固定 0.107.1 根合法完成；Bash 先施加 Vulnerable 后再攻击产生了
更低的敌方剩余 HP，且完整 modeled future fingerprint 不同。因此该反例证明生产 F
实际保留动作顺序影响，`OrderSensitive` 不能 exact-collapse。证据级别为
`pinned_offline_production_replay`，明确记录
`RealMultiplayerOwnershipVerified=false`：它验证模拟/顺序语义，不冒充真实远端玩家所有权或网络时序。

对应 pinned run `35892680392`：**SUCCESS**，并继续通过 U0/U1、U2、P0/P1
runtime 和历史 P0 A/B 分类。

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

### U5 非实机顺序语义 pinned replay

GitHub Actions run `35892680392`：**SUCCESS**；compatibility run `35892680315`：**SUCCESS**。

在固定 0.107.1 Ironclad / Fuzzy Wurm Crawler 场景中，新增 harness 直接通过生产 `CombatBeamSolver.ReplayDiagnosticPrefix` 分别推进：

- `BASH → STRIKE_IRONCLAD`：enemy HP = 39，future fingerprint = `7D7857814B038E3D:4A6951B304DF0382`；
- `STRIKE_IRONCLAD → BASH`：enemy HP = 42，future fingerprint = `93D0F278205774EB:8DB470E2DA385E5E`。

因此已由真实 pinned 游戏 DLL + 生产模拟器确认：Vulnerable/attack 换序会产生不同完整未来状态，且 `OrderSensitive` 不允许 exact collapse。该 run 同时完整通过 Release、U0/U1、U2、P0/P1 与历史 P0 A/B 回归。

此测试仍是单进程 detached simulation。它验证生产状态转移和顺序敏感性，不验证真实双玩家 ownership、网络同步、远端 WorldVersion 到达时序；证据中显式记录 `RealMultiplayerOwnershipVerified=false`。

### U5 非实机终局顺序 pinned replay

GitHub Actions pinned run `35893825974`：**SUCCESS**；compatibility run `35893826085`：**SUCCESS**。

同一固定 0.107.1 场景把敌人生命设为 7，再复用同一组生产 `PlanAction`：

- `BASH → STRIKE_IRONCLAD`：Bash 先结束战斗，生产 replay 在第二动作前按“终局后动作”边界拒绝旧后缀，`TerminalForwardRejected=true`；
- `STRIKE_IRONCLAD → BASH`：两动作均合法，最终 enemy HP = 0、energy = 0，`TerminalReverseCompleted=true`。

这证明了“一个顺序提前终局，另一个顺序仍需继续动作”的合法性不对称会被生产 replay 保留，不能把两种顺序当成可交换路线。该测试仍为 detached 单进程 pinned simulation；它不证明真实远端玩家触发死亡、网络同步或 `WorldVersion` 到达时序。

同一 pinned run 继续完整通过 Release、U0/U1、U2、P0/P1 runtime 与历史 P0 A/B 分类，且未扩大任何搜索预算。

## 仍未验证

真实双端 Host/Client 的 U5 专项 smoke 尚未执行，因此不能声称网络实机已经观察到 forecast boundary。

最小实机补测只需要三类：

1. 本地先施加 Vulnerable，队友攻击，再由本地继续攻击；确认远端动作出现时旧后缀不被直接部署，而是 fresh replan。
2. 一个顺序会先击杀/触发死亡效果，另一个顺序不会；确认日志中的 `reverse_order` 为 `OrderSensitive` 或 `ReverseUnavailable`，不显示 `order_collapsible=true`。
3. 能影响共享 RNG、抽牌或资源的等价测试对；确认不同后态不会被 exact transposition 合并。

该 runtime debt 不阻止 U5 代码阶段收口；进入 U6 前做最终默认迁移/实机闭环时再集中验证。
