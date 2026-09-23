# P2 Shadow 概率与 Chance Node

P2 的目标不是给 Shadow 路线再加一个启发式分数，而是阻止主搜索把“低概率但很有利的队友世界线”当成可自由选择的动作结果。

## P2A：概率质量

`BehaviorLogProbability` 继续表示一条代表动作历史本身的 log probability。新增 `BehaviorLogMass` 表示某个**精确未来状态**承载的总概率质量。

当两条不同动作历史得到相同 `ShadowFutureStateFingerprint` 时：

- 仍只保留一条代表动作历史用于回放；
- 概率质量用 log-sum-exp 相加；
- 不再因为 exact dedup 丢掉另一条历史的概率。

最终 Team Top-K 为每个保留场景冻结：

- `ScenarioProbabilityMass`：该场景在当前 Shadow 行为树中的原始概率质量；
- `ScenarioConditionalProbability`：只在保留场景集合内归一化后的权重；
- `RetainedScenarioProbabilityMass`：Top-K 总共覆盖的原始行为概率质量；
- `ScenarioFingerprint`：精确未来状态身份；
- `ScenarioProbabilityTrusted`：只有未遇到 unsupported/pending Choice 且未撞动作深度上限时才为 true。

因此“Top-K 条数”与“Top-K 覆盖了多少行为概率”不再混为一谈。

## Fail closed

如果存在 pending Choice 或动作深度截断，概率仍可用于诊断，但下一层期望值决策不得把它当作完整概率分布。主搜索仍可使用 P1 的团队目标与保守路线搜索。

## 下一步 P2B

在最终当前回合决策层，把同一组本地当前回合动作下的 Shadow 场景视为 chance node：

1. 相同场景历史只保留该场景下最优的未来本地策略；
2. 概率按 ScenarioProbabilityMass 聚合；
3. 未覆盖概率质量按保守结果处理；
4. 对当前回合动作比较概率加权的团队目标，而不是从 K 条 Shadow 路线中挑最幸运的一条；
5. 实际部署权限不变，仍只部署本地动作。
