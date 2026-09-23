# P3 多人联合决策

## P3A 已实现：最终 Chance Coverage

目标：保证 P2 chance-node 在最终选择前不会因为普通 final-quality trimming 丢掉主要概率场景。

实现位置：`BeamRetentionPolicy.RankFinal`。

约束：

- 主 Beam 宽度不变；
- 搜索时间预算不变；
- 不新增模拟扩展；
- 只补已经有普通最终候选代表的本地当前回合决策；
- 按精确 `ScenarioFingerprint` 保留场景代表；
- 优先高 `ScenarioProbabilityMass`，跨决策 round-robin；
- 最多额外 16 条最终候选；
- 不可信概率场景不进入 coverage portfolio。

## 后续 P3B

下一步再处理“本地玩家与远端玩家在同一 player side 内真正交错动作”的 scheduler。P3B 必须继续满足：

- 远端动作始终 prediction-only；
- 部署只允许本地玩家动作；
- 真实队友任何偏离都由 revalidation / fresh search 接管；
- 不通过增加 Beam/时间预算掩盖调度状态空间增长。
