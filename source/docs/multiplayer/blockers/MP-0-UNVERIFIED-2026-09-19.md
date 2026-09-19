# MP-0 当前阻碍与事实（2026-09-19）

本文件只保留当前多人阶段的可审计结论和仍未完成的阻碍。机器事实以 [Phase 0 矩阵](../evidence/phase0-matrix-2026-09-19.json) 为准：MP-0 Core 为 `PASS`，Hardening 为 `INCOMPLETE`，完整矩阵继续为 `UNVERIFIED`。

## 当前状态

- **MP-0 Core：PASS**：A/B/C 连接矩阵、本地私有状态只读采集、远端公开战斗状态、双 Client 公共敌人状态对照和 Probe 只读契约均有证据。
- **MP-0 Hardening：INCOMPLETE**：连接建立后的退出/重新加入闭环尚未捕获；第三场战斗在最终归档时尚未结束，进程停止不计作生命周期证据。
- **MP-1 Advisor：READY FOR IMPLEMENTATION**：默认仍是 Probe；只有 `COMBATSOLVER_MULTIPLAYER_MODE=advisor` 才进入当前回合的只读路线显示，不执行动作。远端私有字段保持 `Unknown`，未建模遗物 hook fail closed。
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
- Host、A、B 均记录三场战斗开始、两场结束；未捕获连接后的干净退出/重新加入闭环，因此 lifecycle 仍为 `UNVERIFIED`。

## Active blockers

- 补齐连接建立后的退出/重新加入，并在重新加入后再次取得 Probe 或 Lobby 顺序证据；在此之前不要把完整矩阵升级为 PASS。
- 直接 Host 逐时刻敌人公开状态导出仍未单独采集；当前 `enemyStateSync` 仅表示两个独立 CombatSolver Client 的公开状态集合对照。
- Local PlayCard、Local EndTurn 和连续快速动作属于后续 MP-2 Safe Execute，不在本阶段启用。

## 当前安全边界

- Runtime 默认 `MultiplayerProbe`：只读采集，不搜索、不部署、不自动选牌、不自动 EndTurn、不发送自定义网络包。
- Advisor 仅显式环境变量 opt-in，并受当前回合、root capture contract 和远端 fail-closed 语义约束；`MultiplayerSafeExecute` 不可用。
- 证据文件仅由 Lab 环境写入；schema v2 使用 `runSeed` / `combatSegmentId`，紧凑 fingerprint 不能替代缺失的生命周期证据。

## Source of truth

- [Phase 0 机器矩阵](../evidence/phase0-matrix-2026-09-19.json)
- [多人适配入口](../README.md)
- 旧过程日志和完整快照保留在 Git history，不再作为当前事实入口。
