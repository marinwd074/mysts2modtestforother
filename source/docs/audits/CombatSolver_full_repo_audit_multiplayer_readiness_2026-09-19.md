# CombatSolver 全仓多人就绪度审计

审计日期：2026-09-19
源码基线：`97a5550` (`fix combat ID and root intent replay mismatches`)
当前版本：`0.40.2`
目标游戏 / RitsuLib：`0.107.1` / `0.6.2+`
审计范围：`source/src`、`source/docs`、测试与 CI、构建配置、Replay/Checkpoint、网络相关代码

## 结论

**多人功能就绪度：RED / 不具备。** CombatSolver 当前是单人战斗求解器，不存在可交付的多人求解协议、玩家间状态模型、网络动作权威或多人 Replay/Checkpoint 格式。把 `MultiplayerScalingModel` 等原生对象复制到模拟状态，只解决对象隔离，不等于实现多人语义。

**单人与多人共存的失效关闭：基本成立，但尚未达到可证明的安全门槛。** 自动回合入口、回合开始选牌接管和 Instant 模式会主动跳过网络多人；然而控制器的若干公共/内部执行面没有在最前面统一检查 `IsMultiplayerSession`，而且 `CanExecuteCurrentTurn` 也不检查多人会话。应在继续宣称“多人中完全 inert”前补充统一门禁和回归测试。

本审计完成的是准备度判断，不是把产品改造成多人模式。当前正确的发布边界仍是：**多人模式明确拒绝，不能搜索、部署或接管；单人模式继续按现有合同运行。**

## 判定口径

| 能力要求 | 判定 | 证据与含义 |
|---|---|---|
| 明确支持范围 | PASS | `source/README.md:3,77-79,136`、`source/AGENTS.md:9` 明确写明单人；未知语义不应被当成已支持。 |
| 识别网络多人会话 | PARTIAL | `SolverController.IsMultiplayerSession` 在 `source/src/Runtime/SolverController.cs:121-127` 可识别 host/client；自动入口和部分补丁使用了它，但控制器所有执行面没有统一使用。 |
| 自动搜索失效关闭 | PASS（自动路径） | `source/src/Runtime/Entry.cs:78-146` 在回合开始和延迟回调都复核多人状态；`PlayerTurnSetupPatches.cs:256-268`、`CombatInstantModePatch.cs:72-77` 也拒绝多人接管。 |
| 手动搜索/部署/全自动失效关闭 | PARTIAL / P1 | `RequestDeploy` 先处理回合准备接管再到 `CanSolve`（`SolverController.cs:700-732`）；`SetFullAuto` 先处理 `CanTakeOverTurnSetup`；`ApplyCurrentTurn` 没有多人门禁。需要最前置 guard 和状态切换回归测试。 |
| 多玩家状态快照与 Continuation | BLOCKER | 生产代码大量使用 `LocalContext.GetMe`、单一玩家资源和 `Players[0]`；`PreCombatLiveStateSnapshot.cs:44-59` 与 `PreCombatForecastApi.cs:275-286` 明确要求玩家数为 1。 |
| 多玩家回合与动作权威 | BLOCKER | 仓库没有 solver 自有 lockstep、host authority、客户端动作信封、回滚或同步传输；当前部署逻辑直接消费本地 `Player` 和原生 ActionQueue。 |
| 多人专属内容与缩放 | BLOCKER | `GeneratedCombatScenario.cs:93-135` 过滤 `MultiplayerOnly` 卡牌并拒绝 `MASSIVE_SCROLL`；模拟器对 scaling model 的处理是 detached clone，不是多人效果/目标/资源语义实现。 |
| Replay / Checkpoint / Showcase | BLOCKER | `CheckpointArchive.cs:137` 返回 `requires_single_player`；`CombatReplayRecording.cs:79` 仅在一名玩家时创建记录；`CombatShowcaseRuntime.cs:66-74` 强制 `SetUpSavedSingleplayer` 和 `NetSingleplayerGameService`。 |
| 遥测与在线副作用 | PASS（隔离） | `OnlinePresence.cs`、`RunStatistics.cs` 在多人中跳过在线/跑局活动；这些是遥测隔离，不是多人玩法支持。 |
| CI 与测试矩阵 | INCOMPLETE | 当前 CI 和 `source/tools/run-contract-tests.ps1` 覆盖合同、版本和单人负向用例，没有 host/client、延迟、断线、多人选择同步或多玩家确定性矩阵。 |
| 构建与网络依赖 | PASS（未实现） | `CombatSolver.csproj` 没有游戏网络传输包或自有 gameplay transport；这避免了“半接入”风险，但也证明不存在可验证的多人协议实现。 |

## 关键证据

### 1. 当前边界是单人，而不是隐藏的多人实现

- `source/README.md:40,63,79,136`：战前 API、安装要求和通用兼容范围均限定单人；多人模式不在通用兼容范围。
- `source/docs/ARCHITECTURE.md:69,119`：在线存在遥测隔离；战前快照只代表当前单人跑局。
- `source/docs/GENERATED_COMBAT_SCENARIOS.md:84`：生成场景遵循单人池，拒绝多人专属卡牌并验证实际跑局只有一名玩家。

这部分边界清晰，当前不应把“支持单人且多人拒绝”误写成“支持多人”。

### 2. 自动路径有保护，控制器门禁不完整

`Entry.OnTurnStarted` 及其延迟回调、回合准备选牌补丁和 Instant 模式都检查 `SolverController.IsMultiplayerSession`。这是正确的 fail-closed 方向。

但以下路径没有在入口统一先检查多人状态：

- `SolverController.CanExecuteCurrentTurn`（`source/src/Runtime/SolverController.cs:108-118`）只检查战斗状态、结果时间戳或回合准备接管状态。
- `SolverController.RequestDeploy`（同文件 `:700-732`）先调用 `TryContinuePlannedChoice`、处理回合准备，再调用仅检查 `Players.Count != 1` 的 `CanSolve`。
- `SolverController.SetFullAuto`（`source/src/Runtime/SolverController.Automation.cs:14-82`）先处理 `CanTakeOverTurnSetup`，再调用 `CanSolve`。
- `SolverController.ApplyCurrentTurn`（同文件 `:229-250`）直接向活动搜索/回合准备协调器发出接管请求。
- `PlayerTurnSetupCoordinator.CanTakeOverTurnSetup` 和 `TryContinuePlannedChoice`（`source/src/Runtime/PlayerTurnSetupPatches.cs:437-470`）自身不复核多人状态。

`CanSolve` 当前确实拒绝 `state.Players.Count != 1`（`SolverController.cs:1363-1382`），但“玩家数为 1 的网络/过渡态”与 stale search/turn-setup 状态仍不能仅靠玩家数证明安全。`ResetCore` 会清理搜索和战斗会话（`SolverController.cs:1040-1085`），但没有证据表明 NetService 类型发生切换时总能先于所有 UI/control 查询完成清理。因此该项应视为覆盖缺口，而不是已证明可利用漏洞。

### 3. 状态、目标与资源模型不是多人模型

生产运行时和搜索快照以本地玩家为主角：

- `CombatRootSnapshot.cs` 使用 `LocalContext.GetMe(state)`；ContinuationStamp 只保存单一玩家的生命、格挡、能量、星能、金币、牌堆、药水和遗物等关键 continuation 字段。
- `RootCombatTransformationPoolSnapshot.cs` 对无色变形池使用 `players[0]` 的解锁状态。
- `SimulatedCombatState.Potions.cs` 虽有按 NetId 排序的多玩家分支，但文件注释同时明确当前唯一支持模式仍是单人；这是未来兼容形状，不是完整多人合同。
- `SimulatedCombatState` 对 `MultiplayerScalingModel` 做 detached clone 并清空 live run/combat 引用，能避免模拟污染原生对象，但没有定义共享敌人、队友资源、玩家目标、选择广播和每玩家随机性。

### 4. 持久化与离线能力明确拒绝多人

Checkpoint、Replay 和 Showcase 都依赖单人恢复路径。离线 headless 文档也规定请求串行化、实例隔离和共享 host 协调，并没有多人可见窗口或客户端同步测试合同（`source/docs/HEADLESS_TESTING.md:6,38`）。因此不能用现有 headless 证据替代多人运行时证据。

### 5. 网络相关代码是遥测/原生集成，不是 gameplay transport

仓库中出现的 `HttpClient` 主要位于 `OnlinePresence`、`RunStatistics`、问题包/展示上传和更新检查；未发现 solver 自有 `TcpClient`、`UdpClient`、WebSocket、Steam Networking、NetworkStream、lockstep、rollback 或 host-authoritative action protocol。`RunManager.NetService`、`ActionQueueSynchronizer`、`PlayerChoiceSynchronizer` 是对游戏原生网络/选择设施的接入，不构成 CombatSolver 的多人协议。

## 风险与后续顺序

### P0：继续保持明确拒绝，不开放多人自动化

在有正式多人设计和运行时证据前，保留单人硬门槛；不通过“玩家数暂时为 1”、fake multiplayer、单次回合或本地 host 推断可安全部署。

### P1：补齐统一失效关闭门禁

增加一个可审计的 session capability / guard，并在 `CanExecuteCurrentTurn`、`RequestSearch`、`RequestDeploy`、`SetFullAuto`、`ApplyCurrentTurn`、回合准备协调器和部署回调的入口与异步恢复点复核。NetService 从单人切换到多人时，应取消搜索、清空可部署结果、撤销 turn-setup takeover，并记录拒绝原因。至少需要单元/合同用例证明：多人 host、多人 client、玩家数为 1 的网络过渡态、切换时有活动搜索、切换时有活动部署均不会产生 solver action。

### P2：先确定产品权威模型，再拆状态

在实现前明确是“仅 host 求解并由 host 发布动作”，还是“每个玩家只求解自己的回合”。随后定义玩家作用域、动作信封、权限拒绝、选择同步、断线/重连、回滚和 RNG/seed 规则；不能继续复用单一 `LocalContext.GetMe` 作为隐式多人协议。

### P3：重建多人状态、内容和持久化合同

需要覆盖每玩家生命/能量/手牌/牌堆/遗物/药水/选择、共享敌方状态、队友目标、多人专属卡牌约束、`MultiplayerScalingModel` 语义、NetId 稳定排序、第三方效果以及多人 Replay/Checkpoint/Showcase schema。所有字段必须进入 fingerprint、continuation、结果时间戳和 stale-result 校验。

### P4：建立最小可重复的多人验证矩阵

至少覆盖：两玩家 host/client、双方轮流回合、同时/连续玩家选择、延迟、动作拒绝、加入/离开/断线、战斗结束、重连、确定性 RNG、多人专属内容、第三方 Mod、搜索取消与回滚。CI 先运行不超过 10 个高价值合同测试；扩展测试数量前按仓库规则取得批准。

## “多人就绪”前的验收条件

以下条件全部满足后，才能把结论从 RED 改为可评估状态；通过其中一项不能宣称支持多人：

1. 公开文档、安装/兼容矩阵和运行时能力标识对 host/client 支持范围一致。
2. 所有搜索、接管、部署、异步回调、Replay 恢复和 UI control 都绑定明确的玩家作用域及会话代数。
3. 有定义并实现的动作权威、选择同步、断线/重连、RNG/seed、错误拒绝和 stale-result 规则。
4. 多人状态能完整序列化、指纹化、Fork、Continuation、Replay 和 Checkpoint 恢复；不再依赖 `Players[0]` 或单一 `GetMe` 隐式代表全局状态。
5. 多人专属卡牌、遗物、缩放、共享目标和第三方效果有明确的支持/拒绝登记，并由测试覆盖。
6. host/client headless 或可见游戏证据覆盖上述矩阵，且 CI 能稳定复现关键合同。
7. 多人模式下的遥测、问题包和隐私边界经过单独确认，不把网络会话数据误当单人活动。

## 文档质量备注

`source/docs/COMBAT_HOOK_COVERAGE.md` 的标题仍标为 CombatSolver `0.38.2` / 游戏 `0.111.0`，而本审计基线是 `0.40.2` / 游戏 `0.107.1`。该文件可作为历史 Hook 资料，但其中的多人 Hook 数量不能作为当前就绪度证据；后续应补版本标识或明确“历史报告”。

## 本轮验证范围

本轮是文档/仓库审计，按项目规则未重新构建、未启动游戏、未复制 DLL，也未声称完成多人运行时 smoke。已核对当前源码、配置、文档、测试入口和 CI；发布前仍需按上面的 P1/P4 条件补充运行时证据。
