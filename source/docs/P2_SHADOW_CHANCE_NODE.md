# P3 概率模型实验（当前禁用）

当前正式 P3 使用 **Aggressive / Defensive / Conserve / NoAction 非概率压力情景**，不使用行为 prior 对推荐做概率加权。正式设计见 `P3_MULTIPLAYER_JOINT_DECISION.md`。

本文件只保留已经实现的概率基础设施，供后续有真实队友行为日志后继续实验。

## 已有概率质量基础

`BehaviorLogProbability` 表示一条代表动作历史的 log probability；`BehaviorLogMass` 表示 exact-equivalent future state 聚合后的总概率质量。不同动作历史只有在完整 `ShadowFutureStateFingerprint` 相同时才合并，概率质量用 log-sum-exp 相加。

保留场景还记录：

- `ScenarioProbabilityMass`；
- `ScenarioConditionalProbability`；
- `RetainedScenarioProbabilityMass`；
- `ScenarioFingerprint`；
- `ScenarioProbabilityTrusted`。

这些值目前来自通用、未经玩家历史校准的弱 prior，因此不能解释为真实队友选择概率。

`ScenarioSetComplete` 与上述概率字段分离：它只表示 Shadow 情景搜索没有因为 unsupported/pending Choice 或动作深度上限被截断。当前正式 P3 robust rerank 使用的是这个 completeness bit，而不是概率可信度。

## 当前关闭的代码

概率加权选择继续硬关闭：

`EnableMultiplayerChanceAggregation = false`

概率型 final coverage 继续为：

`FinalChanceCoverageLimit = 0`

正式 P3 使用独立的 scenario coverage：最多 4 个当前动作组 × 4 个压力情景，只补已经生成的 candidate，不新增 simulator expansion。

## 将来何时启用

只有取得足够的“预测队友行为 vs 实际队友行为”日志后，才重新评估：

1. prior 是否需要按玩家、角色、牌组或局面校准；
2. retained probability mass 是否达到可接受覆盖率；
3. probability-weighted 结果是否优于当前 robust stress-scenario 排序；
4. 所有比较是否把队友预测开销计入相同总预算。

在此之前，概率值只用于诊断和未来校准，不参与推荐。
