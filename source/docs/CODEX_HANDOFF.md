# Codex 当前交接

> 本文件是单一当前 handoff；每次任务结束覆盖更新，不追加历史。分支为 `main`，精确提交以当前 HEAD 为准。

## 当前状态

- CombatSolver `0.40.2`，目标游戏 / RitsuLib `0.107.1`，兼容符号 `STS2_01071`。
- MP-0 Core：PASS；MP-0 受控生命周期 Hardening：PASS。
- MP-1 Advisor：受控 Smoke PASS；远端私有药水未知时继续 fail-closed。
- MP-2 Safe Execute：正式玩家入口仍 BLOCKED。
- 新增的 `safe-execute-lab` 只允许 Multiplayer Lab 创建的 `ClientCombatSolver` 私有实例进入 MP-2A；普通桌面运行与 `safe-execute` token 均不授权。

## 本轮实施

- MP-2A 安全策略继续限制一次 deployment 最多 1 张本地普通 PlayCard。
- 新增 Lab-only capability gate：必须同时满足：
  - mode = `safe-execute-lab`
  - `COMBATSOLVER_MULTIPLAYER_PROBE_EVIDENCE` 已启用
  - `COMBATSOLVER_MULTIPLAYER_INSTANCE` 指向真实 Lab instance
  - `instance.json` 与 `multiplayer-profile.json` schema/runtimeRoot 匹配
  - profile = `ClientCombatSolver`
- `safe-execute` 字符串明确不能授予能力。
- Safe Execute 捕获实际原生动作时记录 `NATIVE_ACTION_CAPTURED type=PlayCardAction`。
- 单牌动作完成后立即结束 deployment，不等待下一动作；随后由主线程 Probe 观察世界变化、失效旧搜索并 debounce 新搜索。
- 新增 `validate-mp2a-results.ps1`：从真实 Client log 生成 MP-2A 机器摘要。
- 新增 `test-mp2a-validator.ps1`：仅测试验证器自身，合成日志不算实机证据。
- Lab launcher 已支持 `-MultiplayerMode safe-execute-lab`，并拒绝在非 `ClientCombatSolver` 实例启用。
- 自动 EndTurn、药水、Choice、Replay、队友目标、MultiplayerOnly 卡、Full Auto、Instant、连续多牌继续禁止。

## MP-2A 真实 Smoke 判定

`validate-mp2a-results.ps1` 只有同时观察到以下条件才返回 PASS：

1. Lab-only capability marker。
2. 恰好一个 MP-2A deployment。
3. 恰好一个原生 `PlayCardAction`。
4. CombatSolver 路径没有调用自定义网络 API。
5. 没有 Potion / 自动 EndTurn 证据。
6. 动作后 `WorldVersion` 大于搜索时版本。
7. 世界失效后启动新的 debounced search。

注意：`custom_network_api_used=false` 证明 CombatSolver 该路径只走原生动作链，不等同于独立抓包工具的 wire capture。

## 当前仍缺的证据

当前会话无法启动用户电脑上的 Steam / STS2 GUI，也无法实际创建两个真实游戏进程。

因此现在只剩：

- 用本地游戏环境做一次 Vanilla Host + CombatSolver Client 真实运行；
- Client 使用 `-MultiplayerMode safe-execute-lab`；
- 进入战斗后只点击一次“执行本回合”；
- 保持数秒等待 WorldVersion 观察与新搜索；
- 对该次 Client log 跑 `validate-mp2a-results.ps1`；
- 若 PASS，再把机器摘要作为新的 runtime evidence / multiplayer evidence 收口。

在这一步完成前，不得把 MP-2A 写成真实支持或开放正式玩家入口。

## 关键风险

- 当前 WorldVersion 同时覆盖本地和远端可见状态，因此 MP-2B 连续多牌仍无法安全归因。
- 不要通过“执行后简单重置 WorldVersion”绕过该问题。
- Instant 多人模式继续保持关闭。

## 下一步

1. 等本轮 GitHub Actions 全绿。
2. 在有 STS2 本地安装的环境做 Release build。
3. 按 `source/tools/multiplayer-lab/README.md` 执行一次 MP-2A 单牌真实 Smoke。
4. 将验证器 JSON PASS 摘要写入多人 evidence。
5. 只有上述证据完成后，再决定是否设计正式 Safe Execute opt-in。
