# Codex 当前交接

> 本文件只保留当前可执行状态；分支为 `main`。

## 当前结论

- CombatSolver `0.40.2`，目标游戏 / RitsuLib `0.107.1`，兼容符号 `STS2_01071`。
- MP-0 Core / lifecycle：PASS。
- MP-1 Advisor：受控 Smoke PASS。
- MP-2 正式 Safe Execute：仍 BLOCKED。
- MP-2A Lab：单牌行为已有真实支持证据，但正式事件链仍需重新取得 PASS。
- MP-2B / Multiplayer Instant：BLOCKED。

## 本轮排查与修复

当前测试问题分成两类：

1. 旧的 MP-2A 运行问题已由 `b9177a9` 修复：多牌路线与单牌 Overlay 胶囊不一致、自己出牌造成 WorldVersion 后取消 deployment、未点击执行却被 CurrentTurnAdoption 自动部署。
2. 本轮继续修复测试/授权层问题：
   - Multiplayer Lab 原先默认 `Stop-Process -Force`，会截断异步 journal。
   - `CombatDiagnosticJournal.Dispose()` 原先只关闭 Channel，不等待后台 writer 排空。
   - Safe Execute 的显式执行授权原先过早写入裸 bool；当 `CanSolve` 拒绝时可能遗留过期授权。
   - MP-2A 部署 UI 仍显示“完成后结束本回合”，与禁止自动 EndTurn 的合同冲突。

当前修复：

- Lab 停止默认改为 `Graceful`：关闭主窗口并等待正常退出；失败时保留进程并报错，不自动切到 Force。
- `-Mode Force` 仍保留，但必须显式指定，且不能把被强杀运行当成完整 journal 证据。
- journal 正常 Dispose 时给后台 writer 最多 2 秒有界排空窗口；正常战斗期间 producer 仍不等待磁盘 I/O。
- `DiagnosticLogTests` 新增 Dispose 尾事件落盘断言，并加入 L1 contract suite；合同入口由 8 个变为 9 个。
- Safe Execute 只有在 turn-setup 等待被实际接受，或当前状态通过 `CanSolve` 后才武装显式部署请求；拒绝路径不会留下旧授权。
- 部署 UI 根据会话能力显示“结束本回合”或“保持当前回合”。

## 仍需实机验证

下一轮使用全新的 Lab instance：

1. 当前源码 Release build。
2. Vanilla Host + ClientCombatSolver。
3. Client 使用 `-MultiplayerMode safe-execute-lab`。
4. 进入简单多人战斗，先确认不点击时不会出牌。
5. 只点击一次“执行本回合”。
6. 等待动作完成、WorldVersion 变化及新搜索。
7. 使用默认 `stop-owned-instances.ps1` 优雅退出，不加 `-Mode Force`。
8. 对 Client journal 执行 `validate-mp2a-results.ps1`。
9. 七项检查全部 PASS 后，才能写入正式 multiplayer evidence。

## 不要做

- 不开放正式 `safe-execute`。
- 不开放 MP-2B 连续多牌。
- 不用重置/rebase WorldVersion 掩盖并发归因。
- 不开放 Multiplayer Instant、自动 EndTurn、Potion、Choice、Full Auto。
- 不把强制终止或合成 validator 日志算成真实多人 PASS。
