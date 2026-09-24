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

对应 compatibility run `35892680315`：**SUCCESS**；pinned run `35892680392`：**SUCCESS**，并继续通过 Release、U0/U1、U2、P0/P1 runtime 和历史 P0 A/B 分类。

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

### U5 非实机终局顺序 pinned replay

GitHub Actions pinned run `35893825974`：**SUCCESS**；compatibility run `35893826085`：**SUCCESS**。

同一固定 0.107.1 场景把敌人生命设为 7，再复用同一组生产 `PlanAction`：

- `BASH → STRIKE_IRONCLAD`：Bash 先结束战斗，生产 replay 在第二动作前按“终局后动作”边界拒绝旧后缀，`TerminalForwardRejected=true`；
- `STRIKE_IRONCLAD → BASH`：两动作均合法，最终 enemy HP = 0、energy = 0，`TerminalReverseCompleted=true`。

这证明“一个顺序提前终局，另一个顺序仍需继续动作”的合法性不对称会被生产 replay 保留，不能把两种顺序当成可交换路线。该测试仍为 detached 单进程 pinned simulation；它不证明真实远端玩家触发死亡、网络同步或 `WorldVersion` 到达时序。

同一 pinned run 继续完整通过 Release、U0/U1、U2、P0/P1 runtime 与历史 P0 A/B 分类，且未扩大任何搜索预算。

### U5 非实机共享生成 RNG pinned replay

GitHub Actions pinned run `35935837066`：**SUCCESS**；compatibility run `35935837095`：**SUCCESS**。

在同一固定 0.107.1 根中向手牌加入两张真实游戏卡，并分别通过生产 `ReplayDiagnosticPrefix` 执行：

- `INFERNAL_BLADE → DISTRACTION`：最终额外生成 `DISMANTLE`、`TRUE_GRIT`，future fingerprint = `5EB1604F1125EBD8:7D84CF916EF7DFAB`；
- `DISTRACTION → INFERNAL_BLADE`：最终额外生成 `PRIMAL_FORCE`、`UNRELENTING`，future fingerprint = `C1076280507FB268:9AC9FCA14C304F1F`。

两张牌都通过生产镜像消费同一 `CombatCardGeneration` RNG 流；两顺序最终手牌 multiset 不同（`GenerationHandMultisetDifferent=true`），且完整 future fingerprint 不同。因此共享生成 RNG / 手牌后态的顺序影响已由 pinned 游戏 DLL + 生产模拟器直接验证，不能 exact-transposition 合并。

该测试仍为 detached 单进程 simulation：它验证共同状态转移 F 中的 RNG 与牌堆语义，不验证真实远端玩家 ownership、网络事件到达顺序或 `WorldVersion`。

同一 pinned run 完整通过 Release、U0/U1、U2、P0/P1 runtime 与历史 P0 A/B 分类，没有扩大 Beam、节点或时间预算。

### U5 非实机资源顺序 pinned replay

GitHub Actions pinned run `35936767565`：**SUCCESS**；compatibility run `35936767527`：**SUCCESS**。

在同一固定 0.107.1 根中把本地能量设为 1，并注入真实 `OFFERING`：

- `OFFERING → BASH`：生产 replay 合法完成，Offering 先改变资源状态，使后续 Bash 可执行；最终 energy = 1，`ResourceForwardCompleted=true`；
- `BASH → OFFERING`：生产 replay 在第一动作直接拒绝 Bash，诊断明确为 `energy=1 cost=2`，`ResourceReverseRejected=true`。

因此资源变化造成的动作合法性顺序依赖也已由 pinned 游戏 DLL + 生产 replay 直接验证。一个顺序中先获得资源后可继续，反向顺序则连第一步都不合法，不能视为可交换路线。

该测试仍为 detached 单进程 simulation；它验证资源状态与出牌合法性，不验证真实远端 ownership、网络时序或 `WorldVersion`。

同一 pinned run 完整通过 Release、U0/U1、U2、P0/P1 runtime 与历史 P0 A/B 分类，没有扩大搜索预算。

### U5 非实机抽牌/牌堆顺序 pinned replay

GitHub Actions pinned run `35937892644`：**SUCCESS**；compatibility run `35937892578`：**SUCCESS**。

固定 0.107.1 根的抽牌堆顶部两张为 `DEFEND_IRONCLAD`、`STRIKE_IRONCLAD`。向手牌加入真实 `POMMEL_STRIKE` 与 `HAVOC` 后，通过生产 replay 比较：

- `POMMEL_STRIKE → HAVOC`：Pommel Strike 先抽走顶部 Defend，Havoc 随后自动打出并消耗下一张 Strike；最终手牌保留 Defend，Exhaust 为 `STRIKE_IRONCLAD`，future fingerprint = `D0C9E5CB263AF5ED:1427A63872339424`；
- `HAVOC → POMMEL_STRIKE`：Havoc 先自动打出顶部 Defend，Pommel Strike 再抽到下一张 Strike；最终手牌保留 Strike，Exhaust 为 `DEFEND_IRONCLAD`，future fingerprint = `D79C9EEBE1B22A1F:B06C41B99F93102B`。

`DrawPileStateDifferent=true`。因此抽牌改变后续牌堆顶、自动出牌对象和最终 pile state 的顺序依赖已由生产模拟器直接验证，不能 exact-transposition 合并。

第一次测试提交因 fixture 把 nullable `CombatId` 直接赋给 `uint`，在 harness 编译阶段失败；修正为显式 fail-closed 的非空 CombatId 后，上述最终 run 全绿。该早期编译失败不计验证证据。

该测试仍是 detached 单进程 simulation，不验证真实远端 ownership、网络到达时序或 `WorldVersion`。同一最终 pinned run 完整通过 Release、U0/U1、U2、P0/P1 runtime 与历史 P0 A/B 分类。

## 仍未验证

真实双端 Host/Client 的 U5 专项 smoke 尚未执行，因此不能声称网络实机已经观察到 forecast boundary。

Vulnerable/attack、提前终局合法性不对称、共享生成 RNG/手牌后态、资源合法性、抽牌/牌堆顶变化五类顺序语义已经有 pinned production replay，不再要求用随机联机牌局重复证明。U6 最小 U5 实机补测收缩为一条网络链：让真实队友动作插入两个本地动作之间，确认远端 `WorldVersion` / live state 变化使旧条件后缀失效，部署不会跨过 forecast observation，并触发 fresh capture/search；同时确认旧 generation 不会继续提交。该链只验证离线无法提供的 ownership / network timing / observation→replan 层。

该 runtime debt 不阻止 U5 代码阶段收口；进入 U6 最终实机闭环时集中验证。
