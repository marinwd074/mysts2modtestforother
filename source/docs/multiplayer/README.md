# Multiplayer 适配阶段

当前阶段：**MP-0 / Client-only Read-only Probe**。

本阶段依据 `Multiplayer Apply` 中的精简功能方案实现，目标是先观察原生多人客户端能稳定看到的本地状态，不改变多人会话语义。

## 已实现

- `SolverSessionCapabilities`：集中声明单人、多人 Probe、多人 Advisor、多人 Safe Execute 能力；当前网络多人固定选择 `MultiplayerProbe`。
- `MultiplayerClientProbe`：只读记录本地玩家身份、生命/格挡/能量、回合、手牌、牌堆、药水、敌方公开状态、远端公开玩家摘要和完整 RNG 状态；不搜索、不部署、不改 `CombatState`/RNG、不发送自定义网络包。`Observe` 返回硬 fingerprint 是否变化。
- `MultiplayerWorldTracker`：维护只读观察的 `WorldVersion`、dirty 状态和 200ms 稳定等待窗口；提供不消费的稳定读取与按版本确认接口。
- Runtime 的搜索、部署、路线接管、全自动、回合开始接管和 Instant 入口统一经过能力表；没有通过实机证据前，网络多人保持关闭。

已把后续 MP-1/MP-2 的受控路径接入源码，但仍由上述 Probe 门禁关闭：

- Advisor/Safe Execute 允许时，`SearchPolicySnapshot.CurrentTurnOnly` 会截断首个本地回合层，关闭跨回合成长目标、远期 Novelty、路线缓存和 continuation reuse。
- `CombatRootSnapshot` 在显式多人搜索能力开启前拒绝构造当前仍会捕获完整玩家集合的模拟根；local-player-only root 尚未完成队友牌堆/隐藏状态隔离，不会仅凭 `Players.Count` 放开 Beam。
- `MultiplayerSafeLocalActionClassifier` 只接受本地手牌的普通 `PlayCard`，目标仅限自身/敌人/无目标；药水、结束回合、选择、重复语义、多人专属卡、远端或未知目标形成连续前缀硬停止。
- 搜索会记录启动时 `WorldVersion`，结果发布时若远端 fingerprint 已变化则丢弃旧结果；能力门禁打开后，Runtime 会取消旧搜索，等待原生动作队列稳定和 debounce，再只启动一次最新当前回合搜索。
- Safe Execute 路径会在每个本地动作前复核 `WorldVersion`；远端世界变化会停止后续动作，不会自动 EndTurn 或跳过不安全动作继续执行。

## 当前明确未启用

多人 Advisor 搜索、简单本地牌自动执行、药水、选择驱动、自动结束回合、Full Auto、Instant、跨回合复用和 Route Repair 均未开放。定义好的 Advisor/Safe Execute 能力及其代码路径仍由真实 Host/Client 证据门禁关闭，不能由玩家数或网络类型自动推断启用；当前新增的 debounce/失效代码也只在能力门禁打开后生效。

## MP-0 通过条件

需要在真实 `Vanilla Host + RitsuLib/CombatSolver Client` 矩阵中逐项验证：

1. Host 能接受未安装 CombatSolver 的客户端。
2. 客户端能唯一识别本地玩家、NetId 和战斗身份。
3. 本地手牌、DrawPile 顺序、Discard/Exhaust、药水、敌人公开状态和 RNG counter 稳定可读。
4. 抽牌、洗牌、插牌后，客户端观察与实际本地抽牌一致。
5. 队友动作可被观察为 world fingerprint 变化，且探针全程不修改真实战斗。
6. Lobby、战斗开始/结束、下一层、退出和重新加入不破坏上述边界。

本仓库当前没有 Host/Client 实机证据，因此本阶段不能把 MultiplayerProbe 改成 Advisor，也不能解除多人硬门禁。

## 下一阶段

先补齐并保存 MP-0 实机证据，再将能力表切到 Advisor：只搜索本地玩家当前回合、只显示路线、不自动执行。只有 Advisor 稳定后，才评估 `SafeLocalAction` 分类器和远端变化防抖的实际接入。
