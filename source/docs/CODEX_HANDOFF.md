# CODEX_HANDOFF

更新：2026-09-24  
目标：Slay the Spire 2 `0.107.1` / RitsuLib `0.107.1`，CombatSolver `0.40.2` 系列。  
当前工作卡：**U6 — 实机闭环与清理**。

## 1. 当前架构

- **搜索共核（U2）**：单人 `SinglePlayerFullRoute` 与多人 `MultiplayerLocalCrossTurn` 共享完整搜索核、Novelty/Growth/Relic/长期收益与生产模拟语义。多人差异由团队目标、队友情景和同步边界注入，不再维护第二套“简化多人搜索器”。
- **多人情景（U3）**：最终复评使用固定 Aggressive / Defensive / Conserve / NoAction ScenarioSpec，同一当前决策面对同一情景集合；缺失情景记 Unknown 并统一 fallback，不把漏评当优势。
- **风险目标（U4）**：生产最终选择仍使用 Robust comparator；NominalReference 与 BoundedRisk 只做同一 Matrix 上的 A/B 诊断，不冒充真实概率。NoAction 仅表示当前 Joint forecast window。
- **关键顺序（U5）**：本地搜索可形成 `local A → forecast-only teammate B → local C`。队友动作只存在于 detached simulator；不获得部署权限。每本地回合最多一个 teammate forecast observation，不提供主动等待队友动作。
- **Safe Execute（U1）**：每个本地动作提交前先 fresh probe；提交前用生产 `ReplayDiagnosticPrefix([action])` 冻结 predicted continuation + remote fingerprint；原生队列稳定后与 live 后态比较。legacy hand/energy/enemy/remote heuristic 仅保留诊断。
- **U6 执行边界**：Safe Execute 不再存在固定 6/32 张生产上限。能力日志使用 `action_limit=route_bounded`；具体有限容量只在每次 `MP2B_DEPLOY_START` 由该搜索路线的 `max_actions` 给出。

## 2. 已验证

- **U0 diagnostic correlation：PASS**。真实多人 request 8 已串联 `FINAL_CANDIDATE → FINAL_SELECTION → route_identity → DEPLOY_ACTION → NATIVE_ACTION_CAPTURED → U1_POST_STATE_COMPARE → MP2B_ACTION_RECONCILED`。详见 `U0_BASELINE.md`。
- **U1 pinned production replay：PASS**。production one-action replay 对 semantic match / remote mismatch / continuation mismatch / WorldVersion 插入 / abort-late retry 的分类已跑过 pinned 0.107.1。
- **U1 cancellation / late callback：PASS**。真实 request 10 在 native submit 后关闭 Solver，旧 Safe Execute session 没有继续提交后缀动作。
- **U2 degenerate equivalence：PASS**。相同 root、目标、DOP 与固定预算下，单人/多人 route policy 的首动作、完整序列、终局值与工作量一致。
- **P0/P1 pinned correctness：PASS**。Joint exact reuse / remote mismatch 与固定工作量目标语义已有生产 harness 证据。
- **U3：PASS**。代码/合同/pinned + 真实双玩家 Matrix + TimeLimit fail-closed 均完成。
- **U4：PASS（离线/合同/pinned）**。interim vulnerability 反例已修，三种风险解释在同一 Matrix 上报告；生产默认仍为 Robust。
- **U5：PASS（代码/合同/pinned）**。Vulnerable/attack、提前终局、共享生成 RNG、资源合法性、抽牌/牌堆顶五类顺序依赖均用生产 replay 证明不会错误 exact-collapse。

## 3. U6 已完成的清理

当前 U6 分支：`gpt/u6-runtime-closure-cleanup`。

已删除零生产调用的旧边界：

- `MultiplayerSafeExecutePolicy.MaxActionsPerDeployment = 32`
- `SingleActionLimitReason`
- `BoundedActionCeilingReason`
- `TwoActionLimitReason`
- `MultiplayerSafeLocalActionClassifier.TakeMp2ADeploymentSlice`

同时：

- `SolverController` 不再把 `max_actions=32` 当成 capability 输出；
- MP2B / interference validator 同时能读取历史固定-cap 日志和当前 `action_limit=route_bounded` 日志，但生产代码不再为历史验证器保留假边界；
- 新增 `tools/multiplayer-lab/validate-u6-runtime-closure.ps1`；
- 新增对应 synthetic validator contract，并接入 `tools/run-contract-tests.ps1`。

旧 `P0HistoricalPinnedHarness`、MP2A/MP2B 历史日志 validator **暂未删除**：它们仍被 workflow/contract suite 引用，不满足“旧路径调用为零才删除”的 U6 条件。

## 4. 仍未验证

以下不能由合同或 pinned replay 冒充真实 Host/Client PASS：

1. **U6 当前 HEAD 的远端插入闭环**：本地仍有计划后缀时，远端玩家改变状态；必须证明：
   - 所有 `NATIVE_ACTION_CAPTURED` 都属于同一 local `local_net_id`；
   - 远端变化由 post-action remote mismatch 或下一动作 pre-action WorldVersion probe 捕获；
   - 旧 request 不再提交下一张 native action；
   - 随后启动 fresh search。
2. **U1 卡牌专项实机**：
   - 重锤 + 真实 Choice/烙印 + 后续牌；
   - 连续 Offering/抽牌后继续执行未来手牌。
3. U1 one-action replay 的真实帧耗时/卡顿影响。

U5 的真实 ownership / WorldVersion / observation→fresh-replan 债务已合并到 U6 第 1 项，不再单独重复跑一套 U5 smoke。

## 5. U6 最小实机验收

只需一次受控 Host/Client 场景：本地 Safe Execute 至少有 2 张计划本地牌；第一张完成后，在下一张提交前或第一张结算窗口内让另一 Client 产生可观察状态变化。

本地 Client 日志执行：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\validate-u6-runtime-closure.ps1 `
  -LogPath '<client-log-path>' `
  -OutputPath '.\source\.local\multiplayer-lab\results\u6-runtime-closure.json'
~~~

PASS 允许两条合法检测路径：

- `U1_POST_STATE_COMPARE remote_match=false` → `RemoteOrUnknownChange`；
- 下一 `U1_PRE_ACTION_PROBE changed=true` → `world_version_not_accepted`，且该 action 没有 native submit。

两条路径都必须满足：旧后缀未继续提交、只使用本地原生动作、随后 fresh search。

## 6. 下一步

1. 先完成当前分支 compatibility + pinned 0.107.1 Release。
2. 跑上面的 **一次** U6 Host/Client closure。
3. 若 U6 validator PASS：更新 `CombatSolver_GPT_Architecture_Plan.md` 把 U6 标为 COMPLETE，并停止旧边界清理。
4. 若失败：只修 validator 指出的第一处真实断点，不扩大 Beam、时间预算、目标函数或 Shadow 搜索。
5. 当前 `CombatSolver_GPT_Architecture_Plan.md` 没有定义 U7；U6 收口后再决定是否进入“本地精确斩杀 / 缓存 / 增量修补 / 更远期预测”中的下一项。
