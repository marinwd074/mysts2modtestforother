# P2 候选保留

P2 的目标是：**在相同搜索预算下，减少低战损、快斩杀、团队安全和成长路线被 Beam 过早丢弃。** 不增加 BeamWidth、时间预算或内存预算。

## 四个固定预算保护槽

多人 `MultiplayerLocalCrossTurn` 的 `RankBest` 在普通 Beam 排序之后、其他组合 portfolio 之前，最多保护四类互不重复的代表：

1. `LowTeamLoss`：团队累计战损最低；
2. `TeamSafety`：最脆弱队员累计战损最低；
3. `FastLethal`：有完整胜利时优先最早结束；没有完整胜利时保留敌方有效耐久最低的进攻路线；
4. `Growth`：按长期资源、持续成长、setup、未来资源、攻击成长和 replay 潜力保留一条成长路线。

如果存在全员存活候选，四个槽都只从全员存活候选中选。相同路线命中多个槽时只占一个位置。

这些是**保留槽**，不是额外 Beam：`AddRequired(..., limit)` 仍受原有 limit 约束。极小 Beam 下按 LowTeamLoss → TeamSafety → FastLethal → Growth 顺序使用现有槽位。

## 三种不同概念

代码和诊断必须区分：

- **exact future-state merge**：只有像 `ShadowFutureStateFingerprint` 这种覆盖完整 modeled future state 的键才能这样命名；
- **StateKey representative compression / transposition dominance**：主搜索同状态仍可能带不同累计战损或 policy history，因此保留 label/目标比较，不宣称“严格等价”；
- **heuristic retention pruning**：`HeuristicQualityDominates`、Beam 截断和 portfolio 采样只是预算内启发式，不能用于“证明最优”；函数名本身明确标出 `Heuristic`，避免后续精确搜索误用。

因此后续 P4 的“精确斩杀”不得复用 P2 的 heuristic dominance 作为安全剪枝条件。

## 验收合同

`MultiplayerLocalCrossTurnChecks` 固定验证：

- 四种代表可在 beam=4 时同时保留；
- 存在存活路线时，死亡路线不能靠更快斩杀或更高成长抢占保护槽；
- beam=2 时仍只用两个原预算槽，不扩大预算；
- 单人路径不调用该多人专用 portfolio。

本机搜索效果对比仍按 P0 记录暂缓；因此当前只能声明结构/合同完成，不能声称实战漏解率已经下降。

## P2 阶段隔离

P3 的 Shadow chance-node / final chance coverage 代码已经有预备实现，但在 P2 验证期间显式关闭：

- `EnableMultiplayerChanceAggregation = false`;
- `FinalChanceCoverageLimit = 0`.

因此 P2 的行为变化只来自候选保留与剪枝语义收口，不会把多情景复评混入同预算比较。进入 P3 时再单独启用并验证。
