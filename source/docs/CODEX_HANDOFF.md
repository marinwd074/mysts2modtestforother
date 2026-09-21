# Codex 当前交接

## 当前技术状态

- CombatSolver `0.40.2`；目标游戏 / RitsuLib `0.107.1`；兼容符号 `STS2_01071`。
- MP-0 Core / lifecycle：PASS。
- MP-1 Advisor：受控 Smoke PASS。
- MP-2A：显式 `safe-execute`/`safe-execute-lab` 的一动作 Host/Client Smoke 已 PASS，作为历史基线保留；该证据不等于两动作能力已通过。
- MP-2B：两动作上限、显式 SafeExecutionSession、动作后稳定世界等待和本地/远端变化重验证已完成；历史 `MultiplayerSafeExecuteChecks` 39 项、正常/远端干扰验证器合成回归和 Release 构建已通过，真实正常两动作与远端干扰 Smoke 也已分别返回 PASS。摘要见 [`mp2b-smoke-2026-09-20.json`](multiplayer/evidence/mp2b-smoke-2026-09-20.json)。
- MP-2C：已直接将 MP2B 泛化为当前回合 bounded N-action，policy ceiling 为 6；当前合同 40 项、正常/干扰验证器各 6 个合成用例和 Release 构建已通过。真实正常 Smoke 自动完成 3 张牌，真实远端干扰 Smoke 在完成 2 张后中止并重新搜索；摘要见 [`mp2c-smoke-2026-09-20.json`](multiplayer/evidence/mp2c-smoke-2026-09-20.json)。
- Multiplayer Carry Ranking v1：已接入主线程捕获的公开远端/敌人上下文、纯确定性 evaluator 和最终路线排序 tie-break；11 项 Multiplayer Carry Ranking 离线合同与 Release 构建通过。仅显式 Advisor/Safe Execute 使用，默认 Probe/单人保持原排序；远端私有状态、队友行为预测和未知敌方目标均保持 fail-closed/neutral。运行时目标分类已补齐：仅原版怪物的公开 AttackIntent 按 0.107.1 原生 `AttackCommand.FromMonster → TargetingAllOpponents` 合同标为 `AllPlayers`，非攻击/第三方怪物保持 `Unknown`；root 日志已增加机器可读 `all_player_threats/unknown_threats`，并新增 R1/R2 validator 与 CI 合成自测。R1 已在 `SLIMES_WEAK` 真实 Advisor journal 中 PASS（1 名远端、3 敌人、2 个 `AllPlayers` threat、1 个 Unknown，remote private=false）；R2 当前保持 `UNVERIFIED`，但不再作为阶段阻塞项。`f54506c3` 已完成真实回归：Release 0 errors（2 warnings）、11/11 Carry contracts、validator self-test 全 PASS；`carryWindow=current_turn_pre_end` 与 `carryObservationActionCount=3` 对齐第一处 EndTurn，且 `futureOnlyKillCredited=false`。两次真实 `SLIMES_WEAK` fixture 中威胁 `TWIG_SLIME_S` 搜索时仍为 15 HP，而当前 T1 只有两张 `Strike=6`、`Bash=8` 在 T2 才抽到，因此没有形成当前回合可击杀的等价 tie，R2 正确返回 UNVERIFIED。决定性 flip 继续由第 10 项纯合同保证；真实 R1 + 当前窗口回归已证明 runtime 接线与安全语义。后续仅在自然出现合适 fixture 时补 R2 decisive runtime evidence，不再人工刷场景。
- Multiplayer Lab snapshot：已改为 schema 2 的持久 base-game snapshot + profile overlay 增量同步。marker 拆分 `baseGameId`、`ritsuArtifactId`、`combatSolverArtifactId`；游戏版本/底座变化、底座完整性失败或旧 schema 才全量重建，overlay 采用临时 managed tree + SHA-256 + rename/rollback。CombatSolver/RitsuLib 变化分别只更新各自 payload，HostVanilla 不因 CombatSolver 构建变化重建；`prepare-instances` 输出 `FULL_REBUILD` / `OVERLAY_UPDATED` / `REUSED` 和 `copiedFiles`。ownership、no-reparse-point、运行中禁止覆盖和正式证据隔离合同保持不变。
- Multiplayer Instant、Potion、Choice、Replay、Full Auto 和队友目标继续关闭；Reactive Carry 仅在显式 Safe Execute 且最新安全路线边界成立时通过原生 EndTurn。新实现已为显式 Advisor/Safe Execute 打开“只预测本地玩家”的跨回合路线，默认 Probe 仍保持只读当前回合边界；Safe Execute 仍只部署当前真实本地回合。
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

当前只处理 **0.107.1 单人卡牌版本差异**。已确认并修正 44 个 route-affecting mismatch。

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

下一批转向特殊 OnPlay/Hook **执行顺序与 continuation**，优先检查会在 pending choice / generated-card /
Forge / draw / exhaust 中途挂起后恢复的手写链；多人牌仍暂不作为当前 blocker。
