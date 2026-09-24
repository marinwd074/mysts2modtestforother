# 多人求解器：转向实际出牌质量

基线：2026-09-24，main@923f5c999d93c7114b4d146e1494dd4272899c8a。
性质：定向代码审查与下一轮实施任务；本轮未修改生产代码、未运行游戏。实施前刷新HEAD。

## 开发决策

暂停原handoff中的U7精确斩杀优先级。优先解决：无差别取消重搜、自动执行与候选能力不一致、默认排序是否真的优于简单本地路线。保留U0–U6的共同模拟、实际后态校验、情景矩阵和顺序语义，不重复施工。

成功标准改为：代表局面出牌不被明确更优的合法路线支配；无关或轻微队友动作后不必清空路线重新等候；支持的本地药水与遗物能力实际参与决策并形成可执行路线。合同测试通过不是出牌质量证明。

## 已确认的代码事实

1. `SolverController.MonitorCombatPresence` 检测 `MultiplayerClientProbe.Observe` 返回变化后调用 `InvalidateMultiplayerSearch`。后者首先取消debounce、deferred与搜索，然后清空LatestResult/LatestStamp，部分情况下取消部署。它没有先比较新状态下的本地动作选择是否改变。
2. `SolverController.SearchLifecycle` 接收结果时仍检查搜索WorldVersion与当前版本一致。过期结果不能直接部署，但这不要求把其动作序列和搜索成果全部销毁。
3. `MultiplayerWorldTracker` 默认200ms稳定延迟；增加延迟只能合并事件，不能解决“哪些变化值得重算”的决策问题。
4. `SolverController` 捕获设置时，完整多人搜索已启用与单人相同的PotionPolicy和PotionStrategy；药水不是完全没进入搜索。
5. `SolverSessionCapabilities.MultiplayerSafeExecute.CanUsePotionsAutomatically=false`；`MultiplayerSafeExecutePolicy.ClassifyStructural` 拒绝非PlayCard。Deployment虽已有UsePotionAction原生代码，也不代表正式Safe Execute路径能够通过前置gate。搜索推荐与执行能力存在结构差异，具体坏路线是否由它导致须重放验证。
6. `FinalPlanOrdering`明确把 `selectedScenarioDecision` 设为 `u4RobustDecision`。NominalReference/BoundedRisk只对照记录，尚未用于生产选择。
7. U4的NominalReference是压力情景等权平均，不是可信队友预测。BoundedRisk目前是mean与worst差距的0.5惩罚，且保留全情景存活/胜利优先，并非真正的显式风险约束。不能直接切换名字就声称问题已解决。
8. 单多人已共享完整Beam/Novelty/Growth/Relic基础。不得重复增加“启用遗物算法”开关。远端遗物、版本特殊效果另查真实调用路径，不能把存在的旧allow-list直接当生产根因。
9. U5已经支持预测中的local→remote→local；forecast observation处仍停止条件后缀并fresh replan。扩展预测能力可能增加此类边界，不能因U5完成就推断体验更流畅。

## 第一项：从整条路线失效改成分层更新

目标：真实状态一变就更新观察；只有必要时才重新寻找路线。

三个层次分别管理：

- 预测新鲜度：战损、敌人HP、未来回合等数字是否已更新。
- 当前动作可执行性：牌/药水身份、目标、资源、Choice、生命周期是否成立。
- 当前决策质量：该动作在新状态下是否仍值得选择。

实施流程：

1. 保留上一轮少量优质候选的不可变本地动作序列，包含当前路线和不同首动作的备选；不要持有已释放的simulator或跨根复用可变分支。
2. 真实事件结算后抓取新根。将当前剩余前缀和少量备选在新根上重新模拟，更新目标与成本；首版明确限定重放范围，避免每次重放整场比重搜还贵。
3. 当前路线仍合法、且限定候选比较没有实质性更好路线时，保留新验证的前缀；其未来预测可在后台继续改进。标明这是有界近似，不是全局最优证明。
4. 出现目标死亡、新抽牌/资源、关键Power、RNG变化等，按影响重新生成相关候选；这些是强刷新信号，不是所有情况都必须丢弃已有路线。
5. 当前路线不可重放或状态无法建模时完整重搜。Unknown不能变成Continue。
6. 执行前始终对新根生成的新计划授权；不能只重设WorldVersion，让旧预测后态假装有效。动作已提交但结果未知时不得重复提交。
7. 运行中的旧搜索可按成本选择取消或允许产出候选种子；过期结果只能在新根重放验证后采用，绝不能原样部署。最多一个主搜索和有界重评，防止后台工作堆积。

变化分级是优化提示，不是忽略状态的白名单。队友单纯伤害也可能跨斩杀阈值、触发阶段/被动或改变省药价值；必须从事件实际语义和新根重放判定。共享RNG变化后只复用动作建议，不复用旧抽牌确定性或旧状态缓存。

验收用两个正反例：无关队友变化后保留当前动作，避免完整重搜；队友击杀目标/改变斩杀机会后及时换路线。记录full_restart、prefix_replay、首动作改变、原生提交耗时。单纯“重算后首动作相同”不能证明重算无价值，还应比较后缀、目标、药水和终局预测。

### 第一项实施状态（2026-09-24）

**实现与 pinned 门禁完成；真实 Host/Client 正反例仍为 UNVERIFIED。**

- 最终排序只在 `MultiplayerLocalCrossTurn` 保留最多 3 个不可变候选；候选首动作互异，每个只保留当前回合最多 2 个普通 `PlayCard` 动作，不持有 simulator。药水候选暂不进入这条 refresh 快路，留给第二项闭环。
- WorldVersion 变化后不再无条件清空路线。旧根与新根完全一致，或唯一变化是**仍存活敌人的 HP/Block** 时，进入 bounded refresh；目标死亡、本地资源、牌堆/Power/RNG/行动等强变化继续完整搜索。
- bounded refresh 在新根重放当前候选和备选前缀；比较后区分 `Continue` / `Reselect` / `FullRestart`。Unknown、当前候选不可重放、固定前缀无法重新物化时一律 `FullRestart`。
- `Continue` 与 `Reselect` 都会用胜出前缀在**新根**做一次小预算固定前缀搜索，生成新的 `SolverResult` 后才显示/授权/执行；不是只更新 WorldVersion 或继续部署旧结果。当前上限：beam 24、192 expanded nodes、60 ms，且不为该小搜索额外预留 U3 scenario reevaluation 预算。
- Safe Execute 仍沿用 U1/U6 的原生动作提交与逐动作 predicted/live 后态校验；bounded refresh 不增加队友动作权限。若新计划被采用，Safe Auto 会创建新的 deployment/session。
- 诊断新增 `MP_PLAN_REFRESH`：`full_restart`、`prefix_replay`、`first_action_changed`、源/新 route identity、bounded nodes/boundary、`replay_latency_ms`；`NATIVE_ACTION_CAPTURED` 额外记录 `refresh_to_native_submit_ms`。
- 纯合同覆盖：exact root 与存活敌人 HP/Block 变化允许 bounded refresh；目标死亡、能量变化、RNG 变化强制 fresh search。
- 最终验证：compatibility static/L1 **PASS**；Pinned 0.107.1 Release run `35974977137` **SUCCESS**。P0 timed 仍为历史共同的 TimeLimit 边界，classifier 为 `INCONCLUSIVE_TIME_BOUNDARY / BOTH_TIME_BOUNDARY`；固定工作量语义保持 `OBSERVED_EQUIVALENT`，P1 为 `FIXED_WORK_PASS_TIME_BOUNDARY`，没有出现 current-only regression。
- **未验证**：真实多人“队友只打伤存活目标 → Continue/Reselect 且少一次完整等待”和“队友击杀目标 → FullRestart/换目标”的 Host/Client 事件序列；没有这两条实机证据前不宣称实际体验已改善。

## 第二项：让本地药水能力形成闭环

不是单改CanUsePotionsAutomatically=true。贯通：候选枚举→目标解析→原生UsePotionAction→Choice→稳定后态校验→continuation。

复用单人原生使用路径和已有0.107.1模拟器；保留Smart药水成本、禁用/必用设置。支持范围由实际动作语义判断，不能因多人模式整体禁用。

首先接通当前已正确模拟的本地持有药水，对本人和敌人的效果；队友目标根据pinned 0.107.1真实API逐项支持。当前网页的“可投给队友”描述可能对应更新版本，不得直接套入固定版本。自动使用用户自己的药水与控制队友药水是两件事，后者不在范围。

在切换期间保留可执行的无药备选；不得把有药路线截到药水之前后继续宣称是原完整方案。无药路线不一定能获胜，必须真实报告结果。自动出牌的候选排序需知道哪些本地动作当前可部署；多人专属牌继续遵循用户此前的手动规则。

遗物重点核对：本地hook是否共用、根快照与Fork中计数器是否一致、团队影响是否按owner/target正确计算、长期收益是否压过当前明显斩杀。已有覆盖不重做；列出真实缺口后修。

验收：至少覆盖即时资源、伤害/状态、需要Choice的已支持药水类型，确认使用一次、槽位/身份正确、后续路线继续，且Smart不会无收益浪费药水。按实际修改选择测试，不为列齐清单添加不存在的游戏效果。

### 第二项实施状态（2026-09-24）

**本地药水执行闭环已实现并通过 pinned 门禁；真实 Host/Client 多人自动用药仍为 UNVERIFIED。**

- Safe Execute 不再把 `UsePotion` 作为结构性拒绝。动作必须带 PotionId 与槽位；提交前重新解析本地玩家槽位并核对实例 ID，槽位缺失/换药直接 fail closed。
- 目标 admission 首版限定为**本地玩家或敌人**。多人搜索对 `AnyPlayer/AnyAlly` 会过滤队友目标；Self、AnyEnemy、AllEnemies、TargetedNoCreature 继续使用已有模拟器规则。队友目标药水尚未按 pinned 0.107.1 逐项验真，因此没有放开。
- 原生执行继续复用单人 `PotionModel.EnqueueManualUse` 产生的 `UsePotionAction` 与现有 `NativeChoiceRuntime`。Safe Execute 会捕获期望的原生动作类型，并把药水动作送入与出牌相同的 U1 one-action replay → queue/Choice settle → predicted/live semantic post-state 对照。
- `ContinuationStamp` 与 multiplayer WorldVersion fingerprint 已包含本地药水槽位；因此槽位消费、PotionId 变化和后续 continuation 会参与真实/预测一致性校验。动作只能授权一次，失败或不稳定后态不会继续旧后缀。
- Smart / Disabled / RequireAtLeastOne 与 PotionStrategy 保持单人原语义；多人 potion-free baseline 强制使用本地玩家比较，不再让 TeamLoss/WorstPlayerLoss 参与“该不该喝药”的基线选择。团队目标只在已通过本地药水策略准入的路线之间继续排序；没有降低药水成本阈值，也没有删除无药路线。
- 第一项 bounded refresh 仍只接受普通 PlayCard 前缀。如果当前候选从药水开始，它不会生成可重放候选并会 full restart；如果药水出现在首牌之后，只允许重放药水之前的合法牌前缀，不会跳过药水把后续动作伪装成原路线。
- 现有测试面中，`UnattendedTestRunner.Potions` 已有真实/模拟差分、槽位消费与资源/伤害/Power 断言；`PotionContinuation` 已覆盖需要 Choice 的药水以及原生 `UsePotionAction` continuation。本轮没有重新跑真实 Host/Client Godot runtime，因此这些现有 fixture 不能替代多人实机证据。
- 新增/更新的 L1 合同覆盖：合法本地药水 shape、缺 ID/槽位、槽位药水缺失/ID 漂移、队友目标拒绝、live target validation 失败、通用 native local-action attribution。
- 最终门禁：compatibility run `35983184607` **SUCCESS**；Pinned 0.107.1 Release run `35983115684` **SUCCESS**。
- **未验证**：真实多人中本人资源药、敌人伤害/状态药、带 Choice 药水各至少一条 Safe Execute 完整链，以及队友在用药结算中插入动作时是否按预期 fail closed/fresh replan。没有这些证据前不宣称实际多人自动用药体验已改善。

## 第三项：用坏路线对照决定生产排序

从现有问题包选少量可复现快照，添加用户明确更好的手打前缀。每局固定真实初态、相同队友脚本、相同总预算。人类路线也必须用真实规则重放，不能凭主观判优。

对照：

- 现行Robust生产路线。
- 共同搜索核心的本地基线：保留真实多人血量/目标/触发语义，不假装敌人变回单人难度。
- 合作名义路线：使用一个明确可解释的队友响应策略；按状态响应，不预知未来。
- 已知手打前缀，并允许相同预算继续求解后缀。

先定位人类好路线在哪一步丢失：没有枚举、Beam剪掉、目标评分选输、情景复评推翻、执行gate截断。只修有证据的一层。当前无需再造一套框架或重复跑U0–U6全套。

建议目标方向：优化正常合作下的团队结果，用短期致命风险和明确损失预算限制过分依赖队友；不要默认要求每个压力情景都能完成整场胜利。压力情景用于压力检查，不当已校准概率。有限时域未获胜也不等于真实失败。

可保留三类排序对照，但不能机械把0.5风险惩罚换成另一个任意常数。若所有备选都违反预算，选择明确的最小违规/最佳生存路线并记录，不应卡死或伪装安全。

验收以实质收益为准：生存、累计损失、最终HP、结束轮数、药水支出、响应时间。同目标下不能被已知手打路线明显支配；有取舍的路线单独呈现，不把所有维度压成一个看不懂的分数。不保证超过所有人类局面。

### 第三项第一轮实施状态（2026-09-24）

**现有坏路线证据闭环完成：两个排序坏例均定位到基础最终排序；尚无证据支持切换默认 Robust。**

- 真实历史问题包 `25b905c1322b41e6b9a8e10baeae5606`：T2 手牌包含 `ANGER(0)`，旧路线为 `Tremble → Dismantle → Strike → EndTurn`，在 Shuffle 边界形成 `PartialLocalCrossTurnProjection`，明显遗漏合法的 0 费即时伤害。该问题定位在**共同搜索后的最终基础排序**：未完成多人路线过早比较 `AngerCopiesGenerated`，压过了确定的 Enemy HP 进展。现 HEAD 已保留针对性修复：仅实际多人未完成路线把 Enemy HP 排到 Anger copy 长期成本之前；单人和完整胜利排序不变。
- 历史 X1 的 **T3 空推荐** 是第二个独立坏路线样本：搜索中已经存在当前回合可出的 `PlayCard` 候选，但局部质量相同/等价时，最终基础排序被 `ActionCount` 的短路线 tie-break 选成仅 `T3:EndTurn`。当前 HEAD 已在多人本地跨回合的成员内排序与 Beam portfolio 选择中保留“当前回合有可出牌”平局优先；该规则不作用于单人退化夹具。
- 重锤→烙印以及连续 Offering 的历史失败属于 **Safe Execute / deployment gate**：候选已经存在，但 Choice、action ceiling 或后态 continuation 曾截断后缀。这些不能作为“Robust 排序错误”的证据，相关执行边界已由 U1/U6 处理。
- 新增 `MultiplayerScenarioStrategySelection`：在**同一 U3 情景 Matrix、同一预算、零额外 replay**下同时记录 baseline、Robust、NominalReference、BoundedRisk 的选择索引，并显式给出 Robust 是否覆盖 baseline。
- 新增 `MP_QUALITY_SORTING`：直接输出 `production_selected_baseline_rank`、`override_layer=baseline|scenario_robust`、`robust_overrode_baseline`、Robust 与两条参考策略是否一致，以及 `quality_signal=scenario_override_disputed|none`。下一份当前版本坏路线不再需要靠人工猜测“是不是 U3/U4 推翻了共同搜索核心”。
- 新合同用固定输入覆盖“baseline 由 Nominal 选择、Robust 在同 Matrix 改选另一决策、BoundedRisk 再选第三决策”的情况，确认归因层只测量已有选择，不改变风险权重和搜索预算；Anger 真实问题包 ID 也写入当前合同说明。
- **生产行为刻意未变**：仍使用 Robust。两个可复原的“明显不如手打”排序样本（ANGER 与 X1 T3）都证明**好候选已经存在、基础最终排序曾选错**；重锤/Offering 则属于执行层。现有证据没有一条指向 U3/U4 Robust 推翻了更好的共同搜索核心路线，因此不把 `0.5` 换成别的任意常数，也不把压力情景均值冒充概率期望。
- 验证：compatibility run `35985739446` **SUCCESS**；Pinned 0.107.1 Release run `35985739404` **SUCCESS**。合同明确覆盖 ANGER 坏例、X1 T3 空推荐坏例，以及“Robust 覆盖 baseline 且与参考策略分歧”的归因信号；这些测试只固定层级归因，不把 synthetic 分歧当作真实生产迁移证据。
- 下一步只需对**当前 HEAD 新出现的明显坏路线**保留问题包和明确手打前缀；先看 `FINAL_CANDIDATE → MP_QUALITY_SORTING → FINAL_SELECTION → Safe Execute` 即可定位枚举/Beam/基础排序/情景复评/执行中的哪一层。若 `robust_overrode_baseline=true` 且手打前缀在 baseline 中存活，再进入生产风险策略迁移；否则修实际丢失层。
- Beam 层已补真实生产候选池 A/B 诊断：开启详细诊断时，对同一个 `RankBest` pool 额外计算 legacy 单人排序，仅记录 `MP_BEAM_RETENTION_AB` / `MP_BEAM_RETENTION_AB_FINAL`，不增加搜索节点、不改 BeamWidth、不改变生产选择；最终还能区分 raw 顺序差异、被 outer portfolio 救回、被 incumbent 再裁掉、以及真正的 Beam pruning。
- 唯一实机判定入口为 `source/tools/multiplayer-lab/validate-beam-retention-ab-results.ps1`，6-case 合同已覆盖 `no_difference_observed / raw_rank_difference_only / outer_portfolio_rescue_observed / incumbent_pruning_observed / beam_pruning_observed`。Pinned 0.107.1 run `35988652044` SUCCESS；compatibility run `35989267050` SUCCESS（33 PASS / 0 FAIL / 0 SKIP）。
- 因此第三项下一步已经到**真实 Host/Client 人工边界**：当前 HEAD 复现一个“明显不如手打”的多人局面，记录更好的合法手打前缀，并保留正式 Client journal/问题包。只有当 validator 给出 `beam_pruning_observed`，且对应 prefix 确实是更好的合法路线，才修改主 Beam；若被 portfolio 救回，则继续向 FINAL_CANDIDATE/U3/U4 追踪。

### 第三项新增真实样本：CEREMONIAL_BEAST T6

- T6 真实根手牌含 `VICIOUS` 且最终路线留 1 Energy，但多个 baseline/final route 都不打；`scenario_rerank=false` / `chance_rerank=false`，所以不是 Robust/U3/U4 覆盖。
- 同一战斗更早的完整投影曾多次把 `VICIOUS` 放进 T6，排除“完全未枚举/完全不支持”。
- 精确 Vicious 触发语义已存在，实际缺口是战略保留估值：旧 `StrategicEffectModel` 将 `ViciousPower` 当普通 `Scaling(1)`。现改为按同一可达牌窗口中的 Vulnerable 施加次数计 `CardAccessPotential`，不新增全局必打规则或权重。
- compatibility `35999711938` SUCCESS；Pinned 0.107.1 `35999711924` SUCCESS。仍需当前 HEAD 实机复现确认 T6 路线是否因此保住 Vicious。
- Headbutt immediate `MoveToDrawTop` 已在真实 CEREMONIAL_BEAST 日志通过现有单人/多人共用 `NativeChoiceRuntime` 自动选中 `UNRELENTING`；不要为它复制执行器。跨回合 Stampede/TurnStart choice 是另一个已修的执行边界。

## 第四项：最后再决定精确斩杀是否值得加

只有对照证明“候选里确实没找到可行斩杀”时，才恢复局部DFS优先级；若斩杀已找到却被Robust排序淘汰，加DFS无效。总预算包括队友搜索、复评、快照与重放，不隐藏开销。

## GPT下一轮直接执行指令

以当前 HEAD 为准，先读仓库规定的最小上下文。第一项 bounded refresh 与第二项本地药水执行闭环都已实现；下一轮直接进入第三项，不再重复 U0–U6 或重新设计药水执行器。

从已有问题包选择少量“求解器明显不如手打”的可复现局面。固定真实初态、相同队友脚本与相同总预算，同时重放：当前 Robust 生产路线、共同搜索核心本地基线、一个明确可解释的合作名义策略、以及用户给出的合法手打前缀。先确定更好路线是没被枚举、被 Beam 剪掉、被目标/终局评分压掉、被 U3/U4 情景复评推翻，还是执行 gate 截断，然后只改有证据的层。

每轮报告：实际改变的用户行为、旧新对照、修改函数、验证结果、未验证项。没有实机不宣称体验已改善；也不把缺实机变成停止代码/重放分析的理由。按仓库规则提交推送实现，不自动发布。

## 核对入口

- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Runtime/SolverController.cs
- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Runtime/SolverSessionCapabilities.cs
- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Runtime/MultiplayerSafeExecutePolicy.cs
- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Search/CombatBeamSolver.FinalPlanOrdering.cs
- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Search/MultiplayerScenarioReevaluationPolicy.cs
- https://www.megacrit.com/news/2026-03-05-early-access-launch/ （官方多人协作与EA持续平衡说明；不作为0.107.1具体卡牌语义证据）
