# U0 当前差异图与最小质量基线

> U0 只建立可重复的诊断基线，不修改搜索预算、Beam、目标函数或 Safe Execute 判定。
> 审计起点：`ca74bb6f5d091b50c7c72134c228b9a300946530`。
> U0 代码基线：`4b05a6a0fe074f317ed01edb88117ba05d9dfb9d`；后续文档收口提交不改变本页所述代码语义。

## 1. U0 验收边界

U0 要能把一次“推荐不对/执行停了/又重搜”的现象拆成四层独立证据，而不是只看最终路线或合同测试：

| 层 | 当前记录入口 | 能回答的问题 |
| --- | --- | --- |
| 模型转移 | `SearchPathObserver` / `SearchPathObservationStage.Generated, Expanded, ActionAdmitted, ...`；已知路线夹具输出 `PATH_TRACE_EVENT` | 模拟器是否生成并推进了预期动作/状态；路线在哪个搜索阶段消失 |
| 候选 | `[CombatSolver/U0] FINAL_CANDIDATE` | 最终排序前后实际还有哪些可比路线、状态指纹、终局/边界和动作序列 |
| 最终选择 | `[CombatSolver/U0] FINAL_SELECTION` | 最终赢家是谁；P3 scenario rerank / chance rerank 是否参与 |
| 实际执行 | `DEPLOY_ACTION`、`NATIVE_ACTION_CAPTURED`、`DEPLOY_ACTION_COMPLETE`、`MP2B_ACTION_RECONCILED`、`MP2B_ACTION_STATE_DIFF` | 搜索选中的动作是否真的提交、完成；完成后的 live 状态为什么继续或中止 |

`source/tools/test-u0-baseline.ps1` 对上述四层入口和两类夹具做结构门禁。它只证明诊断入口存在，不冒充真实游戏运行证据。

## 2. 单人与多人当前差异

这里比较的是同一当前代码，不把真实多人规则差异误判为算法退化。

| 差异 | 调用位置 | 当前目的 | U0 判断 |
| --- | --- | --- | --- |
| 路线策略 | `MultiplayerLocalCrossTurnContracts.SearchRoutePolicy`；`CanUseFullSearchHeuristics` | 单人用 `SinglePlayerFullRoute`；多人联合预测用 `MultiplayerLocalCrossTurn`。两者已经共享 full-search heuristics | 保留。不是人为降级本身 |
| 持久路线缓存 | `MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache` | 当前仅单人允许 persistent route cache | 真实差异；后续若证明影响质量/响应再单独处理，不在 U0 放开 |
| 队友行为 | `ShadowTeammatePlanner.BuildTeamTopKRoutes`，由 Joint EndTurn 分支使用 | 队友动作只在 detached simulator 中预测；Team Top-K 按单动作交错推进，不生成可部署远端动作 | 多人真实额外状态转移 |
| 联合 EndTurn | `CombatBeamSolver.EndTurnExpansion.cs` | 本地 EndTurn 时绑定 `ShadowForecastPlan`，精确回放选中的队友世界线，再推进全队 End → Enemy → Start | 多人真实额外状态转移 |
| 目标函数 | `MultiplayerCombatObjectiveMath`、`CombatBeamSolver.FinalPlanOrdering` | 单人保持原 HP/资源排序；多人增加团队损失、最差队友、连续 lethal tempo | 真实产品目标差异；U0 不改 |
| P3 压力情景 | `MultiplayerScenarioReevaluationPolicy`、`FinalPlanOrdering` | 多人对完整 Aggressive / Defensive / Conserve / NoAction 情景做 final-only robust rerank | 多人真实不确定性层；概率 prior 仍不可信 |
| MultiplayerOnly 牌 | `MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard` | 多人牌仍保留在真实牌堆/顺序，但不作为本地或 Shadow 主动推荐动作 | 明确产品边界，不属于普通单人牌搜索能力 |
| Shuffle 精确性 | `MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle` | Joint/Shadow 拥有共享 Shuffle RNG 时可跨任意次数；只有无法证明共享 RNG 的 local-only 退化路径在第一次未来洗牌前停止 | 仍存在一个明确退化边界 |
| 执行权限 | `SolverSessionCapabilities`、`MultiplayerSafeLocalActionClassifier` | 单人可完整自动化；多人只部署本地动作，并逐动作 live gate + post-action revalidation | 必须保留的安全/所有权差异 |
| Choice 执行 | `SolverController.Deployment` / `NativeChoiceSession` | 本地 Choice/NestedChoices/TurnStartChoices 单人与多人 Safe Execute 共用原生驱动 | 已对齐，不应再作为多人固定边界 |
| 跨回合续用 | `MultiplayerContinuationRemoteFingerprint`、`MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch` | 多人要求远端可读语义与预测世界线一致；偏离强制 fresh search | 合理的多人一致性差异，但会增加重搜 |

## 3. 已失效兼容规则

以下规则不能再作为“当前多人为什么和单人不同”的理由：

- 固定 **6 / 32** 动作 ceiling 已移除；Safe Execute 的动作上限来自本次有限本回合安全路线。
- `choice_required` 已不再作为多人安全前缀的固定停止条件；本地 Choice 走与单人相同的 `NativeChoiceSession`。
- “第 N 次洗牌后停止”的人为阈值已移除；完整 Joint/Shadow 世界线由共享 Shuffle RNG 决定能否继续。
- “按 NetId 把某个队友整条路线跑完，再轮到下一名队友”的语义已移除；NetId 只用于确定性候选枚举，Team Top-K 是 action-interleaved。
- 旧的“敌方有效耐久 ≤35% 才切换斩杀目标 + 固定 5% 团队战损/回合”已移除；当前为连续 lethal tempo。
- Reactive Carry / current-turn-only / partial-route 补丁不再是目标架构；目标架构是完整联合战斗预测 + 滚动重规划。历史兼容代码存在不等于应继续扩展它。

## 4. “差”的五类分诊

U0 只按直接证据分类；没有证据时保持待验证。

| 类别 | 当前直接证据 | 当前状态 | 下一步证据 |
| --- | --- | --- | --- |
| 预测与真实不符 | Safe Execute 已能输出动作前后边界、`MP2B_ACTION_STATE_DIFF`、远端/敌人/资源/牌堆变化 | **可诊断，具体新复现待验证** | 对重锤+烙印、连续祭品等复现保存预期后态与稳定 live 后态 |
| 搜索没找到好路线 | 历史 Anger 问题曾证明候选遗漏/未完成路线排序问题并已修正；当前 U0 没有新的同 root 同预算漏解证明 | **当前无新直接证据** | 固定 root + 固定总预算，路径 observer 证明更好路线是否 Generated/Expanded/Pruned |
| 排序选错 | P1 已统一多人目标到 Beam/同 StateKey/Final；U0 现在能列 `FINAL_CANDIDATE` 与 `FINAL_SELECTION` | **可诊断，未证明当前仍错** | 同一候选集比较排序键；若好路线存在但排在后面，再归此类 |
| 执行误停/续打错误 | 用户实机曾观察到合法本地链中途停止/重算；当前有 native capture + post-action revalidation 细分字段 | **存在运行症状，根因待 U1** | 关联同一 request/action_index 的搜索选择、native 完成、state diff、abort/replan |
| 重搜导致响应慢 | 多人 remote mismatch / deployment drift 会触发 fresh search；当前请求工作量和运行日志可记录搜索次数 | **机制存在，性能根因未量化** | 区分必要重规划与错误失效，同时记录首次结果、总耗时、transitions/Fork 和重规划原因 |

一个现象可以同时触发多个标签。例如“搜索路线正确，但第一张牌后错误判定偏离并重搜”属于执行/重规划问题，不能因为最后推荐变差就直接归为搜索算法差。

## 5. 固定 U0 输入

### 5.1 无队友事件退化夹具

入口：`source/tools/OfflineSearchHarness/U0BaselineFixture.cs` 的 `ReplayNoTeammateEvents`。

- 队友事件脚本固定为空。
- 通过生产 `ShadowTeammatePlanner.ReplayForecastActions` 执行空脚本。
- 前后用 `ShadowFutureStateFingerprint` 比较完整 modeled state；任何变化直接失败。
- 这个夹具只定义“没有队友事件”本身。做单/多人退化等价比较时，还必须把敌方人数缩放、多人目标语义、团队目标函数等真实规则差异归一化；不能把不同难度的两个游戏状态拿来宣称不等价。

U2 才负责在相同抽象 root、动作集、目标和固定预算下断言首动作/终局值退化等价；U0 不提前改搜索器来让测试通过。

### 5.2 指定队友脚本固定输入

入口：同文件的 `ReplayFixedTeammateScript`。

固定脚本直接由不可变的 `ShadowTeammateActionCandidate` 序列组成，包含：

`PlayerNetId + HandIndex + CardId + UpgradeLevel + SemanticKey + EnergyCost + StarCost + TargetCombatId`

整个脚本一次性交给生产 `ReplayForecastActions`，因此：

- 不重新猜 Top-K；
- 不手写卡牌效果；
- 保留原本的动作顺序、共享 RNG 与 force-end 语义；
- 脚本在给定 detached root 上不可回放时直接失败，不静默替换成别的牌。

`DescribeFixedTeammateScript` 提供稳定的文本身份，便于把同一脚本记录到基线输出。

## 6. 最小质量基线

公平比较必须固定：

- 同一 resolved root / root fingerprint；
- 同一可用动作集合；
- 同一目标策略；
- 同一确定预算或节点/转移上限；
- 同一队友脚本/情景集合；
- Shadow、复评、Fork、指纹与 replay 全部计入总工作量。

最低记录集：

`root → model transitions → final candidates → final selection → actual native execution`

同时记录首次可用结果时间、总耗时、expanded/transitions/Fork、战损、队员死亡、结束轮数、错误停止次数、必要/错误重规划次数。重规划次数少本身不是质量目标。

## 7. 本轮验证状态

### 已完成的静态/结构证据

- 四层诊断入口已独立存在。
- 最终候选和最终选择不再只能从最终 `SolverResult` 反推。
- 空队友事件和固定队友动作序列已有可复用离线夹具。
- 固定脚本直接走生产 Shadow replay，不存在第二套卡牌效果实现。
- `source/tools/run-contract-tests.ps1` 已接入 `U0BaselineChecks`。
- 新增 `source/tools/U0U1PinnedHarness`，直接使用 pinned STS2 `0.107.1` DLL、生产 `CombatBeamSolver`、`SearchPathObserver` 与生产 Shadow replay。GitHub Actions run `35857680737` 在 commit `3ee2a7a54b875a45c145f6fe8a187d5b34fbaad6` 已 PASS：固定 IRONCLAD / FUZZY_WURM_CRAWLER_WEAK / U0U1PINNED1 root 上首动作 BASH，220 expanded / 579 transitions；观察到 Root / Generated / Expanded / ActionAdmitted，输出 3 条 FINAL_CANDIDATE + 1 条 FINAL_SELECTION；空队友事件生产 replay 前后 modeled state 指纹保持一致（`BC42282DEF196DB7:1D1E115B9588656D`）。
- 同一提交的 compatibility run `35857680764` SUCCESS；完整 pinned workflow 中 U2、P0 baseline、P0/P1 harness 也继续 SUCCESS。因此这次 U0 pinned 接线没有破坏既有回归链。
- **U0 real diagnostic correlation 已 PASS（2026-09-23）**：真实多人问题包 `CombatSolver-0.40.2-RUBY_RAIDERS_NORMAL-d91c89658d8f4620abb01e058b340fb1.zip` 中，Beam portfolio 最终选择 `selected_index=3`；该成员的 `FINAL_CANDIDATE rank=1` 与 `FINAL_SELECTION` 都是同一 9-action 路线：`OFFERING → BRAND[BLUDGEON] → BLUDGEON → STRIKE → STRIKE → OFFERING → OFFERING → BLUDGEON → STRIKE`。随后 `SEARCH_RESULT_ROUTE_CAPTURE generation=15` 固定 `route_identity=1a7fa2e287b648ea8188d8fbd8d76cb9`；Safe Execute `request_id=8` 以同一 route identity 启动，第一张 `OFFERING` 产生 `DEPLOY_ACTION` 与 `NATIVE_ACTION_CAPTURED action_index=0`，结算后 `U1_POST_STATE_COMPARE continuation_match=true remote_match=true`，并 `MP2B_ACTION_RECONCILED decision=SafeToContinue`。因此真实运行中已能从最终候选/最终选择关联到固定 route、native submit、live/predicted compare 与 reconcile。这里不要求 `PATH_TRACE_EVENT` 作为 PASS 前提；它仍用于更深的“路线在哪个搜索阶段丢失”诊断。

本轮没有把以下项目写成 PASS：

- 真实 Host/Client 的 U0 四层端到端关联（尤其实际 native execution 层）；
- 重锤+烙印合法 Choice 链；
- 连续祭品后抽出并继续使用后续牌；
- 队友插入动作后旧计划失效且不重复提交；
- U0 四层日志在同一真实问题包中的端到端关联。

Pinned DLL 下的 production Search / replay 已有运行证据；剩余项目依赖真实 native 提交、Choice/ActionQueue 与 Host/Client timing，不能由离线 pinned harness 冒充。

## 8. U0 结论与停止条件

U0 已完成：结构性诊断、pinned production Search/replay，以及真实多人 end-to-end diagnostic correlation 均已有 PASS 证据。

因此停止扩大 U0 测试。后续问题直接沿 `FINAL_CANDIDATE → FINAL_SELECTION → route_identity → DEPLOY_ACTION → NATIVE_ACTION_CAPTURED → U1_POST_STATE_COMPARE → RECONCILED/ABORT` 定位第一处断点；更深的搜索丢路问题再启用 `PATH_TRACE_EVENT`。
