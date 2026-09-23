# U3 公平情景复演与非预知决策

状态基线：source commit `3c990c61fa90343f7e4385cd2d490019553f7c89`。

## 已实现

U3 只在真实多人根且启用团队目标时生效；单人以及 `PlayerCount == 1` 的 U2 退化路径不扣复评预算。

最终正式选择先按原排序得到少量不同的当前决策，再对每个决策使用同一固定 ScenarioSpec 集合：

- `aggressive`
- `defensive`
- `conserve`
- `no_action`

ScenarioSpec 不是跨候选硬重放同一牌串。每个当前决策会先从 root 重放自己的本回合本地前缀，然后从该候选自己的预测状态运行一次 bounded `ShadowTeammatePlanner.BuildTeamTopKRoutes`。四个 ScenarioSpec 共用这一次 Shadow 搜索，再分别通过生产 `ReplayJointForecastEndTurn` 复演对应世界线。

`CurrentTurnDecisionKey` 只包含当前 root turn 可部署动作，到 EndTurn 为止；`ShadowForecast` 与 `TurnStartChoices` 不参与当前决策身份，因此未来队友选择不能把同一个本地决策拆成多个“预知未来”的决策。

每个 candidate × scenario 显式记录：

- `Completed`
- `Terminal`
- `Unknown`

Choice、Shadow action-depth、U3 expansion budget 或回放边界无法完整覆盖时记 `Unknown`。最终 robust rerank 只有在至少两个当前决策、且**所有被比较决策都覆盖完整同一 ScenarioSpec 集合**时才启用；任一决策缺失情景时，全体回到进入 U3 前的基线排序，不允许漏评候选获利。

## 预算

U3 不增加原始 `MaxExpandedNodes`。

`MultiplayerScenarioReevaluationPolicy` 从原始节点预算中预留 bounded U3 work，主搜索只使用剩余部分。当前上限：

- 最多 4 个当前决策
- 每决策最多 128 expanded-work 单元
- 总 U3 reserve 最多 512

每个决策只运行一次 bounded Shadow planner；四个情景共享它。当前回合本地 prefix、Shadow branch expansion、最终场景 replay 都进入工作量计数；场景 replay 的 transition 另外按 `EndTurn + 实际 Shadow actions` 计数。

内部 parallel expansion worker 明确关闭二次 reserve，避免已经切过的主搜索预算再次扣 U3。

最终选择前冻结主搜索是否已经命中自己的 node allocation；U3 使用 reserve 后不能改变原主搜索的 `NodeLimit` 事实。若主搜索已经命中 `TimeLimit`，U3 不再执行，直接 fail-closed 到基线排序。

生产诊断：

- `MP_SCENARIO_BUDGET`
- `MP_SCENARIO_COVERAGE`
- `MP_SCENARIO_RERANK`

并有硬不变量：U3 后 `expanded <= original MaxExpandedNodes`；违反时直接抛错，不静默扩大算力。

## 已验证

Compatibility workflow：

- run `35878443605`
- source `3c990c61fa90343f7e4385cd2d490019553f7c89`
- 结果：SUCCESS

覆盖 U3 固定 ScenarioSpec、情景枚举顺序无关、Unknown fallback、production `CurrentTurnDecisionKey` 非预知、预算拆分、共享 Shadow work 计数，以及“只有预知未来队友选择才有利”的反例。

Pinned 0.107.1 workflow：

- run `35878443497`
- source `3c990c61fa90343f7e4385cd2d490019553f7c89`
- 结果：SUCCESS

其中 CombatSolver Release、U0/U1 production replay、U2 degenerate equivalence、P0/P1 pinned runtime 与历史 P0 A/B 分类均通过。U2 退化测试继续证明无真实队友时不会因 U3 reserve 改变单人搜索预算/结果。

## 仍未验证

**真实双玩家 U3 runtime 仍为 UNVERIFIED。**

P0/P1 pinned fixture 实际是单玩家 root；即使把 route policy 设为 `MultiplayerLocalCrossTurn`，`HasActiveMultiplayerRouteSemantics` 仍为 false，因此不能拿该结果冒充 U3 双玩家运行证据。

最小 Host/Client smoke：

1. 进入真实双玩家战斗，保证本地最终候选至少包含两个不同 `CurrentTurnDecisionKey`。
2. 运行一次多人完整搜索并导出日志/问题包。
3. 检查 `MP_SCENARIO_BUDGET`：
   - `main_node_budget + reserved == total_node_budget`
   - `replay_expanded <= reserved`
4. 检查每个参与决策的 `MP_SCENARIO_COVERAGE` 都列出四个固定 ScenarioSpec。
5. 任一 cell 为 `Unknown` 时，必须看到 `MP_SCENARIO_RERANK enabled=false`，且最终选择保持基线 fallback。
6. 若全部决策四情景完整，可允许 robust rerank；确认最终可部署动作仍只属于本地玩家。
7. 构造或观察队友不同后续行为时，确认相同当前本地动作仍只有一个 `CurrentTurnDecisionKey`。
8. 若主搜索命中 TimeLimit，确认 `MP_SCENARIO_RERANK ... reason=reevaluation_budget_unavailable`，且没有超时后的 U3 replay 扩展。

真实双玩家 smoke 通过后，U3 runtime 才可记 PASS；此前不要推进 U4 的默认策略迁移。

## 日志判定器

正常多人搜索先只判矩阵：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\validate-u3-scenario-results.ps1 `
  -LogPath <CombatSolver日志> -Phase Matrix
~~~

退出码：

- `0`：Matrix PASS
- `1`：发现预算/覆盖/fallback 自相矛盾，FAIL
- `2`：没有观察到可验证的真实 U3 matrix，UNVERIFIED

若专门制造一次主搜索 `TimeLimit`，再单独验证：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\validate-u3-scenario-results.ps1 `
  -LogPath <CombatSolver日志> -Phase Timeout
~~~

需要一次性检查两类证据时使用 `-Phase All`。判定器会检查：

- `main_node_budget + reserved == total_node_budget`
- `replay_expanded <= reserved`
- coverage 行数与 `decisions` 一致
- 每个 coverage 恰好包含 aggressive / defensive / conserve / no_action
- coverage 的 `replay_expanded` 总和等于预算日志中的实际复评展开量
- 任一 `Unknown` 时 robust rerank 必须关闭且 `FINAL_SELECTION scenario_rerank=false`
- 全部四情景完整时 robust rerank 必须开启
- `reevaluation_budget_unavailable` 路径不得同时执行 U3 budget/replay，最终仍须 `scenario_rerank=false`

判定器不会把 synthetic fixture 当 runtime 证据，也不会替代 Host/Client 身份确认。
