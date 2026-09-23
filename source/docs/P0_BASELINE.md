# P0 基线

P0 的目标不是提高搜索强度，而是冻结输入、验证 continuation 行为，并让失败能够被一致归类。P0 期间不得改目标函数、Beam 宽度、Shadow 行为先验或搜索预算来掩盖问题。

## 冻结点

- 仓库：`marinwd074/mysts2modtestforother`
- P0 前源码基线：`380b0801c5882c502d099d704a4bd5d55082e15a`
- STS2：`0.107.1`
- 编译目标：`STS2_01071`
- .NET：9
- Godot：4.5.1
- 机器清单：[`baseline/p0-baseline.json`](baseline/p0-baseline.json)

P0 后续提交与这个源码基线比较。基线 commit 不是“最终正确版本”，只是防止后续比较时输入继续漂移。

## 当前状态

**2026-09-23：pinned 0.107.1 runtime 已补做。** Release Build、P0 contracts 与 Joint continuation gate 已通过；Joint exact reuse 与 remote fingerprint mismatch 均由生产 continuation 代码验证。SP-REGRESSION 的原始 Medium / Smart / fixed 5000 ms / DOP1 门仍不能计 PASS：历史 P0 基线 `380b0801` 与当前版本在同一 pinned runner 上都命中 `TimeLimit`，因此正式分类保持 `INCONCLUSIVE_TIME_BOUNDARY`。

为区分 wall-clock 波动与搜索语义回归，增加了**补充诊断**，但不替代原 5000 ms 性能门：同一 root、Beam 60、DOP1、单成员、固定 1200 expanded nodes、关闭 wall-clock 截断时，历史与当前结果完全一致——首动作 `PROWESS`、累计战损 0、final HP 66、enemy HP 4、1200 expanded，分类 `OBSERVED_EQUIVALENT`。因此目前没有 P0 核心搜索质量回归证据；未封口项只剩原 5000 ms SP 性能/完成性门。

## 固定 workload

### SP-REGRESSION

使用：

`source/tools/GeneratedCombatScenarios/regression-necrobinder-elite.json`

这是固定的 NECROBINDER / A10 / PHROG_PARASITE_ELITE 生成场景。原 fixture 自带 `Deploy`，P0 **必须覆盖为 `Search`**，避免部署逻辑混进算法基线。固定搜索口径为 Medium / Smart / fixed budget 5000 ms / DOP 1。

Windows 本地执行：

~~~powershell
python .\source\tools\GeneratedCombatScenarios\run.py --config .\source\tools\GeneratedCombatScenarios\regression-necrobinder-elite.json --count 1 --mode Search --output .\.local\p0-sp-regression -- -PerformancePresetForTest Medium -FixedSearchBudget -SearchBudgetOverrideMilliseconds 5000 -SearchMaxDegreeOfParallelismForTest 1 -PotionPolicyForTest Smart
~~~

只接受 `.local\p0-sp-regression\0000\result.json` 中 `status=Passed` 且没有时间边界的样本。P0 首次有效运行记录实际 root fingerprint、路线、搜索预算和结果；后续 A/B 必须复用该次生成的 resolved scenario、相同输入和相同总预算。

时间边界命中则该根无效，不能把“更快超时”当改进。

### MP-JOINT-REUSE

使用 `docs/multiplayer/NEXT_LOCAL_JOINT_CONTINUATION_SMOKE.md` 的 Reuse 流程。

通过条件：

- 观察到 `MP_LOCAL_XTURN_CONTINUATION_VALIDATE`；
- 观察到 `SEARCH_REUSED`；
- `MP_LOCAL_XTURN_CONTINUATION_REUSED ... local_state_exact=true reason=exact`；
- 同 route/turn 不出现 reject。

### MP-JOINT-MISMATCH

与 Reuse 使用相同多人配置和相同搜索总预算；只增加“队友在 Safe EndTurn 后、下一本地决策前做一个可读状态变化”的实验变量。

通过条件：

- reject 原因为 `remote_public_mismatch`；
- `local_state_exact=true`；
- 记录 `SEARCH_REUSE_MISS`；
- 随后 Fresh Search；
- 同 route/turn 不得 continuation reuse；
- P0 分类器识别为 `teammate_prediction_deviation`，而不是 `simulation_error`。

## 故障分类

运行：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\classify-p0-baseline-result.ps1 -LogPath '<log>'
~~~

分类口径：

| 分类 | 直接证据 | 含义 |
| --- | --- | --- |
| `simulation_error` | 选中路线 replay 与搜索快照出现标量差异 | 模拟/回放语义先查，不归咎于 Beam |
| `search_miss_evidence` | 同类日志出现更好的已观察路线 | 只是漏解证据；必须同 root、同总预算复现后才能称“已证明搜索漏解” |
| `teammate_prediction_deviation` | local exact + `remote_public_mismatch` | 队友真实行为偏离选中的 Shadow/Joint 世界线，不等同于模拟错误 |
| `runtime_state_mismatch` | continuation 因 combat/local/scaling/card 等边界拒绝，或本地状态不 exact | Runtime/状态生命周期问题 |
| `unclassified` | 没有上述直接证据 | 保留原日志，不猜原因 |

分类可以同时出现多个标签；`primaryClassification` 只用于快速分流。

## 公平比较

验证集比较以下指标：

- 生存 / 胜利；
- 团队累计战损；
- 结束回合；
- 药水和稀缺资源消耗；
- 首次可用结果时间；
- 总耗时；
- expanded / transitions / Fork 等工作量；
- 队友情景评估自身的节点与时间开销；
- 实际状态与预测状态的偏差。

新旧算法必须使用相同输入和相同**总预算**。队友预测开销属于总预算，不能额外赠送。P0 不以扩大 Beam、延长时间或增加内存换取 PASS。

## 停止规则

某个固定 workload 达到 PASS 后，不再重复人工 smoke。只有后续修改触及该 workload 覆盖的模块时才重跑：

- SP：模拟语义、终局排序、搜索保留/去重；
- Reuse：continuation stamp、root capture、Shadow replay、world version；
- Mismatch：队友 fingerprint、continuation admission、fresh-search 调度；
- Deployment：Safe Execute / EndTurn 授权与 lifecycle。

P0 封口条件：

1. Release build 0 error；
2. contract tests 包含 P0 classifier 并通过；
3. SP-REGRESSION 已捕获有效基线；
4. Joint Reuse PASS；
5. Joint Mismatch PASS；
6. 两个多人 smoke 能被分类器正确区分；
7. 当前多人文档不再把 Reactive Carry/current-turn-only 描述为目标架构。

完成后进入 P1：统一目标。
