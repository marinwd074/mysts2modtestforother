# U1 动作后态校验

> 目标：多人 Safe Execute 不再用“这张牌大概应该只改哪些字段”的经验规则决定是否继续，而是复用生产预测器，从当前 live 根精确重放本次本地动作，再拿稳定 live 后态逐字段核对。
>
> U0 基线：`373d07f8edc82f26e85a32f8585738b2b5e14ef7`
> U1 代码基线（文档提交前）：`2e403c46a22470fb62e1266fdb9ecb500816542a`

## 1. 已确认的旧问题

U0 时的 post-action gate 主要依赖：

- 出牌对象必须离开手牌；
- 能量/星星变化落在经验范围；
- 有目标牌只允许目标敌人的 token 改变；
- 队友可读状态必须完全不变；
- WorldVersion 必须推进且稳定。

这些条件适合作为诊断提示，不足以作为卡牌语义。

合法本地牌可以通过生产规则产生以下变化：

- 抽牌、生成牌、洗牌、弃牌/消耗牌；
- Choice / NestedChoice 驱动后的额外效果；
- AoE、溅射、死亡触发或 Power 链导致多个敌人变化；
- 本地效果触发的队友状态变化；
- 卡牌自身回手、复制、费用动态变化；
- RNG 消耗和预测相关模型状态变化。

因此“非目标敌人变化”或“远端可读状态变化”本身不能证明有队友插入；反过来，仅比较动作前后也无法识别“队友刚好在两次本地动作之间变化但尚未被 tracker 采样”。

## 2. U1 当前判定链

### 2.1 每张牌提交前先 fresh probe

`SolverController.Deployment` 在每次 `TryBeginAction` 前执行：

`MultiplayerClientProbe.ObserveActionBoundary(state, "safe_execute_pre_action")`

随后才读取 `MultiplayerSafeExecutionBoundary` 并用其 WorldVersion 请求本次授权。

效果：

- 如果队友/敌人/本地私有状态已经在两张本地动作之间变化，fresh probe 会推进 WorldVersion；
- 该版本不再等于 `safeSession.LastAcceptedWorldVersion`；
- `TryBeginAction` 在原生动作入队前拒绝旧后缀；
- 不会把新的真实状态偷偷当成旧计划的 baseline。

日志：`U1_PRE_ACTION_PROBE`。

### 2.2 原生动作提交前冻结“共同模型的预期后态”

新增：

`SolverController.SafeExecutionExpectedState.cs`

每张 Safe Execute 本地牌在 `card.TryManualPlay(...)` 之前：

1. 从当前 live combat 抓新的 `CombatRootSnapshot`；
2. 使用与当前会话相同的 `SearchRoutePolicy`；
3. 新建 `CombatBeamSolver`；
4. 直接调用生产 `ReplayDiagnosticPrefix([action])`；
5. 通过 `CaptureDiagnosticContinuation` 冻结预测 `ContinuationStamp`；
6. 通过 `MultiplayerContinuationRemoteFingerprint.CapturePredicted` 冻结完整队友语义指纹；
7. 释放 replay snapshot/simulator。

这条路径没有手写卡牌效果，也没有直接调用第二套 `ManualPlay/OnPlayWrapper`。

若单动作 replay 到达非 `None` 的搜索边界，Safe Execute 在提交真实动作之前 fail closed，并记录：

`U1_EXPECTED_POST_STATE_UNAVAILABLE`

### 2.3 原生队列稳定后比较 predicted vs live

仍先等待：

- 根 native action 完成；
- Choice producer 完成；
- `ActionExecutor.FinishedExecutingActions()`；
- WorldTracker 给出稳定的新 WorldVersion。

随后冻结 live：

- `ContinuationStamp.CaptureLive(state, state.Players.ToArray())`
- `MultiplayerContinuationRemoteFingerprint.CaptureLive(...)`

比较结果记录：

`U1_POST_STATE_COMPARE`

核心字段：

- `continuation_match`
- `remote_match`
- expected/actual state fingerprint
- expected/actual remote fingerprint
- 最多 8 个逐字段 continuation differences

## 3. 新的授权规则

`MultiplayerSafeExecutePolicy.RevalidateAction` 现在按以下顺序：

1. 必须捕获真实 `PlayCardAction`；
2. 原生 ActionQueue 必须真实为空，不再硬编码 `ActionQueueIdle=true`；
3. WorldVersion 必须已推进且稳定；
4. predicted remote state 必须等于 live remote state；
5. predicted continuation state 必须等于 live continuation state；
6. 满足以上条件时：
   - 有下一张本地牌 → `SafeToContinue`
   - 本地动作链结束 → `ExpectedLocalChange`

其中 remote mismatch 优先分类为 `RemoteOrUnknownChange`，这样真实队友插入仍会废弃旧后缀；本地/敌人/RNG 等共同模型后态不符则分类为 `ActionMismatch`。

## 4. 旧 gate 的处理

以下字段继续计算：

- `LocalCardRemovedFromHand`
- `LocalPlayerIdentityStable`
- `EnergyStateConsistent`
- `TargetIdentityStable`
- `RemotePublicStateUnchanged`
- `EnemyStateMatchesExpectedTarget`

但它们只进入：

`legacy_mismatches=...`

不再单独决定继续/中止。

因此以下情况现在可以成立：

- legacy 判断“非目标敌人也变了”，但生产 replay 正好预测到了这个 AoE/触发 → **继续**；
- legacy 判断“远端变了”，但生产 replay 预测到这是本地卡的合法联动 → **继续**；
- legacy 判断“牌没有离开手牌”，但生产 replay 预测它结算后回手 → **继续**；
- legacy 全部看似正常，但 live 队友状态和预测不一样 → **中止**。

## 5. 当前合同测试

`MultiplayerSafeExecuteChecks` 新增/修改行为合同：

- semantic state + remote 都匹配时继续；
- legacy 多项同时失败，但 semantic state 匹配时仍继续；
- `ExpectedRemoteStateMatched=false` → `RemoteOrUnknownChange`；
- `ExpectedContinuationStateMatched=false` → `ActionMismatch`；
- native action 缺失仍 fail closed；
- WorldVersion 不稳定仍 fail closed；
- session abort 后不能重新授权；
- EndTurn 仍只接受已确认 WorldVersion。

新增静态门禁：

`source/tools/test-u1-action-poststate.ps1`

它固定：

- pre-action fresh probe 必须发生在 `TryBeginAction` 前；
- expected replay 必须发生在 `card.TryManualPlay` 前；
- helper 必须复用 `ReplayDiagnosticPrefix`；
- predicted/live 都使用 ContinuationStamp + remote fingerprint；
- helper 不得直接出现第二套 `ManualPlay/OnPlayWrapper`；
- ActionQueueIdle 必须读取真实 executor 状态；
- legacy heuristics 只能作为诊断。

## 6. U1 验收矩阵

| 样例 | 期望 |
| --- | --- |
| 重锤 + 真实 Choice/烙印 + 后续牌 | Choice producer 完成；predicted/live 后态相同；不因 legacy enemy/hand diff 中止；继续下一张 |
| 连续祭品后抽出并使用后续牌 | 每张祭品后 hand/draw/energy/RNG 与 replay 相同；后续新抽牌可被下一动作 live classifier 找到并执行 |
| 本地合法 AoE / Power 连锁 | 多个敌人变化只要与 replay 相同就继续 |
| 本地效果合法影响队友 | remote fingerprint 若与 replay 相同则继续 |
| 队友在两张本地牌之间行动 | pre-action probe 先推进 WorldVersion；旧 session 在提交下一张前拒绝 |
| 队友与本地动作并发产生额外变化 | post-action remote fingerprint 不匹配；中止旧后缀并 fresh search |
| 模拟器遗漏一个真实效果 | continuation mismatch；中止并输出逐字段差异，不盲目继续 |
| 用户取消/旧回调迟到 | cancellation/session generation 仍按原逻辑阻止第二次提交；U1 没有增加任何 native submit 入口 |

## 7. 当前验证状态

### 已完成

- U1 生产代码已接线；
- 新 gate 已切到 production semantic replay；
- pre-action remote race 已补 fresh probe；
- ActionQueueIdle 已从实际 executor 读取；
- legacy gate 已降为旁路诊断；
- 纯 policy 行为合同和 U1 结构门禁已加入总 contract suite。

### 仍为 UNVERIFIED

以下不能由 CI 的纯合同/字符串门禁替代：

- 本地 Release build 0 error；
- 真实多人“重锤 + Choice/烙印 + 后续牌”；
- 真实多人连续祭品抽牌链；
- 真实 Host/Client 在两张本地动作间插入队友动作；
- cancellation / network late callback 的真实双端 timing；
- U1 单动作 replay 的实际每张牌耗时和帧影响。

真实测试时必须保存同一 request 的：

`U1_PRE_ACTION_PROBE → U1_EXPECTED_POST_STATE → NATIVE_ACTION_CAPTURED → DEPLOY_ACTION_COMPLETE → U1_POST_STATE_COMPARE → MP2B_ACTION_RECONCILED`

若中止，再跟：

`MP2B_ACTION_REVALIDATION_DIAGNOSTIC / MP2B_ACTION_STATE_DIFF / MP2B_DEPLOY_ABORT`

## 8. 停止条件

U1 不再继续放宽 heuristics，也不修改 Beam、目标函数、Shadow 情景或 P3 排序。

没有真实运行证据前，不因为合同测试全绿宣称“重锤/祭品已实机修复”。

真实上述三类样例通过后，U1 runtime 才可标记 PASS，再进入 U2 的搜索共核与退化等价。
