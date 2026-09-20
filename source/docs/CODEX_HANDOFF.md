# Codex 当前交接

## 当前技术状态

- CombatSolver `0.40.2`；目标游戏 / RitsuLib `0.107.1`；兼容符号 `STS2_01071`。
- MP-0 Core / lifecycle：PASS。
- MP-1 Advisor：受控 Smoke PASS。
- MP-2A：显式 `safe-execute`/`safe-execute-lab` 的一动作 Host/Client Smoke 已 PASS，作为历史基线保留；该证据不等于两动作能力已通过。
- MP-2B：两动作上限、显式 SafeExecutionSession、动作后稳定世界等待和本地/远端变化重验证已完成；历史 `MultiplayerSafeExecuteChecks` 39 项、正常/远端干扰验证器合成回归和 Release 构建已通过，真实正常两动作与远端干扰 Smoke 也已分别返回 PASS。摘要见 [`mp2b-smoke-2026-09-20.json`](multiplayer/evidence/mp2b-smoke-2026-09-20.json)。
- MP-2C：已直接将 MP2B 泛化为当前回合 bounded N-action，policy ceiling 为 6；当前合同 40 项、正常/干扰验证器各 6 个合成用例和 Release 构建已通过。真实正常 Smoke 自动完成 3 张牌，真实远端干扰 Smoke 在完成 2 张后中止并重新搜索；摘要见 [`mp2c-smoke-2026-09-20.json`](multiplayer/evidence/mp2c-smoke-2026-09-20.json)。
- Multiplayer Lab snapshot：已改为 schema 2 的持久 base-game snapshot + profile overlay 增量同步。marker 拆分 `baseGameId`、`ritsuArtifactId`、`combatSolverArtifactId`；游戏版本/底座变化、底座完整性失败或旧 schema 才全量重建，overlay 采用临时 managed tree + SHA-256 + rename/rollback。CombatSolver/RitsuLib 变化分别只更新各自 payload，HostVanilla 不因 CombatSolver 构建变化重建；`prepare-instances` 输出 `FULL_REBUILD` / `OVERLAY_UPDATED` / `REUSED` 和 `copiedFiles`。ownership、no-reparse-point、运行中禁止覆盖和正式证据隔离合同保持不变。
- Multiplayer Instant、Potion、Choice、Replay、Full Auto、旧跨回合路线复用和队友目标继续关闭；Reactive Carry 仅在显式 Safe Execute 且最新安全路线边界成立时通过原生 EndTurn；默认多人仍保持 Probe。
- 本轮没有为 MP2B 声明新的 GitHub Actions 结果；实机结论来自隔离 Multiplayer Lab 的 Host/Client journal 与对应验证器，不等同于 GitHub Actions 结果。

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

## 当前下一步

[Reactive Carry Foundation](multiplayer/NEXT_REACTIVE_CARRY.md) 已完成；A/B/C 三轮真实
Host/Client Smoke 和机器摘要已收口。默认仍保持 Probe，Potion、Choice、Replay、队友控制、
Instant 和旧跨回合路线复用关闭。下一阶段如继续推进，应另立计划评估 uncertainty-aware
ranking 或 teammate behavior model，不把未验证的预测或 Full Auto 混入当前 Safe Execute。
