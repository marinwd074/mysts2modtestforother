# Codex 当前交接

## 当前技术状态

### 2026-09-22 TheBookOfAges 完整 UI 测试集成

- 用户确认已取得上游作者许可；完整 GM Console UI、图片、本地化、Services、GameActions 及多人网络同步实现作为 test-only submodule 接入 `source/tools/multiplayer-lab/MultiplayerTestTools/TheBookOfAges`。
- 固定上游 commit `234a74ccbaf46d7e385ed318c64857f1f7a90cae`；该模块不引用进 CombatSolver 正式 csproj/solution、不进入发布包，只服务 Multiplayer Lab，未来可整块删除。
- 旧的 `install-the-book-of-ages.ps1` 与 `install-shared-console-mod.ps1` 已删除。后续直接从已集成 test-only 模块构建/部署到各测试端，不再维护 Workshop/外部 Console Mod 安装路径。
- 下一步先验证所有端使用同一 GM Console 构建后，最小加牌/资源修改是否仍触发 game-data mismatch；同步稳定后再用于 Tag Team / MultiplayerOnly runtime fixture。

### 2026-09-22 旧测试控制台清理

- CombatSolver 旧的单 Client 状态注入链路已经整组删除：Runtime runner、启动参数/环境变量、fixture JSON、validator/self-test 和 Tag Team 专用观察补丁均不再保留。
- Multiplayer Lab 后续统一使用独立 test-only `TheBookOfAges / GM Console` 模块构造测试状态；CombatSolver 正式 Runtime 不再内置调试控制台注入。
- 历史上“只在一个 Client 注入 debug 状态”会造成 game-data mismatch，因此不要重新引入该路径。

### 2026-09-22 Safe Auto 本机三回合回归

- 当前 `main` 的 Release 构建为 0 warning / 0 error；完整 CI 门禁 `PASS: 27 FAIL: 0 SKIP: 0`。本机 `sts2.dll` SHA-256 与 pinned 0.107.1 值一致。
- 固定 `HostVanilla + ClientCombatSolver`、Steam transport off、Client warm-up 后第二次启动的真实战斗中，用户只开启一次“安全自动”；正式 combat journal 记录本地回合 1/2/3 分别使用新 request 1/2/3、新 route generation 1/2/3，各有 3 次原生 `PlayCardAction` 和 1 次原生 `EndPlayerTurnAction`。每次 EndTurn 后重新 Probe/capture/search，未见第二次手动 Execute、远端中止或自定义网络路径。`validate-safe-auto-results.ps1` 返回 `MULTIPLAYER_SAFE_AUTO_PASS`；两个实例均 Graceful stop。摘要见 [`safe-auto-runtime-2026-09-22.json`](multiplayer/evidence/safe-auto-runtime-2026-09-22.json)。
- Axebot `AXEBOTS_NORMAL` 求解问题为 **PASS（用户实机确认）**：用户在修复后的实际战斗中确认已能正常求解、旧问题已解决。本次未核验该次 combat journal，因此不单独声明日志级 root capture、搜索完成或合法路线合同 PASS；Boot Up Strength 数值 differential 仍为 `UNVERIFIED`。MultiplayerOnly 的 `multiplayer_only_card` 分类仍有静态合同；未构造稳定的最佳路线触边场景，runtime 停止行为仍为 `UNVERIFIED`。

### 2026-09-22 pinned monster static-member guard 完成

- Axebot 修复后，新增 `Sts2LocalInspector --monster-static-source`，直接解析 `MonsterMoveEffects.StaticValues.cs` 并对 hash-pinned 0.107.1 `sts2.dll` 元数据逐项验证怪物类型及 field/property。
- pinned workflow Run `35691169998` 已通过真实 DLL 校验：SHA-256 `a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52`，monster move IL 933 methods，static-member guard **35 types / 51 members PASS**。
- workflow 现在在 `MonsterMoveEffects.StaticValues.cs`、`MonsterValueReader.cs` 或 inspector 变化时自动触发；普通 inspector self-test 也覆盖清单解析器。该 guard 只证明成员存在，不替代 move 数值公式 IL 审计。
- 当前代码基线包含 `efaa9682`、`614f8e93`、`01ee3a72`。Axebot `AXEBOTS_NORMAL` 后续已由用户在实际战斗中确认可以求解；本轮未独立核验 journal。

### 2026-09-22 Axebot 0.107.1 runtime 问题包修复

- 用户问题包 `fcca6db5-cd96-42d4-830b-bc9b7e0112f4.zip` 在 `AXEBOTS_NORMAL` 的 AutoTurnStart 根捕获阶段稳定报 `MissingMemberException: Axebot.RespawnCount not found`，因此没有生成路线；replan 计数也保持 0，属于 search setup failure，不是 continuation/replan 问题。
- pinned 0.107.1 DLL IL 已核对 `Axebot.<BootUpMove>d__30.MoveNext`：原生 Strength 为 `BootUpStrGain * (2 - StockAmount)`，调用 `get_StockAmount`；不存在 `RespawnCount` 成员。
- 已把静态根捕获从 `RespawnCount` 改为 `StockAmount`，并同步修正 BOOT_UP_MOVE 公式。合同脚本现在同时拒绝任何 Axebot `RespawnCount` 回归并锁定 `2 - StockAmount`。
- 该问题包的错误发生在战斗 root snapshot 物化。用户现已在修复后的 Axebot 实际战斗中确认可以求解；没有本次 journal 或 Boot Up 数值 differential 证据。

### 2026-09-22 pinned monster-target runtime 检查点

- `b924fa28` compatibility CI 已 PASS。pinned 0.107.1 monster target 审计的 63 个可确定建模 move 已完成多人 fanout：52 个 simple、9 个 owner-once split，以及 Thieving Hopper / The Insatiable 两个 phase-sensitive RNG 路径。
- 唯一剩余的 `KnowledgeDemon.CURSE_OF_KNOWLEDGE_MOVE` 继续保持 `NeedsRemoteChoiceFailClosed`。pinned IL 已确认：每名 living player 同时建立独立 `BlockingPlayerChoiceContext`，所有 `ChooseCurse` 共用同一个 counter，只有 `Task.WhenAll` 完成后 counter 才 +1。
- 现已修正此前“本地 Choice 解决后可能继续搜索、从而漏掉远端 Choice”的安全缺口：多人 Knowledge Demon 不再先结算本地诅咒，而是创建明确的 uncontrolled-remote-choice 状态；Snapshot 将其映射为 `UnsupportedEffect`。求解器不会为远端玩家选择诅咒，也不会把远端选择当成可优化分支。
- 单人 Knowledge Demon 选择路径保持原逻辑。该改动只收紧多人预测边界，不扩大 `RootActionPlayers`、不新增网络协议、不声明 Host/Client runtime PASS。
- monster target fanout 到此停止扩张。下一步转回 Multiplayer Safe Auto / multiplayer-only card 的真实 runtime 门禁；若未来要支持 Knowledge Demon，必须先设计“不可控远端 choice uncertainty”语义，而不是普通搜索 Choice branching。

### 2026-09-22 readable-state 审计检查点

- 当前 HEAD：`9115977e9317087afc933e9919ab2c196de77d8b`。
- `a834ee4a` 已撤销 `282393c9` 的 post-yield stale-read 方案。原因已确认：`CombatPredictionState.Fork()` 会重新 Attach，Attach/Hook materialization 会再次读取全部 `RootCapturedPlayers`；“本地 EndTurn 后任何队友状态读取都判过期”会无条件误杀合法 T2/T3 本地跨回合路线。当前正确模型恢复为：队友状态在一次搜索内是冻结 root 快照；`RootActionPlayers` 仍只有本地玩家；真实后续回合重新采集 teammate fingerprint，有变化就 Fresh Search。
- `a834ee4a` GitHub Actions 已完成：`static-consistency PASS`、`contract-tests PASS`。这不是完整游戏 Release build；完整构建仍依赖本机 STS2/RitsuLib。
- `9115977e` 补齐队友牌 fingerprint：除牌 ID/升级/附魔/异常/对象身份外，现在纳入本地费用、星能费用、Replay、Exhaust/Sly/Retain、DeckVersion/removed 标记、语义 DynamicVars，以及 Claw/Genetic Algorithm/Maul/Mad Science/Rampage/The Scythe 等已知私有语义计数。对象身份仍保留，因为原生 `NetCombatCardDb` 同样按战斗内稳定的 `CardModel` 实例映射网络卡牌 ID。
- 保存本检查点时，`9115977e` 的 GitHub Actions Run `35684582193` 尚在运行，不能写成 PASS。
- 下一步只做两件事：① 等/查 `9115977e` CI；② 继续审计 readable root 是否还有字段未进入 teammate continuation fingerprint，以及是否存在 `RootCapturedPlayers` 被误用于动作推进的调用点。不要重新引入“post-yield 读取即过期”。

- **2026-09-22 readable-state root 边界更新**：多人搜索 root 允许捕获本地进程已经物化的队友战斗状态，但 `RootActionPlayers` 仍严格只有本地玩家。队友状态在一次搜索内是**冻结 root 快照**：搜索不会生成队友动作，也不会把读取权限升级成控制权；未来真实回合到来时，continuation 会重新采集本地可读队友 fingerprint，任何变化都拒绝旧路线并 Fresh Search。`282393c9` 曾尝试把本地 EndTurn 后的队友 `PlayerCombatState` 立即标为过期，但该机制会在预测 Fork 的内部重新 Attach 时误触发并截断 T2/T3，本轮已撤销。旧交接中“remote private 一律不可读 / local-player-only root”的描述属于历史实现，不再作为当前架构事实。
- CombatSolver `0.40.2`；目标游戏 / RitsuLib `0.107.1`；兼容符号 `STS2_01071`。
- MP-0 Core / lifecycle：PASS。
- MP-1 Advisor：受控 Smoke PASS。
- MP-2A：显式 `safe-execute`/`safe-execute-lab` 的一动作 Host/Client Smoke 已 PASS，作为历史基线保留；该证据不等于两动作能力已通过。
- MP-2B：两动作上限、显式 SafeExecutionSession、动作后稳定世界等待和本地/远端变化重验证已完成；历史 `MultiplayerSafeExecuteChecks` 39 项、正常/远端干扰验证器合成回归和 Release 构建已通过，真实正常两动作与远端干扰 Smoke 也已分别返回 PASS。摘要见 [`mp2b-smoke-2026-09-20.json`](multiplayer/evidence/mp2b-smoke-2026-09-20.json)。
- MP-2C：已直接将 MP2B 泛化为当前回合 bounded N-action，policy ceiling 为 6；当前合同 40 项、正常/干扰验证器各 6 个合成用例和 Release 构建已通过。真实正常 Smoke 自动完成 3 张牌，真实远端干扰 Smoke 在完成 2 张后中止并重新搜索；摘要见 [`mp2c-smoke-2026-09-20.json`](multiplayer/evidence/mp2c-smoke-2026-09-20.json)。
- Multiplayer Carry Ranking v1：已接入主线程捕获的公开远端/敌人上下文、纯确定性 evaluator 和最终路线排序 tie-break；11 项 Multiplayer Carry Ranking 离线合同与 Release 构建通过。仅显式 Advisor/Safe Execute 使用，默认 Probe/单人保持原排序；远端私有状态、队友行为预测和未知敌方目标均保持 fail-closed/neutral。运行时目标分类已补齐：仅原版怪物的公开 AttackIntent 按 0.107.1 原生 `AttackCommand.FromMonster → TargetingAllOpponents` 合同标为 `AllPlayers`，非攻击/第三方怪物保持 `Unknown`；root 日志已增加机器可读 `all_player_threats/unknown_threats`，并新增 R1/R2 validator 与 CI 合成自测。R1 已在 `SLIMES_WEAK` 真实 Advisor journal 中 PASS（1 名远端、3 敌人、2 个 `AllPlayers` threat、1 个 Unknown，remote private=false）；R2 当前保持 `UNVERIFIED`，但不再作为阶段阻塞项。`f54506c3` 已完成真实回归：Release 0 errors（2 warnings）、11/11 Carry contracts、validator self-test 全 PASS；`carryWindow=current_turn_pre_end` 与 `carryObservationActionCount=3` 对齐第一处 EndTurn，且 `futureOnlyKillCredited=false`。两次真实 `SLIMES_WEAK` fixture 中威胁 `TWIG_SLIME_S` 搜索时仍为 15 HP，而当前 T1 只有两张 `Strike=6`、`Bash=8` 在 T2 才抽到，因此没有形成当前回合可击杀的等价 tie，R2 正确返回 UNVERIFIED。决定性 flip 继续由第 10 项纯合同保证；真实 R1 + 当前窗口回归已证明 runtime 接线与安全语义。后续仅在自然出现合适 fixture 时补 R2 decisive runtime evidence，不再人工刷场景。
- Multiplayer Carry v2 / Safe Auto foundation：`4275aff` + `7b9f634` 已落地。新增独立 `MultiplayerSafeAutoEnabled`，不复用单人 `FullAutoEnabled`；仅显式 `MultiplayerSafeExecute` 会话可开启。搜索完成后只由 Safe Auto 或一次性 Execute 授权触发部署；每次部署仍新建 `MultiplayerSafeExecutionSession`，Safe EndTurn 后旧 request/session/route authorization 继续清除，Safe Auto 状态本身可跨本地回合保留。Potion、Choice、teammate/unknown target 等不支持边界会停止 Safe Auto，6-action ceiling 允许继续 fresh search；用户手动 Stop Search / 关闭 Solver 同样会清理 Safe Auto。UI 在 Safe Execute 多人会话下显示“安全自动”。GitHub CI Run #435 曾验证 `MultiplayerSafeExecuteChecks=56 PASS` 与 Safe Auto validator 合成自测；真实 Host/Client 三回合结果见本文件顶部的 2026-09-22 记录。
- Multiplayer Lab snapshot：已改为 schema 2 的持久 base-game snapshot + profile overlay 增量同步。marker 拆分 `baseGameId`、`ritsuArtifactId`、`combatSolverArtifactId`；游戏版本/底座变化、底座完整性失败或旧 schema 才全量重建，overlay 采用临时 managed tree + SHA-256 + rename/rollback。CombatSolver/RitsuLib 变化分别只更新各自 payload，HostVanilla 不因 CombatSolver 构建变化重建；`prepare-instances` 输出 `FULL_REBUILD` / `OVERLAY_UPDATED` / `REUSED` 和 `copiedFiles`。ownership、no-reparse-point、运行中禁止覆盖和正式证据隔离合同保持不变。
- Multiplayer Instant、Potion、Choice、Replay、单人 Full Auto 和队友目标继续关闭；显式 Safe Execute 下已新增实验性 Safe Auto，但不扩大动作边界：仍只部署当前真实本地回合的安全本地普通牌与安全 EndTurn，每个本地回合重新授权；默认 Probe 不自动升级。
- 本轮没有为 MP2B 声明新的 GitHub Actions 结果；实机结论来自隔离 Multiplayer Lab 的 Host/Client journal 与对应验证器，不等同于 GitHub Actions 结果。

## 2026-09-21 Carry 当前回合窗口交接（新对话先读）

- 当前仓库为 `main`，`HEAD` 与 `origin/main` 均为 `e07794f39ab0a2b5729ef1401fc9df46f6be611b`；本轮 Carry 验证基线 `f54506c34f5fbd470a2cb3c8cf4d07c21e869130` 已在当前历史中。
- 第一阶段已收口：Release `0 errors / 2` 条既有 `CS9113` warnings、Carry contracts `11/11 PASS`、validator self-test `PASS`。
- 当前回合窗口回归 `PASS`：`carryWindow=current_turn_pre_end`、`carryObservationActionCount=3`、第一处 EndTurn 为 action index `3`、`futureOnlyKillCredited=false`。这证明 EndTurn 前只观察当前回合动作，后续回合击杀不会被错误计入当前窗口。
- R1 继续保持真实运行 `PASS`。本轮 R2 仍为 `UNVERIFIED`，不是失败或阶段阻塞：主 journal 为 1 名远端、3 个敌人、1 个 `AllPlayers` threat、2 个 `Unknown` threat，`remote_private=false`；当前 T1 只有两张 `Strike=6` 和 `Defend`，T2 才抽到 `Bash=8`，所以没有形成当前回合可击杀的等价 tie。备用 fixture 也没有出现精确的 8 点当前回合攻击，未进行盲点操作。
- 关键证据：最终摘要 [`carry-ranking-current-window-final.json`](../../.local/multiplayer-lab/results/carry-ranking-current-window-final.json)；主 validator [`carry-ranking-current-window-r2.json`](../../.local/multiplayer-lab/results/carry-ranking-current-window-r2.json)；备用 validator [`carry-ranking-current-window-r2-alternate.json`](../../.local/multiplayer-lab/results/carry-ranking-current-window-r2-alternate.json)。主/备用 journal 均保留在各自 `runtime-mp-client-carry-window-r2/diagnostics/CombatSolver-BugReports/logs/CombatSolver/` 目录。
- 两个 Host/Client 已 Graceful stop；本轮未观察到 remote-private 泄漏、`SEARCH_SETUP_FAILURE`、自定义网络 API 或自动部署。Carry 验证本身未改源码；本次交接仅更新本文件。新对话不要重新启动游戏或人工刷 R2，先读本节与 `docs/multiplayer/RUNBOOK.md`，再选择下一个独立能力；只有自然出现精确 fixture 时才补 R2 decisive runtime evidence。

## 项目规则已放宽

当前按“项目主持人 / Tech Lead”方式工作：

- 仓库内、可逆、非发布性决策默认由 Agent 自主执行。
- 可以修复相关相邻问题、重构、补测试/诊断、清理死文件和读取必要历史，不需要逐项审批。
- 失败后可以自行切换安全替代方案。
- 不再强制一个请求一个 commit、固定测试数量、固定 120 秒上限、每轮 handoff、每次 Markdown 输出或禁止读取历史目录。
- 只在产品方向、不可逆删除、外部账号/费用、正式发布、公开协议/数据格式或正式多人能力范围变化时需要额外确认。
- 保留安全与正确性硬边界：不泄密、不破坏用户数据、不强推/改写历史、不擅自发布、不用性能预算掩盖语义错误、多人实验能力不在缺少实机证据时直接转正式。

## 多人实机固定操作（新对话不要重新试错）

- 任何 Multiplayer Lab 运行前先读 `docs/multiplayer/RUNBOOK.md`。
- 同机 Host/Client Lab 必须关闭 Steam transport。启动脚本现已默认 `--force-steam=off`；命令仍建议显式写 `-ForceSteamOff`。只有专门测试 Steam transport 才用 `-AllowSteam`。
- `ClientRitsuOnly` / `ClientCombatSolver` 的第一次启动是 Mod 加载 warm-up：让游戏加载 Mod 并重启一次；**重启后的第二次启动才是正式 Host/Join/Smoke 运行**。warm-up 不能作为 multiplayer evidence。
- Client snapshot 被重新准备/重建、Mod payload 被替换，或游戏再次要求重启时，重新做 warm-up。
- 正式测试结束继续使用 Graceful stop；不要用强杀运行当完整 journal 证据。

## 当前实验职责

- 游戏内所有 GUI 操作交给用户完成；Codex/Agent 不代操作游戏 GUI。
- 必须实机验证的任务由 Codex/Agent 驱动技术全流程：构建、prepare、启动/重启、warm-up、Graceful stop、日志定位、validator、证据分析和修复；不得把启动命令或进程管理反交给用户。
- 到达 GUI 节点时只告诉用户当前要点击/观察的一步；用户反馈后 Codex/Agent 继续余下流程。
- MP-2C 正常/干扰 Smoke 已收口，不为 Reactive Carry 重复同一 deployment 中途干扰证据。

## 本轮 MP-2A 实机证据（2026-09-20）

- 已按远程 `d48065b` 的 Runbook 构建当前 Release，并使用 `HostVanilla + ClientCombatSolver`、Steam transport off、Mod warm-up 后第二次正式 Client 运行。
- Host/Client 进入同一房间和战斗：MapCoord `(3,0)`、Encounter `NIBBITS_WEAK`、Seed `EJEBT4G7Y6`。用户确认点击前没有自动出牌。
- 用户只点击一次“执行本回合”：原生 `PlayCardAction` 打出 `STRIKE_IRONCLAD`；能量 `3 -> 2`、手牌 `5 -> 4`、弃牌堆增加 1 张，敌方生命 `96 -> 90`，UI 进入“等待下一回合”。
- `validate-mp2a-results.ps1` 返回 `MULTIPLAYER_MP-2A_PASS`，7 项检查全部 PASS：Safe Execute capability、单动作部署、原生 PlayCardAction、无自动 EndTurn/药水、动作后 WorldVersion 失效和新的 debounce search。审计摘要位于 `.local/multiplayer-lab/results/mp2a-summary-20260920-user.json`；原始 CombatSolver journal 位于本轮 Client 的 `diagnostics/CombatSolver-BugReports/logs/CombatSolver/` 下。
- 两个隔离实例均使用默认 `Graceful` 停止。以上 Lab 证据与本轮正式 token 证据均只收口单张本地普通牌边界；正式 token 运行的机器摘要见 [`mp2-safe-execute-formal-2026-09-20.json`](multiplayer/evidence/mp2-safe-execute-formal-2026-09-20.json)。
- 正式 token 运行使用 Seed `QPMEQDJ5AQ`、Encounter `NIBBITS_WEAK`；一次 `DEFEND_IRONCLAD` 原生 `PlayCardAction` 完成后，Probe 观察到能量 `3 -> 2`、手牌 `5 -> 4`、弃牌堆增加该牌。验证器返回 `MULTIPLAYER_MP-2A_PASS`，7 项检查全部 PASS。

## 本轮 MP-2B 实机证据（2026-09-20）

- 正常 Smoke：`HostVanilla + ClientCombatSolver`、Steam off、Mod warm-up 排除；同一 deployment 捕获两次本地原生 `PlayCardAction`，两次动作重验证通过，`end_turn=false`，验证器返回 `MULTIPLAYER_MP-2B_PASS`。
- 远端干扰 Smoke：三方 Host/Client 运行中，真实 `request_id=1` 在第一张牌后观察到远端公开变化，记录 `MP2B_REMOTE_DELTA_ABORT`，没有第二个 `NATIVE_ACTION_CAPTURED`，随后出现新的 debounce search；验证器返回 `MULTIPLAYER_MP-2B_REMOTE_ABORT_PASS`。
- 证据摘要：[`mp2b-smoke-2026-09-20.json`](multiplayer/evidence/mp2b-smoke-2026-09-20.json)。本轮三个 owned 进程均以 `Graceful` 停止。

## 本轮 MP-2C 实施状态（2026-09-20）

- `MaxActionsPerDeployment=6`，`TakeBoundedSafePrefix` 返回连续安全本地普通 `PlayCard` 前缀；第一个 Potion/Choice/EndTurn/Replay/远端或未知目标即硬停止，不跳过后续动作。
- 同一个 SafeExecutionSession 继续执行 `Authorized → Executing → AwaitingWorldUpdate → Revalidating → Authorized ... → Completed/Aborted`；每张牌等待原生队列完成、稳定 `WorldVersion`，然后做现有 post-action revalidation，未 reset/rebase world version。
- `validate-mp2b-results.ps1 -MinActions 3 -MaxActions 6` 和干扰校验器 `-MinCompletedActions 2 -MaxActions 6` 已支持本阶段；合成回归均 PASS。两次实机结果均已通过对应验证器，证据为 `multiplayer/evidence/mp2c-smoke-2026-09-20.json`。

## 本轮 Reactive Carry Foundation 状态（2026-09-20）

- Safe Execute 现在在 bounded safe local prefix 后识别路线中的真实 EndTurn；只有
  lifecycle、local turn、route/generation、queue、pending choice、local identity、
  accepted WorldVersion、稳定 world 和 dirty observation 全部重新通过时，才通过原生
  `EndPlayerTurnAction` 结束回合。
- EndTurn 接受后旧 `SafeExecutionSession`、deployment authorization、route seed 和
  continuation 清除；WorldVersion 不 reset/rebase。下一本地回合从 Fresh Probe/capture
  和 Fresh Search 重新建立，旧 request/route 不得部署。
- `MultiplayerSafeExecuteChecks` 已扩展到 53 项 PASS；新增 Reactive Carry validator
  的合成自测 PASS；Release、target-version、refactor-boundary 和完整 contract suite
  均 PASS。
- Smoke A PASS：request 1 / local turn 1，3 张牌后 native EndTurn，下一回合 fresh
  Probe/Search；日志中有后续尝试，摘要按 request scope 验证。
- Smoke B PASS：request 2 / local turn 1，2 张牌后 native EndTurn；EndTurn 后下一次
  fresh search 前观察到队友公开世界变化；同一日志中的早期干扰 request 被排除。
- Smoke C PASS：requests 1/2/3 对应 local turns 1/2/3，完成 3/3/2 张牌、三次 native
  EndTurn 和三次 fresh search；整份正式 journal 无 remote abort、旧授权复用或自定义
  网络 marker。机器摘要见
  [`reactive-carry-smoke-2026-09-20.json`](multiplayer/evidence/reactive-carry-smoke-2026-09-20.json)。

## 本轮 Multiplayer Local Cross-Turn Planning 实施状态（2026-09-20）

- 搜索策略明确区分 `SinglePlayerFullRoute`、`MultiplayerCurrentTurnOnly` 和 `MultiplayerLocalCrossTurn`；默认 Probe 不搜索，单人策略不变，显式 Advisor/Safe Execute 才允许本地跨回合预测。
- 路线可以包含本地 T1→T2→T3 预测，但 root 只捕获本地玩家私有状态；投影动作必须全部是本地动作，不建立队友手牌、牌序或行动模型。未知 root/Hook 语义继续 fail-closed。
- `ContinuationStamp` 已纳入战斗身份、本地身份、回合/阶段、资源、手牌/牌堆/RNG、敌人状态；另以远端公开 fingerprint、多人 scaling/牌约束及单调 `WorldVersion` 做严格续接门禁。精确续接保留 `RouteIdentity`，旧 SafeExecution authorization 已结束，当前回合重新创建 authorization；不匹配执行 Fresh Probe + Fresh Root + Fresh Search。
- 续接校验崩溃已在 `94c6497` 修复：T2 `AutoTurnStart` 在 `CaptureContinuationValidation` 前强制 fresh Probe，继续保持严格 `ActualWorldVersion > max(ExpectedSourceWorldVersion, MinimumWorldVersion)`；续接拒绝按 `cached_turn_missing`、`local_state_mismatch`、`world_version_not_advanced`、`remote_public_mismatch` 等原因记录，空 local diff 不再访问 `[0]`。
- Safe EndTurn 后未来路线只作为不可执行 continuation 保留，部署筛选仍只取当前本地回合；多人不读写 `SolvedRouteCache`，避免持久缓存携带多人完整状态。
- `MultiplayerLocalCrossTurnChecks` 12 项、Release 构建（0 errors、2 条既有 `CS9113`）和 `git diff --check` 已通过；新增合同要求 `MultiplayerLocalCrossTurn` 的未来本地回合在首次依赖共享 `Shuffle RNG` 前停止投影，root 当前回合和单人/CurrentTurnOnly 策略不受影响。新 T3 Fix Client 正式启动确认 63 个补丁加载成功；T3 复测在 `X7TJJK1T4W` / `NIBBITS_WEAK` 中通过：generation 3 的 route `140e6e10fd494b01a05287a5d478da25` 保留 `DEFEND_IRONCLAD`、两张 `STRIKE_IRONCLAD` 和 EndTurn，随后 request 3 以新授权执行原生动作。证据见 [`local-cross-turn-t3-fix-smoke-2026-09-20.json`](multiplayer/evidence/local-cross-turn-t3-fix-smoke-2026-09-20.json)。
- T3 运行同时确认正常路径没有回归：T3→T4 出现 `SEARCH_REUSED` 与 `MP_LOCAL_XTURN_CONTINUATION_REUSED`，旧 authorization 已失效；`remote_public_mismatch`、`local_state_mismatch` 均按合同进入 Fresh Search，无 `SEARCH_SETUP_FAILURE`、越界、WorldVersion reset/rebase 或自定义网络包。
- 独立 X2 已收口：三人 `F23XG9KSJD` / `FUZZY_WURM_CRAWLER_WEAK` 中，Client 1001 以 `STRIKE_IRONCLAD` 改变公开敌方 HP `180→171`；旧 route `bb5892f2e2ff47e78ac0915a9f0013c9` 被拒绝，随后 Fresh Probe/Root/Search 生成 route `01216d8159a241949ba5dbe6cd8e0f71`，request 2 建立新 authorization 并执行新 T2 牌序，旧 future action 未入原生队列。证据见 [`local-cross-turn-x2-smoke-2026-09-20.json`](multiplayer/evidence/local-cross-turn-x2-smoke-2026-09-20.json)。
- Shared-RNG Shuffle Boundary 已实机收口：基线 `22050da` 的两人 Host/Client 运行中，T1 route `aa917965cd3f4569a03125db7034ec82` 保存 1 个 T2 continuation；T1→T2 以 `remote_public_soft_reuse` 精确复用并确认旧 authorization 失效。T2 EndTurn 后由于未来 T3 首次需要共享 `Shuffle RNG`，`continuation_pending=false`；真实 T3 以新 route `c1b69a31dd934fe5a5a41d4701ba25` Fresh Search，`cross_turn_reuse=false`，无 `local_state_mismatch`、旧 future action 入队或 `SEARCH_SETUP_FAILURE`。证据见 [`local-cross-turn-shuffle-boundary-smoke-2026-09-20.json`](multiplayer/evidence/local-cross-turn-shuffle-boundary-smoke-2026-09-20.json)。
- `MP_LOCAL_XTURN_CONTINUATION_MISSING` 的自然 Host/Client 运行夹具仍未触发（`continuation_missing=0`）。调度语义现已收敛到运行时直接消费的 `MultiplayerContinuationScheduleDecision`：远端回合保持 route；本地 Play 且当前 turn 缺失 continuation 时明确要求 Fresh Search，并同时清除 Awaiting/source ownership。该决策由第 12 项 Local Cross-Turn CI 合同直接覆盖；真实日志 marker 仍保留为可选 L3 证据，不再依赖随机对局证明核心状态转换。有效 EndTurn-only 同样由 12 项合同保持允许。
- Missing-continuation 本机编译证据已收口：`91ed6da4` 在真实 STS2/RitsuLib `0.107.1` 环境 Release build PASS（0 errors、2 warnings），`MultiplayerLocalCrossTurnChecks` 12/12 PASS；测试未启动游戏，也未修改源码/文档或创建提交。

## 当前下一步

优先继续 **Multiplayer Carry v2 / Safe Auto**，不要回退重做 Carry Ranking v1，也不要人工刷 R2 decisive fixture。当前代码/合同已通过；下一步是本机 0.107.1 Release build，然后按 `docs/multiplayer/RUNBOOK.md` 走 `HostVanilla + ClientCombatSolver`、Steam transport off、warm-up 后正式运行，至少连续验证 3 个本地回合的 fresh/validated search → 新 SafeExecutionSession → 原生安全本地动作 → 原生 Safe EndTurn。必须确认旧 request/session/route authorization 不跨回合复用，远端公开变化仍触发 fresh replan；Potion / Choice / teammate target / multiplayer-only card 命中时必须停止 Safe Auto 而不是循环重算。Instant / Replay / Showcase / Checkpoint 继续关闭。单人 0.107.1 卡牌审计已收口，除非出现新的真实 mismatch，不再作为当前主线。

已完成剩余 20 个已知 post-0.107.1 单人牌 patch-note 候选：Soul Storm、Momentum Strike、
Demon Form、Primal Force、Taunt、Bloodletting、Cruelty、Dominate、Accelerant、Collision Course、
Sunder、Relax、Whistle、Mangle、Pact's End、Echoing Slash、Terraforming、Crush Under、Salvo、Splash。
未发现新的 route-affecting mismatch，总数仍为 44。Soul Storm 的 Soul 计数、Primal Force→Giant Rock
变形、Pact's End 条件攻击、Echoing Slash 伤害循环等特殊路径均确认继续读取 0.107.1 模型数据。

已知后续补丁单人牌候选现在 **78/78 已审计完成**。solver 自身硬编码常数已完成两批、共 20 条高风险路径：
第一批为 Conqueror、Convergence、Shadow Step、Aggression、Dark Embrace、Calamity、Fan of Knives、
Hello World、Infinite Blades、Unmovable；第二批为 Expect a Fight、Pounce、Predator、Rebound、Reflect、
Synthesis、Tag Team、The Gambit、Unrelenting、Veilpiercer。两批固定量均确认是 0.107.1 的堆叠/
持续/消费计数或模式标记，不是泄漏的后续版本牌面数值；消费端也按对应 Power amount
decrement/consume。未发现新的 route-affecting mismatch，总数仍为 44，且两批均已补静态回归护栏。

直接 live-state 扫描也已完成一轮：Prediction 目录未发现新的后台 live HP/Block/Energy/牌堆读取。
`IntentForecaster.Build` 的 live CombatState/RNG 读取发生在 `CombatRootSnapshot.Capture` 主线程根捕获阶段，
随后后台搜索只消费已捕获 Forecast，因此不构成分支状态泄漏。

`STS2_01071` 分叉已完成第一轮集中审计：Hyperbeam/Scare 的 0.107.1 专用路径保持不变；
v0.108 才新增的 Midnight、Concoct、Constellation、Underworld、Soulbound、Cacophony、Hibernate、
Imitation Learning、The Ball 仍全部位于 `#if !STS2_01071` 边界外，已补静态护栏。Hand Drill 也已澄清：
0.107.1 保留 damage-based WasBlockBroken 触发，但更广的 AfterBlockBroken/Expose 路径继续排除。
本轮仍未发现新的 route-affecting mismatch，总数保持 44。

Release 编译错误已修复：`PowerPredictionStateSupport.cs` 缺失 `using CombatSolver.Engine.Common;`，
导致 `IPredictionStateForkable` / `PredictionForkContext` 无法解析；修复提交为 `63a1222b`，对应 CI 已通过。
静态 verifier 现已增加该命名空间回归检查。

continuation 审计已完成第二轮收口：Draw / Selection / Orb 的批量操作都由底层 execution frame
保存恢复索引；Hook/EndTurn 中未显式描述剩余工作的路径会由 `ExecutionDispatchScope` fail-closed
拒绝局部 continuation，并回退整动作重放，因此不会漏执行后半段。Stampede 等未专门保存循环索引的
Hook 属于性能回退而非语义错误。两轮均未发现新的 route-affecting mismatch，总数仍为 44。

剩余 13 个显式固定 `Power(1)` 路径也已完成审计：Forbidden Grimoire、Hellraiser、Master Planner、
Mayhem、Nostalgia、Reaper Form、Seeking Edge、Stratagem、Subroutine、The Sealed Throne、
Tools of the Trade、Trash to Treasure、Tyranny。它们分别是存在标记或每份 Power 的触发/选择/资源单位，
消费端按 `power.Amount` 或存在性读取，均未发现后续版本数值泄漏；已补静态护栏。
Forbidden Grimoire 的 `RecordLongTermResource(50)` 是 solver 战略估值，不是原生卡牌效果数值，未锁定为
0.107.1 卡牌常量。

近期编译回归也已收口：`bb29ce1c` 修复两条 Release warning 源码，`ae918241` 将历史计数改为读取
稳定的预测卡牌快照 `play.Card.Owner`，并补齐纯合同测试桩；对应 GitHub CI 已通过。
Power 生命周期第一批也已复核：Debilitate、Magic Bomb、Monologue、Oblivion、Sic Em、Strangle、
Colossus、Escape Artist、Hatch、Shrink 的回合边界均与既有适配证据一致。Escape Artist 保持
`3→2→1→1`；Shrink 的永久负层依赖循环入口 `Amount <= 0` 跳过，因此不会被递减。未发现新的
route-affecting mismatch，总数仍为 44。该批只保留审计结论，不再增加冗余静态护栏。

Power 生命周期第二批已继续复核：NoDraw、Dark Embrace、Doom、Asleep、Slumber、Battleworn Dummy、
High Voltage、Territorial、Pale Blue Dot、Smoggy、Consuming Shadow、Nemesis、Juggling、Tender。
这些路径均已进入原生-vs-模拟回合边界测试入口。Dark Embrace 抽牌中途出现选择时仍走既有
fail-closed 整阶段重放，不会从半完成状态继续；Nemesis/Tender 隐藏字典随 Fork 复制，Juggling 走
PredictionStateStore Fork，Pale Blue Dot 内部激活位既随 Fork 复制又进入状态指纹。未发现新的
route-affecting mismatch，总数仍为 44。

GitHub Actions 在 `9118442e` 上约 5 秒内同时结束两个 job、steps/logs 为空；且该提交相对已全绿的
`805ad8df` 仅有本 handoff 文档差异，verifier 与 run-contract-tests 逐字一致，因此当前记录为
runner/checkout 层异常，不据此改战斗源码。

Power amount-change / 临时属性 / Artifact 抵消链已完成一轮复核。Artifact 成功抵消 Debuff 时在
`RecordPowerAmountChange` 之前返回，因此不会把被抵消的 Debuff 错送给 Outbreak/Shroud/
Sleight of Flesh/Vicious；Artifact 自身正常消耗 1 层。Vicious 触发抽牌产生 nested choice 的路径已有
阶段挂起测试，调用点处于 execution dispatch，不能保留半处理的 drained amount-change 批次。
临时 Strength 的正负叠加、Artifact、极值 cap、回合末恢复、Power 顺序和 Fork 隔离已有完整原生差分；
0.107.1 的临时 Focus / Regen 特殊回合末顺序仍由现有 `STS2_01071` 分支保持。未发现新的
route-affecting mismatch，总数仍为 44。

死亡生命周期与 applier/实例清理已完成一轮复核。真正死亡后，Shrink/Constrict/Hex、
Guarded 和 Magic Bomb 都按原始 `Applier == dead` 精确清理；Guarded 保留实例身份并同时消费对应
PowerAmountPredictionState。Fairy in a Bottle / Lizard Tail 先经过 ShouldDie 与 AfterPreventingDeath，
成功复活后不会进入 deferred enemy-death cleanup，因此不会把“曾到 0 HP”误当真实死亡。Magic Bomb
与 Shrink 的施加者死亡语义已有实机差分；Tank/Intercept 的 Guarded/Covered applier 清理已在
0.107.1 卡牌审计中锁定。普通死者 Power 清理遵守 `ShouldPowerBeRemovedAfterOwnerDeath`，
Illusion 的原生移除 veto、玩家 Power 清理顺序、Orb/Pet teardown 与重复清理幂等性都有独立合同覆盖。
`PredictedDeathPhase` 随 Fork 复制并进入状态指纹，复活/永久死亡状态不会跨分支串线。
未发现新的 route-affecting mismatch，总数仍为 44。

最终收尾扫描第一小批已复核反射/私有状态。Nemesis/Tender/Pale Blue Dot/Intercept 的隐藏状态均已有
专门根捕获、Fork 或状态指纹覆盖；`_nextCreatureId` 随 Fork 复制且只用于新生 Creature 的唯一身份，
不参与伤害/RNG/规则判定；`_rootFloatingCards` 是只读根成员集合；MultiplayerScalingModel 的私有
run/combat 引用只用于根捕获时主动断开 live 引用。未发现单人路径存在“读取私有状态但分支未隔离”
的新 route-affecting mismatch，总数仍为 44。

最终收尾扫描第二小批已复核 solver-authored 硬编码常量与特殊计数：Normality 的每回合 3 张上限、
Confused/Slither 的 0..3 能量随机范围（`NextInt(4)`）、Iteration 的“本回合第一次 Status”计数、
Pen Nib 的 10 次循环/第 10 次双倍、Surrounded 的 1.5 倍背击，以及 Juggling 的第 3 次 Attack。
Iteration 的当前抽牌会在 `AfterCardDrawn` 前先写入分支计数，因此 `<= 1` 没有 off-by-one。
这些值均与 0.107.1 语义一致，已补静态护栏；未发现新的 route-affecting mismatch，总数仍为 44。

最终收尾扫描第三小批转到 Search 层 relic counter。RelicCounterCatalog 的 Happy Flower 3、Fake Happy Flower 5、
Pendulum 3、Pollinous Core 4、Pen Nib/Nunchaku/Tuning Fork 10、Joss Paper 5、Iron Club 4、
Galactic Dust 10，以及 Meat on the Bone 的二态目标均与 0.107.1 语义一致；Candelabra/Horn Cleat 为第 2 回合，
Chandelier/Captain's Wheel/Sparkling Rouge 为第 3 回合。另发现计数目标使用 4-bit 槽位但此前没有容量断言：
未来若加入周期 >= 17 的计数器或过多计数器可能静默截断/串入优先级槽。现已将 4-bit 参数命名化，并在
RelicCounterCatalog 静态初始化时 fail-closed 校验 period、slot、重复 Id 与计数器总量；当前行为不变。
上述 period/turn 值与 packing 不变量均已补静态护栏，route-affecting mismatch 总数仍为 44。

最终收尾扫描第四小批在 Search/Prediction 阈值中发现并修复 1 个新的 route-affecting mismatch：
ActEndingBossPolicy 原先把所有非最终 Act Boss 都按 A2+ 的“缺失 HP 恢复 80%”估值，导致 A0/A1
错误地认为战斗内治疗仍有 1/5 会跨 Act 保留。0.107.1 Ancient 原生逻辑是 A0/A1 补满缺失 HP，
只有 Weary Traveler（A2+）才乘 0.8。根快照现在读取 RunState.AscensionLevel：A0/A1 使用
ActClearFullHeal（战斗内恢复 HP 的跨 Act 持久价值为 0），A2+ 保持原 ActClearHeal 的 1/5 价值；
ActTransitionBossHpStrategy 对两种过 Act恢复都继续生效。累计 route-affecting mismatch 更新为 45。

同批还把 GrowthPolicy 内置/第三方成长预算重复写死的 1000 HP 上限收成单一 MaximumBudgetHp 常量；
这是 solver 配置安全边界，不是游戏版本数值。Act 3 的零基索引 2 与 0.107.1 三个 Boss
TEST_SUBJECT_BOSS / AEONGLASS_BOSS / QUEEN_BOSS 也已补静态版本护栏。

新问题包 `CombatSolver-0.40.2-LOUSE_PROGENITOR_NORMAL-e54eceb84baa44f3809748a38e6b2725.zip`
定位到新的 SearchSetupFailure：根快照捕获 LouseProgenitor 时尝试读取不存在的实例成员
`GrowStrength`，实际 0.107.1 模型使用 private static const `_growStrength = 5`；同时
`MonsterValueReader` 原先只搜索实例成员，因此仅改名仍会失败。现已让 int/bool/object 编译访问器
同时支持 instance/static property/field，并将 LouseProgenitor 静态值捕获和 CURL_AND_GROW 消费统一改为
`_growStrength`。现有 DevotedSculptor `_ritualGain` 仍按实例 readonly 字段路径读取，不受影响。
该错误会让 LOUSE_PROGENITOR_NORMAL 在 AutoTurnStart 根捕获阶段完全无法搜索，计入新的
route-affecting mismatch；累计总数由 45 更新为 46。已补静态回归门禁。

最终收尾扫描第五小批继续检查 StrategicEffect / Novelty / memory policy。StrategicEffectModel 中
cards-per-turn、reachable-cards、Buffer 兜底、Focus/Furnace scaling 等裸数字均属于 solver 的启发式估值，
不应伪装成 0.107.1 游戏常量；SearchWaveMemoryPolicy 与 SmartLayerMemoryForecast 的 64 MiB 虽同值，
但分别表示 parent reserve 与 whole-layer forecast 最小余量，当前没有证据要求合并。BFWS 达到
MaxNoveltyEntries 后仍按“先判断当前 tuple novelty、再限制历史写入”处理，已与仓库 ReferenceNovelty
和容量 0/1/7 的生成流合同核对，属于设计语义而非 bug。未发现新的 route-affecting mismatch；在后续 Louse 修复后当前总数为 46。
现有 BfwsResearchChecks 此前未进入 run-contract-tests；已加入 L1 合同套件，并补静态门禁防止再次掉出。

最终收尾扫描第六小批检查 Cycle / Retention / Transposition。Cycle family 的 improvement epoch
使用 byte 但硬上限仅 4，CycleRegion epoch 使用 int 且预算先 clamp 后 checked；0.107.1 手牌上限也由
CardPileCmd 的 Draw/Add 两条路径共同限制为 10，因此 HandFingerprintBuffer[10] 不构成越界风险。
TranspositionFrontier 的 nondominated label 接受/替换逻辑未发现新的状态误合并。

发现并修复 1 个 solver 确定性缺口：Cycle、CycleExit、CrossTurn 的 retention rank 分段允许尾部/头部
重叠，而 SortRetained 原先在最小 retention rank 与 Score 同时相等时直接返回 0。List.Sort 不保证稳定，
并行候选输入顺序可能因此改变后续扩展顺序。现在仅在原本完全平局时追加已有的
CompareCycleCandidateDeterministicFingerprints（StateKey → Action → Parent）作为最终 tie-break；
所有既有 rank/Score 优先级不变。BeamRankSortChecks 已扩展为直接抽取生产 CompareRetainedOrder，
覆盖 rank、score 与重叠 rank 的 deterministic tie-break。

同一批继续发现并修复 transposition 的路径语义遗漏。StateKey 只描述模拟器状态，但
SearchRouteTraits 会决定后续 retention lane，HasNonPotionAction 还会直接禁止 RequiresOpeningUse 药水；
旧 TranspositionLabel 没有这两项，因此相同 StateKey 的两条路径可能在未来可用动作/保留资格不同的情况下
仍被当成互相支配。现在 label 纳入两项路径事实：只有左侧 traits 覆盖右侧全部 traits，且左侧的
HasNonPotionAction 不比右侧更受限时才允许支配。false（尚无非药水动作）可支配 true，反向不可。
此外 ResetRebuildableCaches 原先对重复 StateKey 直接覆盖字典，只保留最后一个 label；现在重建时通过
TranspositionFrontier.TryAccept 恢复完整 nondominated frontier。BeamRankSortChecks 同时抽取生产
Transpositions.cs，覆盖 6 组 path-sensitive dominance 合同。以上两项属于 solver 确定性/剪枝正确性修复，
不新增 0.107.1 版本 mismatch；当前 route-affecting mismatch 计数保持 46。

最终收尾扫描第七小批继续审计 StateKey 外的 path-only 状态。CrossTurnProbe 传播链确认仍由
AttachCycleSchedulingEvidence → AttachCrossTurnSchedulingEvidence 保持，未发现传播断链；CumulativeEnemyHpLost /
TurnOutcome 只用于最终路线报告，不参与未来合法动作。新发现并修复 CombatProgressState 的转置遗漏：
它保存约 28 项历史最好/最低进展基线与 TurnsWithoutProgress，后续 Advance/ShouldPruneCrossTurnNoProgress
会据此判断“下一回合是否有进展”和何时停止无进展路线。旧 TranspositionLabel 未携带该历史，因此两个
当前 StateKey 相同但进展历史不同的节点可能互相支配，导致剩余无进展预算不同的路线被误剪。现在只有
CombatProgressState 值相等时才允许转置支配；ResetRebuildableCaches 与 admission/expanded 两张转置表均
传入同一进展状态。BeamRankSortChecks 的生产代码抽取合同扩展到 8 组，新增双向“不同 progress 不得合并”。
此项是 solver 剪枝正确性修复，不新增 0.107.1 版本 mismatch；计数仍为 46。

最终收尾扫描第八小批单独复核 cross-turn probe/baseline。确认 CrossTurnProbe 是有界调度租约：
它会改变 ShouldPruneCrossTurnNoProgress 的继续资格和 cross-turn retention 排序，但旧普通 transposition
既不识别该租约，也不会绕过它，因此同 StateKey 的普通路线可能提前剪掉正在观察延迟收益的 probe。
现已让 active CrossTurnProbe 与 cycle/ordered-mutation 调度租约一样绕过 admission/expanded transposition；
ResetRebuildableCaches 也跳过 active probe，避免内存重建后 probe label 反向支配普通路线。租约被 retention
明确清除后节点恢复普通转置剪枝。stand-pat baseline 仍只作为 turn-start 的语义比较证据，不进入普通
StateKey/transposition。该项是 solver 剪枝正确性修复，不新增 0.107.1 版本 mismatch；计数仍为 46。

最终收尾扫描第九小批审计 TurnSetupChoices / TurnSetupPlayState 与边界元数据。两项 TurnSetup 数据仅用于
最终 continuation 从自身 parent 链重放，以及运行时用 TurnSetupPlayState 校验 live state；transposition
不会替换 surviving node 的 parent 链，因此不需要进入 label。随后发现 BoundaryReason 是真正遗漏项：
BuildStateKey 不包含 boundary，而最终路线排序、turn-outcome 可比性和 continuation 构建都会读取它。
旧 transposition 因此可能让 UnsupportedEffect/PendingChoice 终止节点与相同 StateKey 的正常可继续节点互相支配。
现在 TranspositionLabel 纳入 SearchBoundaryReason，并要求 boundary 相等才允许支配；普通 admission、
expanded table 和 ResetRebuildableCaches 均传入节点 boundary。BeamRankSortChecks 增加双向不同 boundary
不得合并合同，总数扩展到 10 组。HasPredictionRisk 当时仅确认未直接进入最终排序，因此该批未扩大转置键；第十一小批继续追踪风险历史后，
确认这一结论不足，见下。该项是 solver 剪枝正确性修复，不新增 0.107.1 版本 mismatch；计数仍为 46。

CI 门禁专项排查：最后一次完整成功 run 为 805ad8d（35610137901），约 4 分钟后的 58f56c9
开始两个 job 同时出现 steps=null，之后持续如此；失败 job 连日志 blob 都不存在，而成功 job 有正常
windows-2025 hosted runner 日志，说明失败发生在 checkout/脚本执行之前。workflow 本身未依赖 self-hosted
runner。static-consistency 只使用 PowerShell 7/source/XML/git 检查，现改到 ubuntu-latest；contract-tests
暂留 windows-latest，因为 MultiplayerSnapshotChecks 明确使用 WINDIR/System32/cmd.exe 和 Windows 路径语义。
这样不削弱任何门禁，同时可区分“Windows runner 分配异常”和“账户级 Actions 配额/账单拒绝”。
诊断提交 b5e5105 后，ubuntu-latest 的 static-consistency 与 windows-latest 的 contract-tests 仍同时
steps=null，且都无日志 blob，已排除 Windows runner 专属问题，定位为 GitHub Hosted Actions 在 runner
分配前的账户/仓库级拒绝。仓库 YAML 无法直接解除该外部限制。为避免恢复后继续无谓消耗私有仓库分钟，
workflow 现仅在 compatibility workflow 自身或 source 非 docs 文件变化时自动触发；source/docs/** 文档提交
不再跑完整门禁。另加入同 workflow/ref 的 cancel-in-progress，连续快速提交只保留最新一轮。手动
workflow_dispatch 保留，代码/tools/target/project 变化仍完整执行门禁。云端 runner 被账户级拒绝期间，
新增 tools/run-ci-gates.ps1 作为本地统一入口：顺序执行 target-version、refactor-boundaries、完整 L1
contract suite 与 git diff --check；支持 -NoRestore / -SkipPython 透传。它不替代 GitHub required check，
只保证本地/Codex 可运行与云 workflow 相同的源码门禁。

最终收尾扫描第十小批审计 IsTerminal / TerminalStamp。TerminalStamp 本身不需要整体进入
transposition：其 outcome 已投影为 SimulationSnapshot.PlayerDead / AllEnemiesDead，终局回合又由 StateKey
中的 turn 区分。但这两个终局标志此前未进入 TranspositionLabel。尤其 defeat 路径允许 native pending loss
已经锁定后由后续效果恢复当前 HP，因此 PlayerDead=true 不能可靠地从 HP/StateKey 反推；相同 StateKey 的
live/defeat 路线理论上可被错误合并。现已要求 PlayerDead 与 AllEnemiesDead 均相等才允许转置支配，并同步
admission、expanded table 与 ResetRebuildableCaches。BeamRankSortChecks 新增 live-vs-defeat 与
victory-vs-nonvictory 双向合同，path-sensitive transposition cases 从 10 扩到 14。该项是 solver 剪枝
正确性修复，不新增 0.107.1 版本 mismatch；计数仍为 46。

最终收尾扫描第十一小批继续追踪 HasPredictionRisk / PredictionGaps。最终排序本身不读取 HasRisk，
但风险历史会随 simulator fork 继承；StateEvaluation 在未来胜利时调用 HasUncompensatedDeathGap，若路径历史
含未补偿 Death gap，会把原本 victory 改为 UnsupportedEffect。因此相同当前 StateKey 的两条路线即使都
HasRisk=true，只要 gap 语义不同，未来合法终局就可能不同。现已让 TranspositionLabel 保存快照的完整
PredictionGaps，并仅在两侧 gap 序列相等时允许支配；这比单独比较 HasRisk bool 更严格且直接覆盖 Death /
non-Death、compensated / uncompensated 差异。admission、expanded table 与 ResetRebuildableCaches 均同步。
BeamRankSortChecks 增加双向不同 risk-history 不得合并合同，path-sensitive cases 从 14 扩到 16。
至此已审出的 StateKey 外、会改变未来搜索语义的 path-only 状态均有显式保护；transposition 收尾停止继续
扩张，后续优先回到实机问题包/本地门禁驱动。该项是 solver 剪枝正确性修复，不新增 0.107.1 版本 mismatch；
计数仍为 46。

已完成的 Power、continuation、死亡生命周期和 78/78 卡牌 OnPlay 数值不重复展开；多人牌仍暂不作为当前 blocker。
