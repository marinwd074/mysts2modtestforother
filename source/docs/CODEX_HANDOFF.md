# Codex 当前交接

> 本文件是单一当前 handoff；每次任务结束覆盖更新，不追加历史。分支为 `main`，精确提交以当前 HEAD 为准。

## 当前状态

- CombatSolver `0.40.2`，目标游戏 / RitsuLib `0.107.1`，兼容符号 `STS2_01071`。
- MP-0 Core：PASS；MP-0 受控生命周期 Hardening：PASS。
- MP-1 Advisor：受控 Smoke PASS；远端私有药水未知时继续 fail-closed。
- MP-2 Safe Execute：BLOCKED，运行时能力仍不可达；没有 `safe-execute` opt-in。

## 本轮实施

- 新增纯 `MultiplayerSafeExecutePolicy`，把 Safe Execute 的结构、所有权与目标 fail-closed 规则从 native 对象读取中分离。
- 新增 `MultiplayerSafeExecuteChecks` L1 合同入口。
- MP-2A 部署改为一次最多执行 1 张已分类为安全的本地普通 PlayCard；之后必须重新观察/重算。
- 自动 EndTurn、药水、选择、Replay/重复语义、队友目标、MultiplayerOnly 卡、Full Auto、Instant、连续多牌继续禁止。
- `SolverSessionCapabilities.Capture()` 未开放 `MultiplayerSafeExecute`，所以本轮代码仍 dormant，不改变当前玩家行为。
- 固定本文件为 Codex 接手入口；后续每次任务结束覆盖更新，并在聊天末尾输出 Markdown 交接。

## 关键风险

- 当前 `MultiplayerWorldTracker.WorldVersion` 包含本地手牌、能量、Power、敌人 HP/Block 等；本地自己出牌也会推进版本。
- 因此不要直接开放连续多牌。MP-2B 必须先区分“本地预期变化”和“远端并发变化”，否则可能自判 stale 或错误吞掉远端变化。
- 历史 `docs/audits/CombatSolver_full_repo_audit_multiplayer_readiness_2026-09-19.md` 只作历史证据，不是当前多人状态真源。

## 验证要求

- 必须保持 `.github/workflows/compatibility.yml` 的 static-consistency 与 L1 contract-tests 为绿。
- 本轮纯合同通过只能证明 admission policy；不能写成真实多人执行通过。
- 在解除 MP-2A 门禁前，需要真实 Host/Client 单牌 Smoke：恰好一个本地原生 PlayCardAction、零自定义网络包、零自动 EndTurn、零队友控制，并验证世界变化后重新观察/重算。

## 下一步

1. 确认本轮 GitHub Actions 全绿。
2. 在本地有游戏环境时做 Release 构建，但不要仅凭构建解除门禁。
3. 为 Multiplayer Lab 增加 MP-2A 单牌受控场景和机器摘要格式。
4. 真实单牌 Smoke 通过后，再单独决定是否增加显式 `safe-execute` opt-in。
5. MP-2B 连续多牌保持后置；Instant 多人模式继续不启用。
