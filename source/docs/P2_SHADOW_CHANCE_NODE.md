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

## P2B：当前回合 Chance Node

最终选择不再直接把 Joint Shadow 分支当作玩家可自由选择的结果。候选先按**当前本地回合动作序列**分组；对于每组，只读取当前回合第一个 Joint EndTurn 后的立即场景状态：

1. 用 `ScenarioFingerprint` 去掉后续搜索造成的同场景重复；
2. 用 `ScenarioProbabilityMass` 聚合当前回合结束后的概率分布；
3. 比较保守的胜利失败概率、全员存活概率和概率加权 P1 团队目标；
4. Top-K 未覆盖的概率质量不重新归一化成“必然落在已知好场景”，而按当前已观察到的最坏目标值补齐；未覆盖胜利/存活则按失败处理；
5. 选定当前回合动作组后，具体展示/continuation 代表优先使用该组中概率质量更大的 Shadow 场景；
6. 下一真实回合仍重新捕获状态和滚动重规划，因此 P2B 不构造无限多回合的 chance tree。

如果本次 Shadow 遇到 pending Choice 或动作深度上限，`ScenarioProbabilityTrusted=false`，整个概率聚合 fail closed，回退到 P1 排序。

实际部署权限没有变化：Shadow 仍只存在于 detached simulator，`RootActionPlayers` 仍只允许本地玩家。
