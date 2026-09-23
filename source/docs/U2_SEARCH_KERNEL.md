# U2 — 搜索共核与退化等价

状态：**代码/合同接线完成；真实离线退化等价运行仍 UNVERIFIED。**

## 目标

U2 不把多人搜索改成另一套 Beam，也不为了“测试相等”删除真实多人语义。目标是：

- SinglePlayerFullRoute 与 MultiplayerLocalCrossTurn 进入同一完整搜索核；
- 相同抽象根、相同合法动作集、相同目标、相同固定预算时，首动作、终局值一致；
- 固定 tie-break 后完整动作序列也应一致；
- 多人真正新增的语义仍显式保留。

## 本轮已收口

### 1. 搜索能力与执行权限拆开

SolverController.CaptureSearchPolicy 现在先解析 SearchRoutePolicy，再由完整搜索核决定搜索候选。

完整搜索核：

- SinglePlayerFullRoute
- MultiplayerLocalCrossTurn

两者共用 SolverSearchProfile / Beam 与节点预算、Novelty Portfolio、Growth budgets、relic targets、growth opportunities、长期收益，以及本地药水搜索策略与候选。

CanUsePotionsAutomatically 不再参与搜索策略构造。它仍只表示多人 Safe Execute 是否有权自动提交本地药水动作；本轮没有放宽该执行边界。

### 2. 路线语义与目标函数拆开

新增 SearchPolicySnapshot.UseMultiplayerTeamObjective。

生产配置中单人为 false，真实多人为 true。因此真实多人仍使用团队战损 / lethal-tempo / P2/P3 团队保路与复评；但 U2 differential 可以在同一单人根上只切换 route policy，而保持同一单人目标，避免把“搜索核差异”和“目标函数差异”混在一个测试里。

Beam retention 与 FinalPlanOrdering 已改为读取显式 objective mode，而不是把 MultiplayerLocalCrossTurn 本身当成团队目标的同义词。

### 3. 离线退化等价 A/B 入口

OfflineSearchHarness 新增参数：

~~~text
--route-policy SinglePlayerFullRoute
--route-policy MultiplayerLocalCrossTurn
~~~

在单人根上使用该 override 时，UseMultiplayerTeamObjective 保持为根捕获值 false，只改变路线语义。

新增 source/tools/test-u2-degenerate-equivalence.ps1。脚本用同一 character / encounter / seed / Beam / node budget / DOP=1 分别运行两种 route policy，并比较：

1. 首动作；
2. boundary；
3. projectedBattleHpLost；
4. finalHp；
5. finalEnemyHp；
6. combatEndedTurn；
7. potionCount；
8. onlyDeathRoutes；
9. 固定 tie-break 后的完整 planActions 序列。

通过时写入 .local/u2-degenerate-equivalence/u2-degenerate-equivalence.json。

## 必须保留的多人差异

以下不是 U2 要删除的“算法分叉”：

- MultiplayerOnly 卡保留在真实牌堆状态，但不作为多人本地搜索动作；
- RootActionPlayers 仍只有本地玩家；
- 有真实 captured teammates 时，EndTurn 可进入 Joint/Shadow teammate forecast；
- 团队目标、团队安全保路、P3 teammate stress-scenario rerank；
- multiplayer continuation / WorldVersion / remote fingerprint；
- shared Shuffle RNG 的可信世界线边界；
- Safe Execute 的本地所有权、目标、native attribution 和 post-action revalidation；
- 多人自动药水仍关闭。

RootCapturedPlayers.Count <= 1 时 Joint forecast 会回落到普通共享 EndTurn 路径，这是退化等价测试的关键前提之一。

## 合同入口

轻量 CI：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\test-u2-search-kernel.ps1
pwsh -NoLogo -NoProfile -File .\source\tools\run-contract-tests.ps1
~~~

完整本机验证需要已构建的 OfflineSearchHarness 与 pinned 0.107.1 运行环境：

~~~powershell
dotnet build .\source\CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build .\source\tools\OfflineSearchHarness\OfflineSearchHarness.csproj -c Release
pwsh -NoLogo -NoProfile -File .\source\tools\test-u2-degenerate-equivalence.ps1
~~~

## U2 验收状态

| 项目 | 状态 |
|---|---|
| 单/多人本地跨回合同一 Beam/预算入口 | 已接线 |
| Novelty/Growth/Relic/长期收益共核 | 已接线 |
| 本地药水规划不再被自动执行能力关闭 | 已接线 |
| route mechanics 与 team objective 解耦 | 已接线 |
| MultiplayerOnly / teammate / network / RNG 真实边界保留 | 已接线 |
| 静态 U2 contract | 已接线 |
| 同根固定预算首动作一致 | UNVERIFIED |
| 同根终局值一致 | UNVERIFIED |
| 固定 tie-break 完整序列一致 | UNVERIFIED |
| pinned Release 主项目编译 | 待本轮验证 |
| U1 三类真实多人 smoke | 仍 UNVERIFIED |

只有 differential 实际运行 PASS 后，才能把 U2 标成完成。
