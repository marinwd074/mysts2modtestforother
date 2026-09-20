# Multiplayer 适配阶段

当前阶段：**MP-0 Core 与 Host 重建房间后的 Client 重新加入生命周期均已通过；MP-1 Advisor 受控 Smoke 已通过，重连后的远端私有药水语义保持 fail-closed；MP-2A 显式一动作 Safe Execute Smoke 已通过；MP-2B 正常两动作与远端干扰实机 Smoke 已通过，默认多人仍保持 Probe**。

本阶段依据 `Multiplayer Apply` 中的精简功能方案和修正版执行计划实现，目标是先用隔离的双实例完成真实 Host/Client 证据，不改变多人会话语义。

## 当前门禁状态

- **MP-0 Core：PASS**。连接兼容、本地私有状态只读采集、远端公开战斗状态、双 Client 对照和 Probe 只读契约均有证据。
- **MP-0 Hardening：PASS（受控生命周期）**。已按游戏规则由 Host 退出并重新创建房间，Client 收到 `Quit` 后重新握手、加入、Ready，并再次进入有效战斗；进程停止本身不计入证据。
- **MP-1 Advisor：SMOKE PASS（受控范围）**。静态合同与 Release 构建已通过；默认仍是 Probe，只有显式设置 `COMBATSOLVER_MULTIPLAYER_MODE=advisor` 才会授予当前回合、本地玩家、只显示路线的搜索能力，绝不会自动执行动作。`BurningBlood`、side-turn relic、多人 block-scaling 和 EndTurn replay 边界均已收敛；fresh `-bbfix` client 的真实复验记录 `SEARCH_COMPLETE=5`、`SEARCH_FAILURE=0`、`FAIL_CLOSED=0`，并有原生完成通知与路线回放证据。Probe 仍保持只读，MP-2 Safe Execute 不在本次通过范围内。
- **MP-2A Safe Execute：PASS（历史一动作范围）**。2026-09-20 的 HostVanilla + ClientCombatSolver `safe-execute` 单牌 Smoke 已验证通过；该证据保留为一动作基线。默认多人仍是 Probe，只有精确 `COMBATSOLVER_MULTIPLAYER_MODE=safe-execute` 才进入显式 Safe Execute，`safe-execute-lab` 仍只在 Multiplayer Lab 创建的 `ClientCombatSolver` 实例、Probe evidence 已启用且 ownership/profile marker 匹配时授权。
- **MP-2B Safe Execute：实机 PASS（受控范围）**。当前运行时使用显式 `Authorized → Executing → AwaitingWorldUpdate → Revalidating → next/Completed/Aborted` 会话，一次部署最多接受 2 张本地普通安全牌；每张原生动作完成后都会等待动作队列与 `WorldVersion` 稳定，复核本地手牌/资源/目标、敌人目标变化和远端公开状态。远端或未知变化会记录 `MP2B_REMOTE_DELTA_ABORT`、停止后续动作并重新搜索。正常两动作与远端干扰验证器均已在真实 Host/Client journal 上返回 PASS，摘要见 [`evidence/mp2b-smoke-2026-09-20.json`](evidence/mp2b-smoke-2026-09-20.json)。
- 正式一动作证据摘要见 [`evidence/mp2-safe-execute-formal-2026-09-20.json`](evidence/mp2-safe-execute-formal-2026-09-20.json)。

## 已实现

- `SolverSessionCapabilities`：集中声明单人、多人 Probe、多人 Advisor、多人 Safe Execute 能力；网络多人默认选择 `MultiplayerProbe`，Advisor 必须通过显式环境变量 opt-in，不能由玩家数或网络类型推断。
- `MultiplayerClientProbe`：只读记录本地玩家身份、生命/格挡/能量、回合、手牌、牌堆、药水、敌方公开状态、远端公开玩家摘要和完整 RNG 状态；不搜索、不部署、不改 `CombatState`/RNG、不发送自定义网络包。`Observe` 先用紧凑 fingerprint 判定变化，只有 Lab 证据开启时才生成完整 hard fingerprint。
- Probe 默认只写日志，不在普通 Advisor/桌面运行中产生 JSONL 证据；Multiplayer Lab 显式设置 `COMBATSOLVER_MULTIPLAYER_PROBE_EVIDENCE=1` 后，变化记录才会异步写入实例自有的 `diagnostics/CombatSolver-BugReports/`。证据 schema v2 直接包含 `runSeed` 与 `combatSegmentId`，并保留 16 MiB 上限、序列、`WorldVersion`、本地 Hand/DrawPile/Discard/Exhaust、远端公开摘要、敌人、RNG、MultiplayerScaling、CardMultiplayerConstraint 和只读契约标记。
- `source/tools/multiplayer-lab/prepare-instances.ps1`、`start-host.ps1`、`start-client.ps1`、`stop-owned-instances.ps1` 和 `collect-results.ps1` 提供带 ownership marker 的隔离实例与证据收集；它们不会修改正式 Steam 安装或正式 `MODS`。
- `source/tools/multiplayer-lab/validate-phase0-results.ps1` 只读校验带证据引用的 Host/Client 矩阵和 Probe JSONL；`MP-0A` 只校验连接兼容，`MP-0B` 才要求真实 Probe；它不会启动游戏，也不会解除 Advisor 门禁。
- `MultiplayerWorldTracker`：维护只读观察的 `WorldVersion`、dirty 状态和 200ms 稳定等待窗口；使用紧凑值型 fingerprint 做快速变化检测，只有变化时才生成完整 Describe/JSON 证据。
- Runtime 的搜索、部署、路线接管、全自动、回合开始接管和 Instant 入口统一经过能力表；没有通过实机证据前，网络多人保持关闭。

已把后续 MP-1/MP-2 的受控路径接入源码；默认仍由 Probe 门禁关闭，只有明确 opt-in 才能进入对应能力：

- Advisor/Safe Execute 允许时，`SearchPolicySnapshot.CurrentTurnOnly` 会截断首个本地回合层，关闭跨回合成长目标、远期 Novelty、路线缓存和 continuation reuse。
- `SolverPerspective` 用 `Public/Private/Unknown` 知识语义与 authority 解耦；`CombatRootSnapshot` 在显式多人搜索能力开启时传入 local-player-only capture。`SimulatedCombatState` 与 `CombatPredictionState` 只物化根玩家的私有牌堆、遗物、药水、运行级牌组和 mod card audit，公共玩家名册仍可作为战斗上下文存在，访问未捕获队友私有 combat state、药水或金币会显式失败；远端遗物 hook 若没有针对性的公开语义 capture 也会 fail closed，不能静默丢失队友影响。基于 STS2 0.107.1 原生审计，`BurningBlood` 只覆盖 `AfterCombatVictory`，因此精确类型可从 CurrentTurnOnly root listener 表省略；未知远端遗物仍由 `RootUnsupportedRemotePublicRelicListenerCount` 拦截。
- `MultiplayerSafeLocalActionClassifier` 只接受本地手牌的普通 `PlayCard`，目标仅限自身/敌人/无目标；药水、结束回合、选择、重复语义、多人专属卡、远端或未知目标形成连续前缀硬停止。
- 搜索会记录启动时 `WorldVersion`，结果发布时若远端 fingerprint 已变化则丢弃旧结果；能力门禁打开后，Runtime 会取消旧搜索，等待原生动作队列稳定和 debounce，再只启动一次最新当前回合搜索。
- Safe Execute 路径由显式 MP2B 会话管理每个动作；动作后先等待稳定世界，再区分预期本地变化与远端/未知变化。后者会停止后续动作并触发新的当前回合搜索，不会 reset/rebase `WorldVersion`，也不会自动 EndTurn 或跳过不安全动作继续执行。
- 如果会话从单人/过渡态进入多人，控制器会一次性取消活动搜索、部署、全自动和回合开始接管，并清空可部署结果；之后的 Probe fingerprint 变化继续触发失效。

## 当前明确未启用

多人 Safe Execute 仅通过精确的 `COMBATSOLVER_MULTIPLAYER_MODE=safe-execute` 显式开启，默认安装保持 Probe，不因玩家数或网络类型自动升级；MP2B 当前只开放源码中的两动作当前回合会话，已完成受控实机通过。药水、选择驱动、自动结束回合、Full Auto、Instant、跨回合复用和 Route Repair 仍未开放。Advisor 仍受本地私有/远端公开 root contract、当前回合和 fail-closed 约束。

## MP-2B 当前实现与验证状态

- `MultiplayerSafeExecutionSession` 固定一次用户授权的请求 ID、路线 generation、起始/已接受 `WorldVersion`、动作上限和状态迁移；旧路线或生命周期变化不会 reset/rebase 世界版本。
- `MultiplayerSafeLocalActionClassifier` 只取最多两张本地普通 `PlayCard`，不接受药水、EndTurn、Choice、Replay/重复语义、多人专属牌、队友目标或未知目标；第二张牌执行前仍会针对 live state 重新分类。
- 每张牌只通过原生 `PlayCardAction`；动作队列完成后强制进行一次 action-boundary Probe，并等待稳定 `WorldVersion`，再复核本地手牌/能量/星星、身份/目标、敌人非目标状态和远端公开 fingerprint。
- 本地合同检查为 39 项 PASS，MP2B 正常 Smoke 与远端干扰验证器各有 5 项合成用例 PASS；真实 Host/Client 正常两动作与远端干扰证据也已通过对应验证器。运行命令见 `tools/multiplayer-lab/README.md`，实机步骤与摘要见 `RUNBOOK.md` 和 `evidence/mp2b-smoke-2026-09-20.json`。

## MP-0A / MP-0B 通过条件

MP-0A 需要在真实 Host/Client 矩阵中验证：

1. Vanilla Host 能接受未安装 CombatSolver 的客户端。
2. Host 不需要 CombatSolver，且没有 CombatSolver 自定义网络包。
3. Client 与 Host 的 wire/model compatibility 成立。

MP-0B 在 MP-0A 成立后，继续验证：

1. 客户端能唯一识别本地玩家、NetId 和战斗身份。
2. 本地手牌、DrawPile 顺序、Discard/Exhaust、药水、敌人公开状态、RNG counter 和 MultiplayerScaling 稳定可读。
3. 抽牌、洗牌、插牌后，客户端观察与实际本地抽牌一致。
4. 队友动作可被观察为 world fingerprint 变化，且探针全程不修改真实战斗。
5. Lobby、战斗开始/结束、下一层、退出和重新加入不破坏上述边界。
6. 单人 Release/contract/smoke 回归不受影响。

`localPlayCardSync`、`localEndTurnSync`、`fastActionStress` 不属于 MP-0 必需
项，移到 MP-2 Safe Execute。FastMP 命令行启动本身也不能代替 Lobby 或 wire
证据。

本仓库已取得 A/B/C 三组连接到首战的成对日志，并取得 D: 新 DLL C 组（Vanilla Host + CombatSolver Client）的最终非空、只读 Probe 证据：339 条记录、4 个战斗段、每张牌带有每场战斗稳定的实例 token，覆盖 81/81 次抽牌匹配、5 次洗牌、70 次弃牌、4 次消耗和 93 次远端摘要变化；配对日志还覆盖 4 场战斗启动、其中 3 场结束后的奖励与地图推进。双 Client 敌人公开状态对照也已通过。最新 Host 重建房间、Client 重新加入并再次进入 `PHROG_PARASITE_ELITE` 的证据已写入 [`evidence/phase0-matrix-2026-09-19.json`](evidence/phase0-matrix-2026-09-19.json)。

为补强敌人公开状态证据，Lab 新增 `compare-probe-public-state.ps1`，并准备了第二个 D: 盘 `ClientCombatSolver` 观察实例；它只比较同一局两个独立 Client Probe，不修改网络协议或自动提升矩阵状态。

当前仍有效的多人限制集中记录在 [`LIMITATIONS.md`](LIMITATIONS.md)。

## MP-1 Advisor 手动验证入口（当前）

以下 D: 快照已装入当前 `CombatSolver.dll`；命令只启动可见隔离实例，不自动创建 Lobby、选择角色或点击 UI。启动后由用户手动完成 Join、Ready、进入普通战斗，并观察建议模式：

```powershell
# Run from the repository root.
$repoRoot = (Get-Location).Path
$labRoot = Join-Path $repoRoot '.local\multiplayer-lab'
$toolRoot = Join-Path $repoRoot 'source\tools\multiplayer-lab'

pwsh -NoLogo -NoProfile -File "$toolRoot\start-host.ps1" `
  -InstanceRoot "$labRoot\runtime-mp-advisor-host-20260919-bbfix" -ForceSteamOff
pwsh -NoLogo -NoProfile -File "$toolRoot\start-client.ps1" `
  -InstanceRoot "$labRoot\runtime-mp-advisor-client-20260919-bbfix" `
  -ClientId 1000 -MultiplayerMode advisor -ForceSteamOff
```

验证时应看到 `MP_ADVISOR_SEARCH_START` / `MP_ADVISOR_SEARCH_COMPLETE`；队友动作应产生 `MP_ADVISOR_WORLD_CHANGED` 并使旧结果出现 `MP_ADVISOR_SEARCH_STALE`。Advisor 只能显示当前本地回合路线，不能自动出牌、结束回合、用药、驱动选择或发送自定义网络包。生命周期验证必须遵守游戏规则：Host 退出并重新创建房间后，Client 再加入、Ready 并进入有效战斗；停止进程本身不计作 lifecycle 证据。运行日志和 Probe 仍留在 `.local/`，不直接提交。

### MP-1 Advisor 首轮 Smoke 结果（2026-09-19）

- Vanilla Host 与 `COMBATSOLVER_MULTIPLAYER_MODE=advisor` Client 成功完成 Lobby、Ready、MapCoord 和普通战斗创建；此前 `CombatReplayOutcome` 的 `state.Players.Single()` 崩溃已修正为本地玩家选择。
- Client 的 Advisor combat log 产生 `MP_ADVISOR_SEARCH_START` 16 次、`MP_ADVISOR_SEARCH_COMPLETE` 0 次；16 次均在 `root_capture` 因未建模的远端公开 `RELIC.BURNING_BLOOD` hook 触发 `MP_ADVISOR_FAIL_CLOSED`。未绕过该合同，也未发布路线。
- 同一轮 Probe 134 条记录全部保持 `readOnly=true`、`actionsEnqueued=false`、`customNetworkPacketSent=false`；因此本轮没有 Solver 自动动作或自定义网络包证据。该结果是 Advisor 的真实阻塞证据，不应记为 Smoke PASS。
- 下一步只针对该具体公开语义建立受限 capture（或选择没有该 hook 的明确场景）；不捕获队友全部遗物/牌组，不扩大到 Safe Execute。

### MP-1 Advisor BurningBlood root fix（2026-09-19）

- 原生反射与 IL 审计确认：`BurningBlood` 仅声明 `AfterCombatVictory(CombatRoom)`，逻辑是胜利后治疗持有者；它不参与当前回合战斗 hook、当前敌人状态或当前路线评分。
- `afa6a64` 增加精确类型 allow-list：远端 `BurningBlood` 不进入 local-player-only root listener 表；未知远端遗物仍保留并由 root contract fail closed，远端 `RelicsOf(remote)` 仍不可用。
- `MultiplayerRootCaptureChecks` 通过 4 项合同检查；Release DLL SHA-256 为 `507FAFDCAF72E3E56ED537BCB3A4B15BCC99A63277308F3714B910C2BBA85E11`。全新隔离快照为 `runtime-mp-advisor-host-20260919-bbfix` / `runtime-mp-advisor-client-20260919-bbfix`，尚未把手动 Smoke 结果记为 PASS。

### MP-1 Advisor side-turn / block scaling 修复（2026-09-19）

- `BurningBlood` root 修复后的首次实机复验已进入 combat；随后 generation 6 暴露未捕获远端 `RelicsOf(remote)` 被 side-turn relic phase 误枚举，generation 7 暴露 `ModifyBlockMultiplicative` 对本地 `DEFEND` 也提前拒绝双玩家。
- `ffad49b` 让 side-turn relic 只枚举参与且已捕获的玩家，远端 turn 仍显式 fail-closed；多人 block mirror 先复刻原生 enemy/powered-block early-exit，再调用原生 scaling table，不保留 live RunState/CombatState。
- 新 Release/runtime DLL SHA-256 为 `70FA663D661056317093EE9F6FAFE7FA37699FEB3681B16FF5B9FD420A6D384C`；`-bbfix` client 快照已替换并于 23:06 重启，下一轮真实 Smoke 仍待手动完成 Lobby/战斗。
- `37592ca` 将 EndTurn replay 的玩家阶段限制为 `RootCapturedPlayers`，避免 local-player-only root materialize 远端私有 combat state；当前 runtime DLL SHA-256 为 `281A286A109F4A2EC428290E0CAEF9B05A034DE837C6793533590EF695BC4A75`，client 已于 23:17 重启，随后 fresh Smoke 结果见下。

### MP-1 Advisor 真实 Smoke PASS（2026-09-19）

- fresh `-bbfix` runtime（DLL SHA-256 `281A286A109F4A2EC428290E0CAEF9B05A034DE837C6793533590EF695BC4A75`）的当前 combat log 记录 `MP_ADVISOR_SEARCH_START=10`、`MP_ADVISOR_ROOT_CAPTURE_BEGIN=10`、`MP_ADVISOR_SEARCH_COMPLETE=5`、`MP_ADVISOR_FAIL_CLOSED=0`、`SEARCH_FAILURE=0`。
- 原生完成通知记录 `SEARCH_COMPLETION_NOTIFICATION kind=Succeeded native=shown`；同时有 `ROUTE_REPLAY=6`、`ROUTE_ACTION=17`、`UI_STATE state=ready`（5 次）的路线发布/回放记录。`MP_ADVISOR_SEARCH_STALE=1` 后出现后续 generation 完成，且记录了 world-version 变化，满足本轮 stale/re-search 观察目标。
- 同一 client 的 Probe `51/51` 条记录为 `readOnly=true`，`actionsEnqueued=true` 为 `0`，`customNetworkPacketSent=true` 为 `0`；本轮没有 Safe Execute、自动出牌、自动 EndTurn、药水或选择入口。路线中的 PlayCard/EndTurn 仅为模拟回放证据，不是 live action enqueue。
- 证据保留在 `.local/multiplayer-lab/runtime-mp-advisor-client-20260919-bbfix/diagnostics/`；Host 重建房间后的退出/重新加入生命周期已通过。重连后的 SLIMES/PHROG 战斗中，Advisor 对远端私有药水库存缺失按合同 fail-closed，未发布路线；未知远端遗物 fail-closed 和远端私有清单不可访问的合同继续有效。
- 机器可读摘要：[mp1-advisor-smoke-2026-09-19.json](evidence/mp1-advisor-smoke-2026-09-19.json)。完整日志仍只保留在 `.local/`，不进入仓库。

### MP-1 post-Smoke 固定工作量对照（2026-09-20）

- 当前源码 `a6ab8ff` 的 CompatibilitySmoke 构建在固定 `COMPAT1071` / `IRONCLAD` / `NIBBITS_NORMAL` / Medium beam 60 / DOP1 / 5000 ms 条件下完成 3 个独立样本；三次均为 `expanded=3528`、`transitions=10156`、路线和结果 identity 与历史 Batch 7 baseline 完全一致。
- 当前样本平均 `2961.759 ms / 373,990,581 B`；历史 baseline 平均 `3169.203 ms / 370,614,600 B`。当前 spot 对照为耗时 `-6.546%`、分配 `+0.911%`，没有 Gen2 或 >50/100 ms 帧尾部；由于不是与重建的 pre-MP1 二进制交错运行，不宣称稳定加速，只记录固定工作量无回归。
- 逐样本 JSON 与口径说明保留在 [`runtime-evidence/20260920-post-mp1-performance`](../../runtime-evidence/20260920-post-mp1-performance/)；专用 smoke 的 launcher `exit_code=0` 收尾提示不改变 JSON 结果判定。

### MP-1 Advisor Stability 新一轮（2026-09-20）

- 使用当前源码构建（commit `1b6028502ab00e9c1394e51ffd3cb1661fcd533c`，DLL SHA-256 `864EC2A8276845B0C412106259D75AE2D30333826D415BE03A7371FD3A3B474F`）完成新一轮隔离实机；Host 按游戏规则退出并重建房间，Client 重新加入、Ready，并再次进入 `SLIMES_WEAK` 战斗。
- 重连后的 Advisor combat journal 记录 `MP_ADVISOR_SEARCH_START=4`、`MP_ADVISOR_ROOT_CAPTURE_BEGIN=4`、`MP_ADVISOR_SEARCH_COMPLETE=3`、`MP_ADVISOR_FAIL_CLOSED=0`、`SEARCH_FAILURE=0`；其中 1 个 generation 在 world invalidation/debounce 期间未发布，未记录为成功完成。成功路线有 `ROUTE_REPLAY=3`、`ROUTE_ACTION=7`、`UI_STATE state=ready=3`，全部为当前回合 `CurrentTurnAdoption`。
- 同一 Client Probe 共 `130` 条，`readOnly=true` 为 `130/130`，`actionsEnqueued=true` 为 `0`，`customNetworkPacketSent=true` 为 `0`，`searchStarted=true` 为 `0`；远端状态 fingerprint 持续变化，未发生 live action 或自定义网络包。
- 本轮是 `PASS_CONTROLLED_REJOIN_NO_POTION`：本地药水槽全程为空，未覆盖“重连后远端存在私有药水”的场景，因此总体 Advisor Stability 仍为 `PARTIAL`，既有 fail-closed 阻碍不解除。机器摘要见 [`evidence/mp1-advisor-stability-2026-09-20.json`](evidence/mp1-advisor-stability-2026-09-20.json)。

### MP-0 生命周期与重连后边界（2026-09-19）

- Host 日志先记录 `Stopping host. Reason: Quit` 与 Client 断开，随后新握手、`ClientLoadJoinRequestMessage`、Client Ready、run load 和 `Combat started`；Client 日志同步记录 Quit、再次 Join、epoch 4、Ready、run load 和 `PHROG_PARASITE_ELITE` 战斗开始。
- 重连后的 Probe 共 `257` 条，全部 `readOnly=true`，`actionsEnqueued=0`、`customNetworkPacketSent=0`；NIBBIT、SLIMES、PHROG 三类战斗均重新取得公开状态和回合变化。
- 重连后的 Advisor 遇到远端私有药水库存未捕获时记录 `MP_ADVISOR_FAIL_CLOSED=7`、`SEARCH_COMPLETE=0`，这是当前 Unknown 私有语义的预期安全边界，不计作 Advisor 搜索通过，也不升级到 Safe Execute。
- 机器摘要中的生命周期、Probe 后续和 fail-closed 细节见 [`evidence/mp1-advisor-smoke-2026-09-19.json`](evidence/mp1-advisor-smoke-2026-09-19.json)。

### MP-1 Advisor 非空远端药水实机复验（2026-09-20）

- 新一轮使用当前 Release DLL（commit `54e3d91`，SHA-256 `864EC2A8276845B0C412106259D75AE2D30333826D415BE03A7371FD3A3B474F`）完成 Host 退出重建房间、Client 重新加入、Ready 和再次进入战斗。
- 远端玩家实际使用了 `FIRE_POTION` 与 `COLORLESS_POTION`。药水仍在远端私有库存时，Advisor 记录 `SEARCH_START=9`、`ROOT_CAPTURE_BEGIN=9`、`FAIL_CLOSED=7`、`SEARCH_SETUP_FAILURE=7`；失败原因为 `Player 1 is outside the captured potion inventory`，符合 Unknown 私有语义的 fail-closed 合同。
- 远端药水被消耗后，generation `45`/`46` 各完成一次当前回合搜索；Probe 共 `259` 条，全部 `readOnly=true`，`actionsEnqueued=0`、`customNetworkPacketSent=0`。总体稳定性仍为 `PARTIAL`，不把“非空远端药水”升级为正向搜索能力。
- 正常退出时游戏写入 `progress.save`、`prefs.save`、`settings.save` 和 `profile.save`；同一实例根目录的 `Roaming`/`Local` 数据可复用，进程重启仍会重新加载 Mod，未保存的当前战斗状态不承诺跨进程恢复。退出阶段另有独立的 `RunManager.ToSave_Patch1` `NullReferenceException`，已与 Advisor 计算失败分开记录。
- 机器摘要见 [`evidence/mp1-advisor-potion-2026-09-20.json`](evidence/mp1-advisor-potion-2026-09-20.json)。

## 下一阶段

MP-0 生命周期证据已收口；固定工作量单人对照已完成受限 spot 验证，非空远端私有药水场景已实机复验并继续按合同 fail-closed，更广 Advisor 稳定性仍为 `PARTIAL`。MP2B 当前受控范围已完成两动作正常与远端干扰 Smoke，默认仍保持 Probe；后续扩大能力边界需要独立计划。
