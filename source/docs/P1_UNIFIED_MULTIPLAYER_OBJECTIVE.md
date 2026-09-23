# P1 统一多人目标

P1 只解决一个问题：**最终选中的路线、Beam 中途保留的路线、同状态代表路线必须优化同一个团队目标。** 不扩大 Beam、不增加时间预算、不修改 Shadow 行为先验。

P0 本机验证在 2026-09-23 被明确跳过，因此 P1 的静态/合同 PASS 不能反向宣称 P0 runtime 已通过。

## 统一 Rank

多人 `MultiplayerLocalCrossTurn` 使用 `MultiplayerCombatObjectiveRank`：

1. 完整胜利优先；
2. 全员存活优先；
3. `LossEquivalent` 越低越好；
4. 最差队员累计战损比越低越好；
5. 团队累计战损比越低越好；
6. 完整胜利比较结束回合；未结束路线比较敌方剩余有效耐久；
7. 完全相同后才回到原有 Score、资源、药水和确定性 tie-break。

单人排序不经过这个 Rank，保持原逻辑。

### 完整胜利

`AdaptiveLethalTempo` 继续使用连续的：

`teamLossRatio + turnsToEnd × LossRatioPerTurn(rootEnemyDurability, worstPlayerLossRatio)`

因此没有旧 35% 阈值跳变。

### 未结束路线

Beam 不能直接使用“结束回合”项，因此使用同一损失尺度的 bounded progress credit：

`teamLossRatio - progress × LossRatioPerTurn(currentEnemyDurability, worstPlayerLossRatio)`

高耐久时 urgency 很低，额外战损几乎不能靠进度抵消；接近斩杀时允许少量额外战损换取明显敌方耐久下降。整个 progress credit 上限仍受 5% 常数与 team-risk factor 约束。

## 接线范围

P1 Rank 已接入：

- `FinalPlanOrdering`：正式最终路线；
- `BeamRetentionPolicy.CompareFinalCandidates`：最终候选预筛选，避免团队更优路线在正式 FinalPlanOrdering 前被删；
- `BeamRetentionPolicy.SortByBeamRank`：普通 Beam；
- `BeamRetentionPolicy.IsBetterSearchNode`：同一 `StateKey` 代表选择；
- Transposition Pareto：保持原保守条件。其 `StateKey` 已固定 turn/enemy durability，label 已显式保存 `AllPlayersAlive / TeamLossRatio / WorstPlayerLossRatio`，因此不需要新增一个可产生错误 dominance 的近似 scalar。

Shadow 自己的 exact fingerprint / heuristic overflow pruning 不在 P1 修改范围；它仍负责生成候选世界线，主搜索负责用统一 Rank 评价进入主路线的结果。

## 验收

`MultiplayerLocalCrossTurnChecks` 新增纯合同：

- 早期高耐久时，小幅进度不能抵消明显额外战损；
- 近斩杀时，明显进度可以抵消有限的额外战损；
- 完整胜利 Rank 与既有连续 tempo 结果一致；
- 全员存活是硬边界；
- `MinimizeTeamLoss` 不把进度混进 loss-equivalent，只在战损相同后用耐久 tie-break。

P1 不要求当前执行本机 smoke；本机验证继续按 P0/P1 后续统一验证集补做。
