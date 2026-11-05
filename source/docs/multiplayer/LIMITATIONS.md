# Multiplayer 当前能力与限制

本文件只记录当前能力合同。旧 MP-0/MP-1/MP-2 日志与阶段结论从 Git history 恢复；当前待复验项统一见 [CODEX_HANDOFF](../CODEX_HANDOFF.md#当前未验证边界)，验证入口见 [TEST_MATRIX](../TEST_MATRIX.md#multiplayer)。历史 PASS 不代表当前构建已经复跑。

## 会话权限

`COMBATSOLVER_MULTIPLAYER_MODE` 控制会话权限；它与路线评分策略的开关是两件事。

| 模式 | 激活条件 | 能力 |
|---|---|---|
| Probe | 未设置或未匹配受支持模式 | 只读观察；不搜索、不执行 |
| Advisor | 显式 `advisor` | 本地建议、未来回合规划与严格续用；不执行 |
| Safe Execute | 显式 `safe-execute` | 搜索、本地安全动作、计划内 Choice、本地药水、安全 EndTurn 与独立 Safe Auto |
| Lab Safe Execute | `safe-execute-lab` + Probe evidence + 合法受管理 Client 实例 | 同一安全执行合同；不向普通实例授予 Lab 权限 |

真源为 [`SolverSessionCapabilities.cs`](../../src/Runtime/SolverSessionCapabilities.cs) 与 [`MultiplayerSafeExecutePolicy.cs`](../../src/Runtime/MultiplayerSafeExecutePolicy.cs)。Safe Execute 授权在进程内有惰性缓存，切换启动环境后应重启实例。

## 搜索与队友状态

- 获准搜索后默认使用 **local-single-core**：真实多人战斗根、本地单人质量轴、共享 WorldVersion 与本地执行权限。
- `UseMultiplayerPrediction` 默认关闭；Team Objective、teammate forecast、Scenario Robust 仅属于显式实验预测栈。
- 根捕获只冻结本地进程已物化、能够读取的数据；未知或未建模语义必须拒绝。后台搜索不得回读变化中的 live 状态。
- 读取队友可见状态不扩大 `RootActionPlayers`；不会部署队友动作，也不发送 CombatSolver 自定义网络包。
- MultiplayerOnly 牌保留真实牌堆占位和抽牌距离，当前不进入主动搜索/自动执行；Tag Team 未进入白名单。

## 执行动作

- Safe Execute 可提交本地 `PlayCard` 和 `UsePotion`。药水必须重新核对槽位、身份和目标，动作后继续核对预测/实机状态。
- 计划内 Choice / NestedChoices 使用原生计划驱动器；不能把 `CanDriveChoices=true` 理解为支持任意未知选牌语义。
- 后续回合已有明确计划的 turn-start Choice 可重放；这不开放新的多人 turn-setup 搜索或 `CanInterceptTurnSetup`。
- 执行长度取自当前回合的安全路线切片，不再使用历史“最多 6 张牌”作为当前上限。每个动作后仍须做归因和状态复核。
- Replay 语义、队友目标、未知目标、MultiplayerOnly 和无法证明后态的动作继续拒绝。
- Safe EndTurn 需要当前本地可操作回合、无待选牌、世界稳定且生命周期一致；后续回合必须取得新授权。
- 多人 Safe Auto 是 [`SolverController.Automation.cs`](../../src/Runtime/SolverController.Automation.cs) 中独立的受校验循环；单人 Full Auto、Fast/Instant、Showcase 与战前预测能力没有因此开放给多人。

## 续用与恢复

旧 request/generation/WorldVersion 的部署授权不能直接继承到新根。严格 continuation 不匹配后，可按已有合同在新根重放候选或使用 R1 恢复；必须重新证明完整状态、路线质量和当前动作合法性，失败则重新搜索。

部分敌人公开伤害、Targets RNG 与目标死亡情景已有定向恢复合同及实机证据。不能因此忽略所有 RNG、牌堆、Power 或目标变化，也不能把所有队友状态变化都描述成必须完整重搜。

## 仍有的验证缺口

- 显式采用路线后的连续 RNG 失效恢复、Headbutt / turn-start Choice、Vicious 估值等仍有 current-build Host/Client 待复验项。
- 本地药水执行、实验 teammate observation 时序、MultiplayerOnly / Tag Team 原生语义未完成全面真实多人验收。
- Carry Ranking R2 decisive flip 仍为可选补充证据，不能以无决定性差异的场景记为 PASS。
- 编译、合同、pinned replay、validator self-test 与真实 Host/Client 是不同证据层；本文件的能力核对不新增实机 PASS。

操作前使用 [RUNBOOK](RUNBOOK.md)，结果按 [当前交接](../CODEX_HANDOFF.md) 与 [测试矩阵](../TEST_MATRIX.md) 的证据边界解释。
