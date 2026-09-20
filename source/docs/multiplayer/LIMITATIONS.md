# Multiplayer 当前限制与验证事实（2026-09-20）

本文件只保留当前多人阶段的可审计结论和仍有效的限制。机器事实以 [Phase 0 矩阵](../evidence/phase0-matrix-2026-09-19.json) 为准：MP-0 Core 与受控生命周期 Hardening 为 `PASS`；重连后的 Advisor 远端私有药水语义仍按合同 fail-closed，MP-2 继续 blocked。

## 当前状态

- **MP-0 Core：PASS**：A/B/C 连接矩阵、本地私有状态只读采集、远端公开战斗状态、双 Client 公共敌人状态对照和 Probe 只读契约均有证据。
- **MP-0 Hardening：PASS（受控生命周期）**：Host 退出并重新创建房间后，Client 收到 Quit、重新握手、Join、Ready，并再次进入有效战斗；进程停止不计作生命周期证据。
- **MP-1 Advisor：SMOKE PASS（受控范围）**：静态合同与 Release 构建已通过；默认仍是 Probe，只有 `COMBATSOLVER_MULTIPLAYER_MODE=advisor` 才进入当前回合的只读路线显示，不执行动作。fresh `-bbfix` client 已形成 `SEARCH_COMPLETE=5`、原生完成通知和路线回放证据，Probe 保持只读；远端私有字段保持 `Unknown`，未知远端遗物仍 fail closed。
- **MP-2 Safe Execute：BLOCKED**：本地动作分类、世界版本自变更和原生动作证据仍未满足。

## AB 组连接实机结果（2026-09-19）

- `HostVanilla + ClientVanilla`、`HostVanilla + ClientRitsuOnly`、`HostVanilla + ClientCombatSolver` 均完成 Lobby、Ready、MapCoord `(3,0)`、战斗创建和 `Combat started`；对应归档仍按矩阵登记。
- 这些结果证明连接兼容入口，不把缺少生命周期闭环误报为完整 MP-0 PASS。

## C 组非空 Probe 实机结果（2026-09-19）

- Vanilla Host + CombatSolver Client 的最终新 DLL 归档包含 `339` 条 Probe 记录、`4` 个战斗段；牌 token、抽牌前缀、洗牌、Discard/Exhaust、敌人变化和远端摘要变化均有直接证据。
- `339/339` 条记录保持 `readOnly=true`、`searchStarted=false`、`actionsEnqueued=false`、`customNetworkPacketSent=false`；本轮未启用 Advisor/Safe Execute。

## 双 Client 对照最终收尾快照（2026-09-19）

- Vanilla Host 同时承载 Client A `netId=1000` 和 Client B `netId=1001`；两端分别记录 `234` / `243` 条 Probe，合计 `477/477` 条满足只读契约。
- 独立 Client-to-Client 的公开敌人状态报告为 `PASS`，三段同 Seed、状态差集均为 `0`。这不是自定义 Host 协议证据。
- Host、A、B 均记录三场战斗开始、两场结束；该历史双 Client 快照不包含生命周期，但最新 Advisor follow-up 已单独完成 Host 重建房间后的 Client 重新加入闭环。

## MP-1 Advisor 首轮 Smoke（2026-09-19）

- Vanilla Host 与 Advisor Client 成功完成 Lobby、Ready、MapCoord 和普通战斗创建；Client 端加载 CombatSolver `0.40.2`，63 个补丁全部成功。
- Advisor combat log 记录 `MP_ADVISOR_SEARCH_START=16`、`MP_ADVISOR_SEARCH_COMPLETE=0`、`MP_ADVISOR_FAIL_CLOSED(root_capture)=16`。每次失败均为 `PredictionUnsupportedException`：远端玩家的 `RELIC.BURNING_BLOOD` hook 尚无公开语义 capture。
- Client Probe 本轮 134/134 条仍为 `readOnly=true`，且 `searchStarted=false`、`actionsEnqueued=false`、`customNetworkPacketSent=false`；未产生 Solver 动作或自定义网络包。
- 已修正多人诊断路径的单人假设：`CombatReplayOutcome` 改为观察本地玩家；该修复已提交并推送。当前仍保持远端遗物 fail-closed，不把本轮记为 Advisor Smoke PASS。

## MP-1 Advisor BurningBlood 语义审计与修复（2026-09-19）

- STS2 `0.107.1` 原生 `BurningBlood` 只声明 `AfterCombatVictory(CombatRoom)`；其状态机只在胜利后检查持有者死亡、闪烁并治疗持有者，不影响 CurrentTurnOnly 搜索中的 combat hook、敌人或当前评分。
- `afa6a64` 只允许精确 `BurningBlood` 从远端 root listener 表省略；未知远端遗物仍由 `RootUnsupportedRemotePublicRelicListenerCount` fail closed，未捕获的远端 `RelicsOf(remote)` 仍抛出，避免私有清单读取和 live listener 泄漏。
- `MultiplayerRootCaptureChecks` 4 项通过；新 DLL SHA-256 为 `507FAFDCAF72E3E56ED537BCB3A4B15BCC99A63277308F3714B910C2BBA85E11`。新的 Host/Client 快照目录带 `-bbfix` 后缀，真实 Advisor Smoke 仍待手动进入战斗复验。

## MP-1 Advisor side-turn / block scaling 修复（2026-09-19）

- BurningBlood 修复后的首轮实机已进入 combat，但 generation 6 报告 `RelicsOf(remote)` 被 side-turn relic phase 误访问，generation 7 报告多人 block mirror 对本地 `DEFEND` 过早抛出 single-player guard；两者均保持 fail-closed，未发布路线。
- `ffad49b` 只让 side-turn relic 遍历参与且已捕获玩家；原生 block mirror 复刻 enemy/powered-block early-exit，并复用原生 scaling table。未知远端 turn 仍抛出 `PredictionUnsupportedException`，没有读取远端私有遗物。
- 新 DLL SHA-256 为 `70FA663D661056317093EE9F6FAFE7FA37699FEB3681B16FF5B9FD420A6D384C`；`-bbfix` client 已替换并重启，下一轮真实 Smoke 随后由 `37592ca` 继续修正并完成。
- `37592ca` 进一步把 EndTurn replay 的玩家阶段限制为 `RootCapturedPlayers`，修正 local-player-only root 对远端私有 combat state 的 materialize；当前 runtime DLL SHA-256 为 `281A286A109F4A2EC428290E0CAEF9B05A034DE837C6793533590EF695BC4A75`，client 已于 23:17 重启。

## MP-1 Advisor 真实 Smoke PASS（2026-09-19）

- 当前 combat log：`SEARCH_START=10`、`ROOT_CAPTURE_BEGIN=10`、`SEARCH_COMPLETE=5`、`SEARCH_STALE=1`、`FAIL_CLOSED=0`、`SEARCH_FAILURE=0`；完成通知为 `kind=Succeeded native=shown`，并有 `ROUTE_REPLAY` / `ROUTE_ACTION` / `UI_STATE=ready` 记录。
- 当前 Probe 共 `51` 条，`readOnly=true` 为 `51/51`，`actionsEnqueued=true` 为 `0`，`customNetworkPacketSent=true` 为 `0`。路线动作是模拟回放记录，不是 live action enqueue。
- 证据文件：`.local/multiplayer-lab/runtime-mp-advisor-client-20260919-bbfix/diagnostics/CombatSolver-BugReports/logs/CombatSolver/40228-d303f91cf2004ad5a218de87817320ee/`；运行中的 Host PID `38548` 与 Client PID `40228` 均正常响应。

## MP-0 生命周期与重连后 Advisor 边界（2026-09-19）

- Host 日志记录 `Stopping host. Reason: Quit`、Client 断开、新握手、`ClientLoadJoinRequestMessage`、Ready、run load 和 `Combat started`；Client 日志同步记录 Quit、重新 Join、epoch 4、Ready、run load 和 `PHROG_PARASITE_ELITE` 战斗开始。证据见 [`evidence/phase0-matrix-2026-09-19.json`](../evidence/phase0-matrix-2026-09-19.json) 与 [`evidence/mp1-advisor-smoke-2026-09-19.json`](../evidence/mp1-advisor-smoke-2026-09-19.json)。
- 重连后的 Probe `257/257` 保持 `readOnly=true`，`actionsEnqueued=0`、`customNetworkPacketSent=0`；NIBBIT、SLIMES、PHROG 战斗均重新取得公开状态和回合变化。
- 重连后的 Advisor 记录 `MP_ADVISOR_SEARCH_START=7`、`ROOT_CAPTURE_BEGIN=7`、`FAIL_CLOSED=7`、`SEARCH_COMPLETE=0`，原因是远端私有药水库存不可见。这是当前 Unknown 私有语义的预期 fail-closed 边界，不把该轮误记为 Advisor 搜索通过。

## MP-1 Advisor Stability 新一轮（2026-09-20）

- 当前源码构建（commit `1b6028502ab00e9c1394e51ffd3cb1661fcd533c`，DLL SHA-256 `864EC2A8276845B0C412106259D75AE2D30333826D415BE03A7371FD3A3B474F`）在新的隔离 Host/Client 上完成了 Host 重建房间后的 Client 重新加入，并再次进入 `SLIMES_WEAK` 战斗。
- 重连后的 combat journal 记录 `SEARCH_START=4`、`ROOT_CAPTURE_BEGIN=4`、`SEARCH_COMPLETE=3`、`FAIL_CLOSED=0`、`SEARCH_FAILURE=0`；成功路线有 `ROUTE_REPLAY=3`、`ROUTE_ACTION=7`、`UI_STATE=ready=3`。1 个 generation 在 world invalidation/debounce 期间未发布，不把它计作成功。
- Probe `130/130` 为 `readOnly=true`，动作入队和自定义网络包均为 `0`；本轮本地药水槽为空，因此只证明“无远端私有药水”的受控重连稳定性，不覆盖既有的非空远端私有药水阻碍。机器摘要见 [`evidence/mp1-advisor-stability-2026-09-20.json`](../evidence/mp1-advisor-stability-2026-09-20.json)。

## MP-1 Advisor 非空远端药水复验（2026-09-20）

- 新一轮按游戏规则完成 Host 退出、重建房间，Client 重新加入、Ready 并再次进入战斗；远端玩家实际使用了 `FIRE_POTION` 与 `COLORLESS_POTION`。
- 远端私有药水仍存在时，Advisor 的 `generation=38..44` 均在 root capture 以 `InvalidOperationException: Player 1 is outside the captured potion inventory` fail-closed；记录为 `SEARCH_START=9`、`ROOT_CAPTURE_BEGIN=9`、`FAIL_CLOSED=7`、`SEARCH_SETUP_FAILURE=7`。这证明阻碍场景已被实际覆盖，且没有通过读取或猜测远端私有库存来绕过门禁。
- 远端药水消耗后，`generation=45`/`46` 成功完成当前回合搜索；Probe `259/259` 仍只读，无动作入队或自定义网络包。机器摘要见 [`evidence/mp1-advisor-potion-2026-09-20.json`](../evidence/mp1-advisor-potion-2026-09-20.json)。
- 正常退出时游戏写入 `progress.save` 等实例存档；退出收尾另出现 `RunManager.ToSave_Patch1` 经 `CombatBugReportExporter` 的 `NullReferenceException`，这是独立的诊断/存档导出问题，未改变前述 Advisor fail-closed 结论。

## 当前限制

- 直接 Host 逐时刻敌人公开状态导出仍未单独采集；当前 `enemyStateSync` 仅表示两个独立 CombatSolver Client 的公开状态集合对照。
- MP-1 Advisor 的首轮真实 Smoke 已通过受控验收；无药水重连场景和非空远端药水 fail-closed 场景均已实机覆盖；固定工作量单人 post-MP1 spot 对照已完成且路线/工作量无回归，但更广稳定性仍待收口，未知远端遗物和远端私有药水的 fail-closed 门禁不可移除。对照证据见 `runtime-evidence/20260920-post-mp1-performance/`。
- 重连后的远端私有药水库存仍不可访问，Advisor 必须保持 fail-closed；如需支持正向搜索语义，应另立受控 public-state 设计与合同，不在本次 MP-0 生命周期收口中静默放开。退出阶段的 `CombatBugReportExporter` `NullReferenceException` 另需独立 triage。
- MP-2 Safe Execute 的**正式玩家入口仍不可达**。当前只增加 `safe-execute-lab` 受控测试入口：必须由 Multiplayer Lab 启动、存在匹配的 `ClientCombatSolver` ownership/profile marker 且 Probe evidence 已启用；普通桌面进程和 `safe-execute` token 均不会获得能力。Lab 内一次 deployment 最多执行 1 张已分类为安全的本地普通 PlayCard，然后停止并等待新的世界观察/搜索。Local EndTurn、药水、选择、Replay、队友目标和连续快速动作均未启用；连续多牌仍需要区分本地预期状态变化与远端并发变化。

## 当前安全边界

- Runtime 默认 `MultiplayerProbe`：只读采集，不搜索、不部署、不自动选牌、不自动 EndTurn、不发送自定义网络包。
- Advisor 仅显式环境变量 opt-in，并受当前回合、root capture contract 和远端 fail-closed 语义约束；`MultiplayerSafeExecute` 不可用。
- 证据文件仅由 Lab 环境写入；schema v2 使用 `runSeed` / `combatSegmentId`，紧凑 fingerprint 不能替代缺失的生命周期证据。

## Source of truth

- [Phase 0 机器矩阵](../evidence/phase0-matrix-2026-09-19.json)
- [多人适配入口](../README.md)
- 旧过程日志和完整快照保留在 Git history，不再作为当前事实入口。
