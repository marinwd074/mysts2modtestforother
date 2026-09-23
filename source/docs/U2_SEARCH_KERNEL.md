# U2 — 搜索共核与退化等价

状态：**COMPLETE（2026-09-23）**。

U2 的范围只验证“无真实多人新增语义时，多人本地跨回合搜索退化为单人搜索核心”。它不宣称真实多人战斗与单人战斗相同，也不替代 U1 的 Host/Client Safe Execute 实机 smoke。

## 目标

- SinglePlayerFullRoute 与 MultiplayerLocalCrossTurn 进入同一完整搜索核。
- 相同抽象根、相同合法动作集、相同目标、相同固定预算时，首动作与终局值一致。
- 固定 tie-break 后完整动作序列一致。
- 不为测试相等而删除真实多人语义。

## 已完成的结构收口

### 1. 搜索能力与执行权限拆开

SolverController.CaptureSearchPolicy 先解析 SearchRoutePolicy，再由完整搜索核决定搜索能力。

SinglePlayerFullRoute 与 MultiplayerLocalCrossTurn 现在共用：

- SolverSearchProfile / Beam / 节点预算；
- Novelty Portfolio；
- Growth budgets；
- relic targets / growth opportunities；
- 长期收益；
- 配置的本地药水搜索策略与候选。

CanUsePotionsAutomatically 不再决定搜索是否能考虑药水。它只保留为部署权限；多人 Safe Execute 自动药水仍未开放。

### 2. route mechanics 与团队目标拆开

SearchPolicySnapshot 新增 UseMultiplayerTeamObjective。

生产配置：

- 单人：false；
- 真实多人：true。

BeamRetentionPolicy 与 FinalPlanOrdering 读取显式 objective mode，不再把 MultiplayerLocalCrossTurn 本身当成团队目标的同义词。因此 U2 可以在同一个单人根上只切 route policy，并保持完全相同的单人目标。

### 3. 历史多人 tie-break 只在真实多人根启用

新增 MultiplayerLocalCrossTurnContracts.HasActiveMultiplayerRouteSemantics(routePolicy, playerCount)。

以下行为不再仅因为 route policy 名为 MultiplayerLocalCrossTurn 就启用，而要求实际 playerCount > 1：

- incomplete route 的 Anger copy 延后排序；
- current-turn playable route tie-break；
- 无可信共享 RNG 世界线时的未来 Shuffle 边界。

这使“1 个 captured player + MultiplayerLocalCrossTurn”真正退化回共享搜索核心，同时真实多人仍保留原有边界。

### 4. 真实多人语义继续保留

以下差异没有为了 U2 测试被删除：

- MultiplayerOnly 卡保留在真实牌堆状态，但不作为多人本地搜索动作；
- RootActionPlayers 仍只有本地玩家；
- 有真实 captured teammates 时，EndTurn 可进入 Joint/Shadow teammate forecast；
- 团队战损 / lethal tempo / team-safety / P3 scenario rerank；
- multiplayer continuation / WorldVersion / remote fingerprint；
- shared Shuffle RNG 的可信世界线边界；
- Safe Execute 的本地所有权、目标、native attribution 与 post-action revalidation；
- 多人自动药水仍关闭。

RootCapturedPlayers.Count <= 1 时 Joint forecast 回落到普通共享 EndTurn 模拟。

## U2 验证设施

### 轻量合同

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\test-u2-search-kernel.ps1
pwsh -NoLogo -NoProfile -File .\source\tools\run-contract-tests.ps1
~~~

2026-09-23 的兼容工作流结果：

- multiplayer local-cross-turn contract：25 PASS；
- U2SearchKernelChecks：PASS；
- 总合同：31 PASS / 0 FAIL / 0 SKIP；
- static-consistency：PASS。

验证 commit：f44831350d7c0f2e2c283aa6d1ddf7bea7ccd6ce。

### pinned 0.107.1 同根 differential

权威 differential 使用独立的 source/tools/U2DegenerateHarness，而不是依赖 src/Testing 的旧 OfflineSearchHarness。

它：

1. 用 pinned 0.107.1 游戏 DLL 建一个真实单人 combat root；
2. 从同一 live combat 连续捕获等价根；
3. 固定 DOP=1、Beam、MaxExpandedNodes、时间上限与 objective；
4. 关闭 Novelty/Beam-width portfolio，避免额外组合策略干扰；
5. 只切换 SinglePlayerFullRoute / MultiplayerLocalCrossTurn；
6. 同时比较首动作、完整动作序列、终局值、score 与搜索工作量。

入口：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\test-u2-degenerate-equivalence.ps1
~~~

pinned workflow 会先以 TreatWarningsAsErrors=true 编译 CombatSolver Release 与 U2DegenerateHarness，再实际执行 differential。

## 2026-09-23 实际 differential 证据

Pinned workflow：

- commit：583cc63578032649c946d43f0f8a7be130ce9a9a
- run：35840289311
- CombatSolver Release：0 warnings / 0 errors
- U2DegenerateHarness Release：0 warnings / 0 errors
- U2DegenerateEquivalence：PASS

固定样例的两个 route policy 得到完全相同结果：

| 字段 | SinglePlayerFullRoute | MultiplayerLocalCrossTurn |
|---|---:|---:|
| FirstAction | BASH | BASH |
| Boundary | None | None |
| ProjectedBattleHpLost | 2 | 2 |
| FinalHp | 78 | 78 |
| FinalEnemyHp | 0 | 0 |
| CombatEndedTurn | 6 | 6 |
| PotionCount | 0 | 0 |
| OnlyDeathRoutes | false | false |
| Score | 10002279982 | 10002279982 |
| ExpandedNodes | 496 | 496 |
| TransitionCount | 1368 | 1368 |
| ChoiceBranchesEvaluated | 0 | 0 |

固定 tie-break 后的完整 18-action 序列逐项一致。首动作语义为 BASH，随后整条路线直到第 6 回合击杀均一致。

证据 JSON 由 CI 运行时写到 u2-degenerate-equivalence.json，并在 workflow 日志中完整输出。

## U2 验收

| 项目 | 状态 |
|---|---|
| 单/多人本地跨回合同一 Beam/预算入口 | PASS |
| Novelty/Growth/Relic/长期收益共核 | PASS |
| 本地药水规划不再被自动执行能力关闭 | PASS |
| route mechanics 与 team objective 解耦 | PASS |
| 历史多人 tie-break 仅在真实多人根启用 | PASS |
| MultiplayerOnly / teammate / network / RNG 真实边界保留 | PASS |
| 静态 U2 contract | PASS |
| 同根固定预算首动作一致 | PASS |
| 同根终局值一致 | PASS |
| 固定 tie-break 完整序列一致 | PASS |
| 固定预算搜索工作量一致 | PASS |
| pinned Release 主项目编译 | PASS |
| U2 专用 harness 编译与实跑 | PASS |

## 不属于 U2 完成条件的未验证项

U1 的真实多人 runtime smoke 仍独立 UNVERIFIED：

- 重锤 + 真实 Choice/烙印 + 后续牌；
- 连续祭品抽牌后继续出牌；
- 队友在两张本地动作之间插入可读变化；
- cancellation / late callback 不重复提交。

这些影响 Safe Execute 实机稳定性，但不推翻 U2 已完成的搜索共核退化等价验收。
