# CombatSolver 当前测试矩阵

> 只保留当前能力、验证入口和未验证边界。历史批次与旧数字从 Git history / 定向 evidence 查。

## 静态 / 构建

| 层 | 入口 | 证明范围 |
|---|---|---|
| Target | `tools/verify-target-version.ps1` | 0.107.1 / RitsuLib / compatibility symbol |
| Architecture | `tools/verify-refactor-boundaries.ps1` | 依赖和职责边界 |
| L1 contracts | `tools/run-contract-tests.ps1` | Search / Prediction / Multiplayer 纯合同 |
| Release | `dotnet build CombatSolver.csproj -c Release` | 当前本机真实依赖下编译 |

CI 只能证明其实际执行的层级；没有日志/steps 的 runner 失败不能记作源码 FAIL。

## 单人

- 主线：稳定基线。
- 0.107.1 后版本差异候选已完成主要卡牌审计。
- Axebot `AXEBOTS_NORMAL`：用户实机确认旧 search setup 崩溃已解决。
- 怪物 static-member guard：固定 DLL 元数据门禁。
- Monster target fanout：63 个可确定 move 已支持；Knowledge Demon 多人远端 Choice 继续 fail closed。

## Multiplayer

| 能力 | 状态 |
|---|---|
| MP-0 Core / lifecycle | PASS |
| MP-1 Advisor 受控 Smoke | PASS |
| MP-2A 单动作 Safe Execute | PASS |
| MP-2B 连续动作 + 远端干扰中止 | PASS |
| MP-2C bounded N-action | PASS |
| Reactive Carry / Safe EndTurn | PASS |
| Local cross-turn T1→T2→T3 | PASS |
| Safe Auto 连续 3 本地回合 | PASS |
| Carry Ranking R1 | PASS |
| Carry Ranking R2 decisive | UNVERIFIED |
| MultiplayerOnly runtime boundary | UNVERIFIED |
| Tag Team 原生双 Client 语义 | UNVERIFIED |
| TAG_TEAM Safe Execute whitelist | NOT ENABLED |
| Potion / Choice / Replay / teammate-target / Multiplayer Instant | FAIL-CLOSED |

真实 multiplayer PASS 必须来自 Host/Client journal + 对应 validator；合成 validator self-test 不能替代实机。

## 当前多人测试工具

- 旧 CombatSolver Console Fixture 已删除。
- test-only `TheBookOfAges / GM Console` 保留完整 UI / GameActions / 网络同步实现，不进入正式 CombatSolver DLL。
- 当前先验证“所有端同构建 + 最小状态修改”的同步稳定性，再用它构造 MultiplayerOnly 场景。

## 证据读取原则

只在核验具体结论时读取对应本地问题包、运行日志或 Git history。不要为了普通开发把历史 evidence、performance 或旧报告批量加载进上下文。
