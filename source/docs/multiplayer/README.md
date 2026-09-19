# Multiplayer 适配阶段

当前阶段：**MP-0 Core 已通过；MP-0 Hardening（生命周期）未完成；MP-1 Advisor 已具备受控验证入口**。

本阶段依据 `Multiplayer Apply` 中的精简功能方案和修正版执行计划实现，目标是先用隔离的双实例完成真实 Host/Client 证据，不改变多人会话语义。

## 当前门禁状态

- **MP-0 Core：PASS**。连接兼容、本地私有状态只读采集、远端公开战斗状态、双 Client 对照和 Probe 只读契约均有证据。
- **MP-0 Hardening：INCOMPLETE**。连接建立后的退出/重新加入闭环仍未捕获；因此完整矩阵仍保持 `UNVERIFIED`，不能把进程停止当作生命周期通过。
- **MP-1 Advisor：READY FOR VALIDATION**。静态合同与 Release 构建已通过；默认仍是 Probe，只有显式设置 `COMBATSOLVER_MULTIPLAYER_MODE=advisor` 才会授予当前回合、本地玩家、只显示路线的搜索能力，绝不会自动执行动作。真实多人 Smoke 尚未完成。
- **MP-2 Safe Execute：BLOCKED**。本地动作分类、原生动作证据和世界版本自变更保护尚未满足。

## 已实现

- `SolverSessionCapabilities`：集中声明单人、多人 Probe、多人 Advisor、多人 Safe Execute 能力；网络多人默认选择 `MultiplayerProbe`，Advisor 必须通过显式环境变量 opt-in，不能由玩家数或网络类型推断。
- `MultiplayerClientProbe`：只读记录本地玩家身份、生命/格挡/能量、回合、手牌、牌堆、药水、敌方公开状态、远端公开玩家摘要和完整 RNG 状态；不搜索、不部署、不改 `CombatState`/RNG、不发送自定义网络包。`Observe` 先用紧凑 fingerprint 判定变化，只有 Lab 证据开启时才生成完整 hard fingerprint。
- Probe 默认只写日志，不在普通 Advisor/桌面运行中产生 JSONL 证据；Multiplayer Lab 显式设置 `COMBATSOLVER_MULTIPLAYER_PROBE_EVIDENCE=1` 后，变化记录才会异步写入实例自有的 `diagnostics/CombatSolver-BugReports/`。证据 schema v2 直接包含 `runSeed` 与 `combatSegmentId`，并保留 16 MiB 上限、序列、`WorldVersion`、本地 Hand/DrawPile/Discard/Exhaust、远端公开摘要、敌人、RNG、MultiplayerScaling、CardMultiplayerConstraint 和只读契约标记。
- `source/tools/multiplayer-lab/prepare-instances.ps1`、`start-host.ps1`、`start-client.ps1`、`stop-owned-instances.ps1` 和 `collect-results.ps1` 提供带 ownership marker 的隔离实例与证据收集；它们不会修改正式 Steam 安装或正式 `MODS`。
- `source/tools/multiplayer-lab/validate-phase0-results.ps1` 只读校验带证据引用的 Host/Client 矩阵和 Probe JSONL；`MP-0A` 只校验连接兼容，`MP-0B` 才要求真实 Probe；它不会启动游戏，也不会解除 Advisor 门禁。
- `MultiplayerWorldTracker`：维护只读观察的 `WorldVersion`、dirty 状态和 200ms 稳定等待窗口；使用紧凑值型 fingerprint 做快速变化检测，只有变化时才生成完整 Describe/JSON 证据。
- Runtime 的搜索、部署、路线接管、全自动、回合开始接管和 Instant 入口统一经过能力表；没有通过实机证据前，网络多人保持关闭。

已把后续 MP-1/MP-2 的受控路径接入源码，但仍由上述 Probe 门禁关闭：

- Advisor/Safe Execute 允许时，`SearchPolicySnapshot.CurrentTurnOnly` 会截断首个本地回合层，关闭跨回合成长目标、远期 Novelty、路线缓存和 continuation reuse。
- `SolverPerspective` 用 `Public/Private/Unknown` 知识语义与 authority 解耦；`CombatRootSnapshot` 在显式多人搜索能力开启时传入 local-player-only capture。`SimulatedCombatState` 与 `CombatPredictionState` 只物化根玩家的私有牌堆、遗物、药水、运行级牌组和 mod card audit，公共玩家名册仍可作为战斗上下文存在，访问未捕获队友私有 combat state、药水或金币会显式失败；远端遗物 hook 若没有针对性的公开语义 capture 也会 fail closed，不能静默丢失队友影响。
- `MultiplayerSafeLocalActionClassifier` 只接受本地手牌的普通 `PlayCard`，目标仅限自身/敌人/无目标；药水、结束回合、选择、重复语义、多人专属卡、远端或未知目标形成连续前缀硬停止。
- 搜索会记录启动时 `WorldVersion`，结果发布时若远端 fingerprint 已变化则丢弃旧结果；能力门禁打开后，Runtime 会取消旧搜索，等待原生动作队列稳定和 debounce，再只启动一次最新当前回合搜索。
- Safe Execute 路径会在每个本地动作前复核 `WorldVersion`；远端世界变化会停止后续动作，不会自动 EndTurn 或跳过不安全动作继续执行。
- 如果会话从单人/过渡态进入多人，控制器会一次性取消活动搜索、部署、全自动和回合开始接管，并清空可部署结果；之后的 Probe fingerprint 变化继续触发失效。

## 当前明确未启用

多人 Safe Execute、简单本地牌自动执行、药水、选择驱动、自动结束回合、Full Auto、Instant、跨回合复用和 Route Repair 均未开放。Advisor 仅通过 `COMBATSOLVER_MULTIPLAYER_MODE=advisor` 显式开启，并仍受本地私有/远端公开 root contract、当前回合和 fail-closed 约束；默认安装保持 Probe，不因玩家数或网络类型自动升级。

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

本仓库已取得 A/B/C 三组连接到首战的成对日志，并取得 D: 新 DLL C 组（Vanilla Host + CombatSolver Client）的最终非空、只读 Probe 证据：339 条记录、4 个战斗段、每张牌带有每场战斗稳定的实例 token，覆盖 81/81 次抽牌匹配、5 次洗牌、70 次弃牌、4 次消耗和 93 次远端摘要变化；配对日志还覆盖 4 场战斗启动、其中 3 场结束后的奖励与地图推进。双 Client 敌人公开状态对照也已通过。连接结果和当前部分状态结果已写入 [`evidence/phase0-matrix-2026-09-19.json`](evidence/phase0-matrix-2026-09-19.json)；退出/重新加入仍未完成，所以完整 MP-0 继续是 `UNVERIFIED`，但这不再阻塞 Advisor 受控实现入口。

为补强敌人公开状态证据，Lab 新增 `compare-probe-public-state.ps1`，并准备了第二个 D: 盘 `ClientCombatSolver` 观察实例；它只比较同一局两个独立 Client Probe，不修改网络协议或自动提升矩阵状态。

当前阻碍和未验证项集中记录在 [`blockers/MP-0-UNVERIFIED-2026-09-19.md`](blockers/MP-0-UNVERIFIED-2026-09-19.md)。

## 下一阶段

继续把退出/重新加入作为 MP-0 Hardening 单独补证；Advisor 维持显式 opt-in，只搜索本地玩家当前回合、只显示路线、不自动执行。只有 Advisor 的固定工作量性能对照、root contract 和实机稳定性完成后，才评估 `SafeLocalAction` 分类器和 MP-2 Safe Execute。
