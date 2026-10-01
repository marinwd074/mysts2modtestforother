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

`U0U1PinnedHarness rng-restore` 对照旧 RNG 还原的计数器、状态、后续随机值及父状态隔离；完整请求对照复用 `request-hydration-benchmark-off`，高计数器压力夹具为 `multi-hit-high-counter`。不替代实机搜索或退出取证验证。

`request-hydration-benchmark-off --measure` 另核对 Snapshot 全调用与子阶段调用数、不重叠阶段上界、结果内部子阶段和 worker 汇总；固定输入包含 shared-audit / draw-repeat / multi-hit，三敌覆盖 DOP=1/4。诊断开关对照验证完整根/路线/最终状态/质量/工作一致，不作为新增默认门禁或提速证明。

`U0U1PinnedHarness root-history` 核对根历史预聚合与原扫描、预测事件叠加、未知玩家/非玩家回退、Fork 隔离及新根更新。完整请求对照沿用 `request-hydration-benchmark-off`；`history-repeat` 人工记录 2000 次抽牌但保持棋盘不变，只作长历史压力输入。JSON 含实际加载 DLL SHA256；请求计时不包含根捕获，不替代真实两玩家验证，不新增默认门禁。

`tools/StrategicKeywordChecks/run.py` 比较 134932 个完整上下文，含 Corruption 已确定技能消耗时的按需读取及小刀固有关键词；`--native` 生成完整关键词集合 oracle 并显式编译进既有 harness，比较 462 个上下文/抽牌时机，覆盖 Hex 全局 Hook、移除/可写卡牌、NoDraw 顺序、Fork 隔离及 DOP=4。oracle 共享标量公式，不替代旧算法标量合同。同一入口的 `strategic-energy` 另核对 180 次原生费用、792 个上下文、48 个并发分支及查询前后 Power/history 指纹、真实 Owner 变化隔离，覆盖免费/消耗 Power、局部费用、X/负费用和牌堆移动；原生牌堆临时挂载仅限无头串行 oracle。原生完整请求使用人工 `strategic-repeat`（4 Power + 6 张抽牌堆卡牌），覆盖 DOP=1/4；Benchmark 以完整动作 JSON 值核对 Choice/目标，结果记录 `fullRoute`。不替代实机 Hook/部署验证，不新增默认门禁。

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
