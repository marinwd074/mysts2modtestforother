# Multiplayer architecture

本目录记录 CombatSolver 当前多人生产边界。历史 MP-0/MP-1/MP-2、U0–U6 过程报告和 dated smoke 证据已从当前工作树移除；需要时从 Git history 或本地问题包恢复。

## 目标

多人模式的目标不是控制队友，而是让**本地玩家**获得接近单人求解质量的路线，同时把队友行为、共享战斗状态和多人时序作为重要环境因子。

核心约束：

- 搜索/模拟尽量与单人共享，不维护第二套低质量算法。
- `RootActionPlayers` 只允许本地玩家。
- 队友动作只作为 forecast / observation；不会被部署。
- MultiplayerOnly 牌保留真实牌堆状态与抽牌距离，但当前不进入主动推荐/自动执行。
- 任何未知私有语义、强状态偏移或无法证明的执行后态都不沿用旧部署授权。

## 当前层次

### Shared search core

单人和多人共用主要 CombatBeam/Search/Novelty/Growth/Relic/Potion 逻辑。多人不应因为“安全”而默认退化成只看当前回合的简化算法。

### Team objective and scenario reevaluation

多人候选额外评估团队战损、终局速度和队友情景。生产默认仍使用 Robust 解释，但当前 Quality-first 工作要求用真实坏路线证据验证它是否真的改善选择；没有证据不调整权重。

### Teammate interleaving

支持本地动作与 forecast-only teammate observation 交错模拟。队友节点属于预测环境，不获得 authority。共享 RNG、易伤/攻击顺序、抽牌/资源等顺序敏感状态必须进入 fingerprint/equivalence 判断。

### Bounded route refresh

队友造成轻量 HP/Block drift 时，可在新根对少量已保留候选做有界重放和小搜索，输出 Continue / Reselect / FullRestart。目标死亡、资源、Power、牌堆、RNG 或无法重新物化的变化直接 FullRestart。

### Safe Execute

本地动作逐个执行：

1. live structural/resolved admission；
2. 通过原生游戏入口提交；
3. 等待 Choice/动作队列和世界稳定；
4. 对比 predicted/live semantic post-state；
5. 只有 continuation 仍成立才继续使用当前 session。

旧 request、旧 generation 或 forecast observation 之前的授权不能在新世界状态中复活。

### Potions

多人搜索使用与单人相同的 PotionPolicy/PotionStrategy。Safe Execute 已支持本地 `UsePotion`，提交前重新核对槽位、PotionId 与 live target；动作后继续走统一 post-state/continuation 校验。不会控制队友药水。

## 当前验证状态

- U0–U6：实现、合同和 pinned 0.107.1 阶段完成。
- bounded refresh：实现/pinned 通过；真实多人样本仍可补证。
- local potion Safe Execute：实现/pinned 通过；真实多人自动喝药仍需实机问题包验证。
- teammate forecast observation → fresh replan 的部分真实 Host/Client 时序仍标记为未验证。
- 当前优先级已从继续堆安全层转为**实际路线质量**。

当前状态与下一任务以 [../CODEX_HANDOFF.md](../CODEX_HANDOFF.md) 为准，质量计划见 [../CombatSolver_Quality_First_Next.md](../CombatSolver_Quality_First_Next.md)。

## 运行与测试

- Host/Client 手动操作、隔离实例与日志采集：见 [RUNBOOK.md](RUNBOOK.md)。
- 当前已知能力边界：见 [LIMITATIONS.md](LIMITATIONS.md)。
- 固定版本兼容审计：见 [../compat/0.107.1/README.md](../compat/0.107.1/README.md)。
- 当前门禁：见 [../TEST_MATRIX.md](../TEST_MATRIX.md)。

运行日志、Probe、问题包和一次性 JSON 不提交到本目录。它们保存在 `.local/`、外部问题包或 Git history；可重复的结论应转成测试/fixture。


## 生产路线质量模式

当前多人生产搜索采用 **local-single-core**：

- 战斗根仍是真实多人状态，包括多人实际敌方血量、怪物动作与目标语义。
- 路线评分、Beam 保留和终局排序以单人核心为准，不使用 Team Objective 改写。
- 队友 Shadow forecast 与 Scenario Robust 不参与生产路线选择；它们保留为离线 A/B 与研究模块。
- 多人专用 Runtime 继续负责网络状态、continuation、目标合法性和 Safe Execute，不把这些执行语义变成另一套出牌目标。

这样多人模式的目标是“在真实多人战斗里求本地玩家自身的高质量牌序”，而不是预测或替队友做整队决策。


## 多人预测算法开关

“设置 → 常规 → 多人模式”提供 **启用多人预测算法（实验）** 总开关。

- 默认关闭：使用真实多人战斗状态，但路线质量由单人核心决定；Team Objective、Shadow teammate forecast、Scenario/Robust 复评和 Carry 排序均不参与生产选择。
- 开启：恢复完整多人预测栈，主要用于实战 A/B 对照；搜索成本更高，且此前样本中路线质量曾低于 local-single-core。
- “多人路线目标”只在总开关开启时可修改并生效。
- 此开关不改变多人怪物实际血量、多人目标语义、continuation、多人牌边界或 Safe Execute。
