# Codex 当前交接

> 本文件是单一当前 handoff；每次任务结束覆盖更新，不追加历史。分支为 `main`。

## 当前基线

- MP-2A 实现提交：`fec5f43026378c92a8f8e6ffdea2c81b766f0c57`（`test: add MP-2A lab smoke gate`）。
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

## 当前唯一主要未完成项

当前 ChatGPT 会话能够读写 GitHub 仓库和检查 GitHub Actions，但**没有用户电脑的桌面/Steam/STS2 进程控制能力**。

因此无法在这里直接完成：

- 启动本机 Slay the Spire 2；
- 建立真实 Vanilla Host；
- 启动第二个 CombatSolver Client 游戏实例；
- 手动进入 Lobby / Ready / 战斗；
- 在真实游戏 UI 点击一次“执行本回合”。

这不是代码缺口。

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
