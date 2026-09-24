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

## 第四项：最后再决定精确斩杀是否值得加

只有对照证明“候选里确实没找到可行斩杀”时，才恢复局部DFS优先级；若斩杀已找到却被Robust排序淘汰，加DFS无效。总预算包括队友搜索、复评、快照与重放，不隐藏开销。

## GPT下一轮直接执行指令

以当前HEAD为准，先读仓库规定的最小上下文。完成第一项的一个可工作的最小切片：保留旧本地候选、在新根重放、区分继续/重选/完整搜索，优先覆盖队友普通变化导致无意义重启的路径。不要一次重写目标、药水和执行器。

针对旧实现与新实现运行相同事件序列，证明减少的是完整重启和等待，而不是漏掉变化。保留目标死亡/资源或RNG变化的反例。若现有结构无法支持有界重放，先给出代码级障碍并做最小重构，不回到全仓安全审计。

每轮报告：实际改变的用户行为、旧新对照、修改函数、验证结果、未验证项。没有实机不宣称体验已改善；也不把缺实机变成停止全部代码工作的理由。按仓库规则提交推送实现，不自动发布。第一项完成后接第二项，然后在实际坏路线中推进第三项。

## 核对入口

- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Runtime/SolverController.cs
- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Runtime/SolverSessionCapabilities.cs
- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Runtime/MultiplayerSafeExecutePolicy.cs
- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Search/CombatBeamSolver.FinalPlanOrdering.cs
- https://github.com/marinwd074/mysts2modtestforother/blob/923f5c999d93c7114b4d146e1494dd4272899c8a/source/src/Search/MultiplayerScenarioReevaluationPolicy.cs
- https://www.megacrit.com/news/2026-03-05-early-access-launch/ （官方多人协作与EA持续平衡说明；不作为0.107.1具体卡牌语义证据）
