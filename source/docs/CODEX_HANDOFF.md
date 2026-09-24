# Codex 当前交接

> 只记录当前架构、已验证结果、未验证事项和下一任务。历史过程从 Git history 与阶段文档查。

## 基线

- CombatSolver `0.40.2`
- STS2 / RitsuLib pinned `0.107.1`
- .NET 9 / Godot 4.5.1 / `STS2_01071`
- 单人和多人共享生产搜索/模拟核心；多人额外叠加队友情景、团队目标与同步边界。
- `RootActionPlayers` 只允许本地玩家；不会部署队友动作。
- MultiplayerOnly 牌保留真实牌堆状态与抽牌距离，但不进入主动搜索/自动执行。

## 当前架构

1. **共同搜索核心**
   - `SinglePlayerFullRoute` 与 `MultiplayerLocalCrossTurn` 共用完整 Search/Beam/Novelty/Growth/Relic/长期收益基础。
   - 无队友事件、相同目标和固定预算下已有 pinned 退化等价证据。

2. **多人目标与情景**
   - U3 使用固定 ScenarioSpec 做公平复评，缺失覆盖记 `Unknown`，不把漏评当优势。
   - U4 生产默认仍为 Robust；NominalReference / BoundedRisk 只做同 Matrix、零额外 replay 的参考遥测。
   - `NoAction` 只表示当前 forecast window 内队友不行动，不代表整场战斗不行动。

3. **本地/队友交错**
   - U5 支持 `local A → forecast-only teammate B → local C` 的 detached 模拟。
   - B 只属于预测环境，不获得 deployment authority。
   - reverse-order probe 只有完整 `ShadowFutureStateFingerprint` 相同才允许 ExactEquivalent collapse。
   - 每本地回合最多一个 teammate forecast observation；不主动等待理想队友行为。

4. **Safe Execute**
   - 不再存在固定 1/2/6/32 张生产 action ceiling；session 容量来自当前有限 selected route。
   - 每个本地动作走原生提交 → Choice/队列结算 → predicted/live semantic post-state 对照。
   - forecast observation 前停止部署条件后缀并 fresh replan；旧 request/generation 不得复活。
   - capability 记录为 `action_limit=selected_route`。

## 已验证

- U0：搜索转移、候选、最终选择、实际执行四层诊断与 pinned production replay 已完成；历史 real correlation 已有通过记录。
- U1：predicted/live post-state 主链、取消/迟到回调边界、pinned production replay 已完成；具体卡牌 Host/Client 专项链仍见“未验证”。
- U2：**COMPLETE**。共享搜索核与 pinned degenerate equivalence PASS。
- U3：**COMPLETE**。公平 Scenario Matrix 与 TimeLimit fail-closed 已有真实双端 PASS。
- U4：**COMPLETE**。interim-risk 反例已修正；三种风险解释合同/回归 PASS，生产默认仍 Robust。
- U5：**COMPLETE（算法/合同/pinned）**。Vulnerable/attack、提前终局、共享生成 RNG、资源合法性、抽牌/牌堆顶五类顺序反例均由 pinned production replay 覆盖。
- U6：**COMPLETE（实现与清理）**。
  - 删除固定 Safe Execute action ceiling 与旧 MP-2A 单动作兼容入口/limit aliases。
  - 删除对应旧 validator 路径，保留历史 MP-2B/MP-2C 日志兼容解析。
  - Pinned workflow 已覆盖 Safe Execute policy/classifier/controller 关键文件。
  - 新增 `validate-u6-runtime-closure.ps1`，区分 PASS / FAIL / UNVERIFIED。
  - U6 synthetic 合同覆盖：完整 PASS、无二次部署 PASS、旧 request 复用 FAIL、stale 新授权 FAIL、缺 remote delta UNVERIFIED。
  - Pinned 0.107.1 Release run `35939029095` SUCCESS。
  - compatibility run `35939790075` SUCCESS：`32 PASS / 0 FAIL / 0 SKIP`。

## 未验证 / 已知风险

- **U6-C 真实 Host/Client runtime smoke 被用户于 2026-09-24 明确跳过。** 因此 U6 的实现阶段已关闭，但不能声称真实 forecast boundary → remote WorldVersion advance → fresh search → old request dormant 已在当前 HEAD 实机 PASS。
- U5 的真实远端插入 ownership / WorldVersion / observation→fresh-replan 证据与上项相同，继续记为 `UNVERIFIED`。
- U1 的具体重锤+Choice、连续 Offering/抽牌链等专项 Host/Client 行为不由 pinned replay 替代。
- 这些项是已知运行证据缺口；除非后续改动触及对应边界或准备发布，不作为下一算法阶段 blocker。

## Quality-first 第一项状态

- **第一项最小可工作切片：IMPLEMENTED / PINNED PASS / REAL MP UNVERIFIED。**
- 多人最终结果保留最多 3 个不同首动作候选，每个最多 2 个当前回合普通 PlayCard；单人路径不承担候选保留开销。
- 轻量变化仅允许 exact root 或仍存活敌人的 HP/Block drift 进入 bounded refresh；目标死亡、资源、Power、牌堆/RNG 等强变化仍 fresh search。
- bounded refresh 在新根重放候选并区分 Continue / Reselect / FullRestart；Continue/Reselect 均通过固定前缀小搜索重新物化为新的 SolverResult，再进入现有 Safe Execute。
- 小搜索硬上限：beam 24 / 192 expanded nodes / 60 ms；Unknown 或无法物化一律 FullRestart。
- 日志：`MP_PLAN_REFRESH` + `refresh_to_native_submit_ms` 已接通。
- compatibility static/L1 PASS；Pinned Release run `35974977137` SUCCESS。historical classifier 未出现 current-only regression：P0 timed=`BOTH_TIME_BOUNDARY`，fixed-work=`OBSERVED_EQUIVALENT`，P1=`FIXED_WORK_PASS_TIME_BOUNDARY`。
- 真实 Host/Client 正反例尚未跑：普通队友非致死伤害应出现 Continue/Reselect；目标死亡等强变化应出现 FullRestart。此项继续记为 UNVERIFIED，不阻止进入下一实现项。

## Quality-first 第二项状态

- **第二项本地药水执行闭环：IMPLEMENTED / PINNED PASS / REAL MP UNVERIFIED。**
- `MultiplayerSafeExecute` 只有在实际 admission/native/revalidation 链接通后才启用 `CanUsePotionsAutomatically=true`；不是单独翻 capability 开关。
- Safe Execute structural/resolved gate 现支持本地 `UsePotion`：要求明确槽位与 PotionId，并在提交前重新核对本地槽位实例、PotionId 与 live `IsValidTarget`。
- 多人搜索中的 `AnyPlayer/AnyAlly` 药水首版只保留本地玩家目标；敌人目标与 Self/AllEnemies/TargetedNoCreature 继续使用已有 0.107.1 模拟语义。队友目标仍关闭，未扩展为控制队友药水。
- 原生执行复用现有 `potion.EnqueueManualUse(target)` / `UsePotionAction` 与 `NativeChoiceRuntime`；Safe Execute 现在会捕获并归因 `UsePotionAction`，随后复用 U1 one-action predicted/live semantic post-state 校验。
- `ContinuationStamp` 与 multiplayer compact/local fingerprint 原本已包含药水槽位，因此成功动作必须同时满足槽位消费/身份变化、WorldVersion advance、稳定后态与 continuation 一致；不新造药水专用执行器。
- Smart / Disabled / RequireAtLeastOne 与 potion-free baseline 未改；`PotionStrategyChecks` 继续约束“无收益不浪费药水”等策略语义。
- 第一项 bounded refresh 仍故意只重放普通 `PlayCard` 前缀；路线遇到药水不会删掉药水再把后续牌伪装为原路线。无可重放候选时直接 `FullRestart`。
- compatibility run `35983184607` **SUCCESS**；Pinned 0.107.1 Release run `35983115684` **SUCCESS**。中途两个失败只暴露字段从 `NativePlayCardCaptured` 泛化为 `NativeLocalActionCaptured` 后的测试/诊断残留，均已修正。
- 现有 potion differential / potion continuation runtime fixtures 继续覆盖资源、伤害/状态、Choice、原生 `UsePotionAction` 与槽位消费语义；本轮没有在真实 Host/Client 多人局重新跑这些 fixture，因此实际多人自动喝药仍记为 `UNVERIFIED`。

## Quality-first 第三项状态

- **第一轮坏路线归因 / 排序可观测性：IMPLEMENTED；生产默认未迁移。**
- 已恢复真实历史问题包 `25b905c1322b41e6b9a8e10baeae5606`：T2 手牌含 `ANGER(0)`，旧 Solver 路线 `Tremble → Dismantle → Strike → EndTurn`，在 Shuffle 边界形成 `PartialLocalCrossTurnProjection`。根因是未完成多人路线的最终基础排序过早惩罚 `AngerCopiesGenerated`；当前 HEAD 已保留 Enemy HP 优先的定向修复和合同。
- 重锤→烙印、连续 Offering 的既有坏体验属于 Safe Execute / deployment 截断，不是 Robust 情景排序证据；这些执行层边界由 U1/U6 负责。
- 新增 `MultiplayerScenarioStrategySelection` 和 `MP_QUALITY_SORTING`。同一 U3 Matrix / 同一预算下现在能直接看到 baseline winner 是否被 Robust 覆盖，以及 Robust/NominalReference/BoundedRisk 是否一致；不增加 replay、不改变搜索预算。
- 生产仍为 Robust。现有可复原坏路线没有证明修复后 HEAD 上是 U3/U4 Robust 推翻了更好的共同搜索核心路线，因此本轮不改风险权重、不把等权压力情景均值当概率期望。
- 当前验证：源码改动已推送；compatibility / pinned Release CI 正在运行。无新的真实 Host/Client 重放，因此不宣称当前实际出牌质量已实机改善。

## 下一任务

继续第三项，只处理**当前 HEAD 新出现的明显坏路线**。优先保留问题包和用户明确更好的合法手打前缀，并用 `FINAL_CANDIDATE → MP_QUALITY_SORTING → FINAL_SELECTION → Safe Execute` 定位丢失层。

边界：

1. 若手打前缀未进入 FINAL_CANDIDATE，继续区分未枚举与 Beam 丢失；不要直接改 U4。
2. 若 hand/baseline 候选仍在且 `robust_overrode_baseline=true`，再用同一 Matrix 的各情景指标判断是否确为 Robust 过度保守；没有证据不切默认。
3. 若 FINAL_SELECTION 正确但实际动作缺失，回到 Safe Execute / Choice / continuation 处理，不污染排序目标。
4. 比较生存、累计战损、最终 HP、结束轮数、药水支出与响应时间；保持总预算不变。

仍按小阶段推进。
