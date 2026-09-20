# Codex 当前交接

> 本文件是单一当前 handoff；每次任务结束覆盖更新，不追加历史。分支为 `main`。

## 当前基线

- MP-2A 实现基线：`b9177a9`（`fix: gate MP2A deployment and preserve local action completion`）。该修复让无选项的安全本地牌在多人 Safe Execute 中直接等待原生动作完成，限制 Overlay 到单牌安全切片，并且只有用户明确点击“执行本回合”才会部署；带选项动作仍由安全策略 fail-closed。
- GitHub Actions run：`35479112564`，`static-consistency=PASS`，`contract-tests=PASS`。
- L1 总结果：`PASS: 8 / FAIL: 0 / SKIP: 0`。
- `MultiplayerSafeExecuteChecks`：21 项通过。
- `MultiplayerSafeExecuteEvidenceChecks`：验证器 PASS/FAIL/UNVERIFIED 三类自测通过。
- CombatSolver `0.40.2`；目标游戏 / RitsuLib `0.107.1`；兼容符号 `STS2_01071`。

## 当前多人状态

- MP-0 Core：PASS。
- MP-0 Hardening lifecycle：PASS。
- MP-1 Advisor：受控 Smoke PASS；远端私有药水未知时继续 fail-closed。
- MP-2 Safe Execute：**正式玩家入口仍 BLOCKED**。
- `safe-execute-lab` 仅用于真实 MP-2A Smoke，不是正式玩家模式。

## 已完成的 MP-2A 基础设施

- 一次 deployment 最多执行 1 张安全本地普通 PlayCard。
- 非 PlayCard、药水、自动 EndTurn、Choice、Replay、MultiplayerOnly 卡、队友/未知目标全部 fail-closed。
- Full Auto、Fast/Instant 覆盖、跨回合搜索/复用继续关闭。
- Lab-only capability gate 必须同时满足：
  - `COMBATSOLVER_MULTIPLAYER_MODE=safe-execute-lab`
  - Probe evidence 已启用
  - `COMBATSOLVER_MULTIPLAYER_INSTANCE` 指向 Lab instance
  - `instance.json` / `multiplayer-profile.json` schema 与 runtimeRoot 匹配
  - profile 精确为 `ClientCombatSolver`
- 正式 `safe-execute` token 明确不能授权。
- Safe Execute 自动搜索只展示建议，不会因 `CurrentTurnAdoption` 自动出牌；显式执行请求单独记录，避免最后一张牌在未点击时被部署。
- Safe Execute 原生牌入队期间允许该牌自身造成的 WorldVersion 变化穿过监控取消边界；远端/非预期变化仍取消部署。
- Lab launcher 已支持 `-MultiplayerMode safe-execute-lab`，且非 ClientCombatSolver 实例会被拒绝。
- Safe Execute 运行时记录：
  - `LAB_CAPABILITY`
  - `MP2A_DEPLOY_START`
  - `NATIVE_ACTION_CAPTURED type=PlayCardAction`
  - `DEPLOY_END ... end_turn=false`
  - `MP2A_WORLD_CHANGED`
  - 后续 `SEARCH_DEBOUNCED_START`
- 单牌原生动作完成后 deployment 立即收束，不会等待或执行路线中的第二张牌。
- 主线程随后重新观察世界；WorldVersion 改变会失效旧结果并触发新的 debounce 搜索。

## 真实 Smoke 验证器

入口：

`source/tools/multiplayer-lab/validate-mp2a-results.ps1`

PASS 必须同时满足：

1. 观察到 Lab capability。
2. 恰好一个单牌 MP-2A deployment。
3. 恰好捕获一个原生 `PlayCardAction`。
4. CombatSolver 该执行路径未使用自定义网络 API。
5. 无 Potion / 自动 EndTurn 证据。
6. 动作后的 WorldVersion 大于搜索时版本。
7. 世界失效后启动新的 debounced search。

验证器可以输出机器 JSON 摘要。缺证据返回 `UNVERIFIED`，矛盾证据返回 `FAIL`。

`custom_network_api_used=false` 只证明 CombatSolver 该执行路径使用原生动作链，不等同独立抓包工具的 wire capture。

## 最近一次 MP-2A Lab 运行（2026-09-20）

- 修复前源码 `a50673e` 已完成 Release 构建；Host/Client 使用同一构建产物。
- Host/Client 已按 Lab 流程启动并进入 `NIBBITS_WEAK` 多人战斗；Client 已加载 RitsuLib 与 CombatSolver。
- `runtime-mp2a-client-20260920-fix1` 的 CombatSolver journal 观察到 `LAB_CAPABILITY`，但每当路线超过 1 张牌时都抛出 `MAIN_THREAD_CALLBACK_FAILURE`：`部署动作数为 1，Overlay 动作胶囊数为 2/3`，所以第一次点击通常无反应。
- 当路线恰好切到单牌时，journal 能捕获原生 `PlayCardAction`，但牌自身造成的 WorldVersion 变化又触发 `DEPLOY_CANCELED`，没有 `DEPLOY_END`；这对应“手动打前几张后最后一张自动/无反应”的现象。
- 日志还证明未点击“执行本回合”时，`CurrentTurnAdoption` 结果会直接调用 `StartDeployment`，导致最后一张牌自动部署。以上三类错误均已在 `b9177a9` 修复；`fix1` 结果为 `FAIL`，不是 MP-2A PASS。
- 本轮机器摘要留在 `.local/multiplayer-lab/results/mp2a-20260920-fix1-summary.json`，journal 留在 `runtime-mp2a-client-20260920-fix1/diagnostics/CombatSolver-BugReports/logs/CombatSolver/21664-8e3d1237e7c34f49b7129968c3d57029/combat-a518725aebe94412bf0799f37cc327a5.jsonl`；不作为正式 multiplayer evidence。
- 验证器已排除普通网络/手动 `EndPlayerTurnAction` 的误报，只把 CombatSolver 自己的自动结束回合标记视为禁用动作证据；`test-mp2a-validator.ps1` 当前 `checks=4` 通过。

## 本轮 MP-2A Smoke（2026-09-20，`fix2`）

- Host/Client 使用 `b9177a9` 的同一 Release 构建；Client 实例为 `ClientCombatSolver`，运行参数为 `safe-execute-lab`。本轮 Host/Client 已在记录后停止，未遗留 owned 进程。
- 未点击“执行本回合”时，截图 `/.local/multiplayer-lab/screen-fix2.png` 显示手牌未变化，未发生自动出牌；点击一次后，`/.local/multiplayer-lab/screen-fix2-after-click.png` 显示“执行完成／已打出 1 张牌”。
- 原生游戏日志 `runtime-mp2a-client-20260920-fix2/logs/20260920-103058-client-1d722745.log` 记录了一次 Client 请求、入队、执行并完成 `PlayCardAction card: CARD.DEFEND_IRONCLAD`（约 02:36:31）；没有 CombatSolver 自定义网络动作。
- Probe `runtime-mp2a-client-20260920-fix2/diagnostics/CombatSolver-BugReports/logs/CombatSolver/multiplayer-probe-3680-598e16dc7f6f4440bb18ecb4f0537fad.jsonl` 的序列 7→9 显示 `world_version` 7→9、能量 3→2、格挡 0→5，`DEFEND_IRONCLAD instance=2` 从手牌进入弃牌；`customNetworkPacketSent=false`。
- 这次只能记为行为 Smoke `UNVERIFIED`：combat journal 与 process journal 均为 0 字节，`validate-mp2a-results.ps1` 因缺少 Solver 事件链返回 `UNVERIFIED`。原因是本轮停止脚本强制终止进程，异步 journal 尚未落盘；该结果不能证明产品行为失败，也不能替代正式 PASS。

## 当前唯一主要未完成项

真实 MP-2A 单牌 Smoke 仍未取得 PASS。`b9177a9` 的行为修复已由本轮截图、原生游戏日志和 Probe 支持，但缺少已落盘的 Solver 事件链。下一次应使用优雅退出（或先让 journal 完成落盘）重新验证两条互斥行为：未点击时不出现 `MP2A_DEPLOY_START`/原生动作，明确点击一次后恰好出现一个 `NATIVE_ACTION_CAPTURED`、`DEPLOY_END`、WorldVersion 失效和新搜索。没有这条完整事件链时，不能升级正式 Safe Execute 入口。

## 下一步实机步骤

在有 STS2 安装的本地仓库环境：

1. 用当前源码做 Release build。
2. 用 `prepare-instances.ps1` 准备 Vanilla Host 与 ClientCombatSolver。
3. 启动 Host。
4. Client 使用：
   `-MultiplayerMode safe-execute-lab`
5. 正常进入一场简单多人战斗。
6. 等求解路线稳定。
7. 只点击一次“执行本回合”。
8. 不再点击，保持 Client 数秒，让 Probe 记录 WorldVersion 变化和重新搜索。
9. 停止 owned instances。
10. 对本次 Client `logPath` 执行：
    `validate-mp2a-results.ps1`
11. 只有结果为 `PASS` 才把摘要写入正式 multiplayer evidence。

## 不要做

- 不要把 `safe-execute-lab` 改成正式 `safe-execute`。
- 不要开放连续多牌 MP-2B。
- 不要通过执行后直接重置 WorldVersion 来规避并发归因。
- 不要开放 Multiplayer Instant、自动 EndTurn、药水、Choice 或 Full Auto。
- 合成 validator 自测不能当实机多人证据。

## 后续门槛

只有真实 Host/Client MP-2A 单牌 Smoke 取得 PASS 后，才讨论正式 Safe Execute opt-in。

MP-2B 连续多牌必须另行设计“本地预期变化 vs 远端并发变化”的 WorldVersion 来源归因。
