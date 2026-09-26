# 多人本地搜索优化：实施任务书

日期：2026-09-26；原方案审查基线：`fe966f5`。下文设计和源码事实表记录该基线，实施进度见本节；不是性能收益承诺。后续 GPT 开工必须刷新 HEAD、核对下列符号，已经存在的能力不得重复实现。

### 当前实施进度（中间提交）

2026-09-26 用户要求执行最难的跨回合复用部分。本次实现基于 `9390af5`，保留远端已加入的路线提前发布和续接误判修复。

- P0 已收尾：默认 `MultiplayerSinglePlayerCore` 共用候选前缀保留入口；最终排序 → 结果物化 → Runtime Pending refresh → 新根 bounded replay 调用链加入快速结构门禁。`--replay-candidate-retention` 专用合同同时覆盖 exact / 存活目标 HP/Block 可重放，以及目标死亡 / 本地资源 / RNG 变化拒绝。候选仍最多 3 条、每条最多 2 个当前回合普通 PlayCard；未开启 Shadow/Team/Scenario/Carry，未修改预算、评分或 UI。
- P1 已实现：精确 continuation 仍是第一优先级；失败后若战斗、本地玩家和多人规则身份仍兼容，Runtime 提取当前回合连续普通 PlayCard 建议并冻结到 `SearchPolicySnapshot.ContinuationSeedActions`。Search 保留正常冷根，同时从真实新根额外重放建议分支；完整合法则加入完整种子，中途失效则只保留已验证前缀，第一步失效或明确的 `PredictionUnsupportedException` 则完全回到冷搜索；其他模拟异常继续上抛，不能被热启动掩盖。
- P1 种子只在每个请求的首个 Beam baseline 成员消费一次，Novelty、精炼成员、强制药水/Choice、既有 fixed-prefix 专项搜索不重复花这份工作；重放转移计入原搜索的 transition/elapsed 账户。所有后续排序、药水资格、最终物化和 Safe Execute 仍走现有流程，不直接部署旧结果，也不改变自动采用时机。
- 首版继续只支持同一新根当前回合、带精确 `CardStateKey` 的普通 PlayCard；药水、Choice、TurnStart Choice、Shadow forecast 和跨越下一个回合边界仍明确停在建议重放边界。RNG/手牌变化允许从新根重新尝试建议，但牌实例不存在、不可出或动作语义异常时停止，不猜同名替代。
- P1 已加入 `resume_kind=exact_continuation|seeded_search|cold_search` 诊断和专用 action-boundary 合同；真实 Host/Client 的跨回合等待收益与最终质量仍需实机/固定输入 A/B 证明，本阶段不宣称性能收益。
- P2 已进入实现：continuation seed 不再进入普通 Beam 初始 Frontier。Coordinator 先以独立 `continuation_seed_incumbent` 成员重放同一新根建议，探针最多使用当前节点/时间额度的 5%，实际消耗从后续普通搜索余额扣除；中途失效继续沿 P1 的已验证前缀语义。普通 Beam、Novelty、药水审计收到的 policy 均清空 seed，因此候选集合、Beam retention 和动作枚举不因 P2 占槽或变序。
- P2 独立成员只有得到完整胜利结果后才通过现有 `PublishAdoptableResult` / `SolverRouteAdoptionSeed` 发布为可手动采用 incumbent；随后正常搜索继续并用现有正式质量排序决定是否提升。未增加按钮，也未改变 Full Auto / Multiplayer Safe Auto 的默认提前采用时机。
- P2 固定输入验证已通过。独立 incumbent 在同一路线、同质量下三次均更早得到可采用结果；枚举提示随后做了独立 fixed-work A/B，三次都保持同一路线、同最终质量和相同节点数（7536 vs 7536），`time_to_reference_quality` 分别为 6163→2113ms、6726→2477ms、7730→3667ms，中位数 6726→2477ms。样本仅 3 对，不宣称统计显著或可靠 P95；证据见 [P2 targeted validation run 7](https://github.com/marinwd074/mysts2modtestforother/actions/runs/36222967258) 的 3 次 attempt。
- P2 枚举提示因此只接入已有合法 continuation seed 的 `MultiplayerSinglePlayerCore` 路径：它仅提高与已实现前缀严格一致的“下一动作”枚举优先级；一旦前缀偏离、跨回合、Choice/目标/卡实例不匹配就停止提示。候选集合、合法性、Beam retention、最终排序、预算和 Safe Execute 授权均不放宽；没有 seed 时行为不变。独立 incumbent 的 5% 预算和普通 Beam 的提示仍是两个隔离机制。
- P2 至此按离线固定输入收尾；真实 Host/Client 的累计等待与跨连续本地回合收益仍需实机证据。P3 调度扩展和 P4 热点优化仍未进入。

## 1. 项目决策

继续采用真实多人战场上的单人搜索核心，不计算队友未来出牌，不重新启用 Shadow、团队目标、Scenario/Robust 或 Carry。保留队友已发生的真实变化、多人怪物语义、被动效果与本地安全执行。

优先顺序：**修复默认模式复用入口 → 新根重放旧路线 → 提早交付合格候选 → 按数据处理搜索家族等待 → 优化真正的模拟热点**。

三个目标分别衡量：

1. 搜索更快：同等质量需要更少实际工作或更低单位模拟成本。
2. 好路线更早：缩短达到指定质量和可执行状态的等待，不能只让进度条提前出现。
3. 少重复计算：新回合先尝试续接、重放和热启动，不因预测偏差直接遗忘所有动作建议。

“最优路线”在本文指现有预算下找到的最佳合格路线。有限 Beam、未知队友行为不能证明全局最优；首动作稳定、连续没有改善、找到胜利路线，都不是普遍最优证明。不以减少 Beam、节点、时间、并发、验证或改变质量排序换速度，也不增加默认预算。

本文是本地单人核心下的专项任务书；质量原则仍见 [Quality First](CombatSolver_Quality_First_Next.md)。旧 [总体计划](CombatSolver_GPT_Architecture_Plan.md) 中队友预测方向不属于本任务。本文不把旧实验记录重新定义为当前生产性能事实。

## 2. 源码事实与问题定位

以下路径相对 `source/`；链接直接指向当前源码。

| 事实 | 位置 | 实施含义 |
|---|---|---|
| `MultiplayerSinglePlayerCore` 已存在；跨回合投影和持久路线缓存均允许该模式 | [MultiplayerLocalCrossTurnContracts.cs](../src/Search/MultiplayerLocalCrossTurnContracts.cs) | 不另造多人搜索器，不再实施“关闭队友搜索” |
| `FinalOrdering` 只在 `routePolicy == MultiplayerLocalCrossTurn` 时保留重放候选 | [FinalPlanOrdering](../src/Search/CombatBeamSolver.FinalPlanOrdering.cs)，`replayCandidates` 构造段 | 默认本地核心模式遗漏候选保留，是可直接处理的门控缺口；仍须合同证明整个调用链可达 |
| 已保留的候选最多 3 条、首动作不同、每条最多当前回合 2 个普通 PlayCard | 同上 | 当前复用不是跨回合路线修复，也不覆盖首动作药水 |
| bounded refresh 要求同回合、有候选、根相同或存活敌人 HP/Block 变化；随后固定前缀小搜索重新物化 | [MultiplayerPlanRefresh](../src/Runtime/SolverController.MultiplayerPlanRefresh.cs)、[RefreshContracts](../src/Search/MultiplayerPlanRefreshContracts.cs) | 已有 24 beam / 192 nodes / 60ms 的局部机制；60ms 只约束其中的小搜索，不是整个 refresh 的硬墙钟上限 |
| `TryCreateContinuation` 要求 `cached.ExpectedState == actual`，另有多人身份/版本检查 | [CombatPlan.cs](../src/Search/CombatPlan.cs)、[SearchLifecycle](../src/Runtime/SolverController.SearchLifecycle.cs) | 精确续接正确但容易失配；失配后需要单独的建议重放路径，不能放松等式 |
| `SolvedRouteCache` 以根、策略、版本等生成键，保存值型路线；默认本地核心模式可用 | [SolvedRouteCache.cs](../src/Runtime/SolvedRouteCache.cs) | 属于相同根的持久命中，不会自动解决新回合状态变化；不把它改成模糊命中执行缓存 |
| `SearchMemberExecutionSession` 已实现 `IResumableSearch`，支持 Step、取消、释放 | [Phases.cs](../src/Search/CombatBeamSolver.Phases.cs) | 它是 `CombatBeamSolver` 的嵌套类，不存在独立同名文件；不要再实施 E2 |
| Coordinator 已有 Smart 成员轮转、跨家族 potion scout、interim/progress 发布 | [CombatSearchCoordinator.cs](../src/Search/CombatSearchCoordinator.cs) | 不是从零添加调度和流式结果；先验证当前哪个家族仍在等待 |
| E1 已在正式排序后提前发布预览，但该预览不携带 adoption seed | [当前交接 E1](CODEX_HANDOFF.md#搜索效率-e12026-09-25已关闭) | “已展示”与“可安全采用”不同；新阶段只补剩余缺口，不重复声称解决 E1 |

由源码能确认复用边界窄、默认候选门控遗漏；不能仅据此断言总耗时主要来自哪里。队友预测关闭后的当前性能需要重新分类，旧 Shadow 耗时不能当成当前优化收益。

## 3. 先定义指标，避免优化错位置

沿用 E0/E1 诊断、request/member/candidate identity，扩展现有记录，不创建平行日志系统。

| 指标 | 精确定义 |
|---|---|
| `first_legal_ms` | 稳定根捕获后，首个通过当前动作语义验证的候选生成时间；不代表质量合格或允许执行 |
| `first_admissible_ms` | 首个通过当前模式、Smart 药水资格及结果准入的候选时间 |
| `first_executable_ms` | 首个候选在当前根重新物化且满足执行授权条件的时间 |
| `time_to_reference_quality_ms` | 首次不劣于固定基线最终合格结果的时间；必须使用相同目标字典序，不混成随意加权分 |
| `generated / selected / published / executable` | 同一候选四个时间点，用于分离生成晚、选择晚、展示晚和授权晚 |
| `resume_kind` | `exact_continuation / replay_repair / seeded_search / cold_search`，分别计数 |
| `restart_reason` | 延续现有 rejection reason，细分回合缺失、状态变化、目标死亡、RNG、策略变化、Unsupported 和过期 |
| 工作与成本 | expanded、transitions、replay transitions、materialization 时间、主线程最长占用、分配量和驻留内存 |

请求根变化时创建新 epoch。旧请求耗时不能归零后从报告消失：同时报告整个本地回合从首次请求到原生首动作的累计等待、废弃 transitions、总 CPU 工作。修复和新搜索共用请求预算；旧根工作仍计入交互累计成本。公平 A/B 固定相同事件序列与请求触发策略。

最小样本面：静态同回合、队友只打伤敌人、队友击杀目标、新回合抽牌、药水/Choice、长战斗。优先复用现有问题包及 `tools/E0PinnedHarness`，记录是否真实多人。离线输入证明可重复性；Host/Client 才能证明真实等待改善。

## 4. 目标架构：路线建议与执行授权分离

```text
Runtime 捕获稳定根 + observation revision + policy identity
    │
    ├─ 精确 continuation 命中 ───────────→ 现有新回合校验与授权
    │
    └─ 不命中
         ├─ RouteSeedStore：读取值型动作建议
         ├─ RouteRepair：在新根重放、重评、必要时截断
         └─ Coordinator：修复候选作为 incumbent/排序提示，原搜索继续
                    │
              现有排序与 Smart 准入
                    │
              新根物化 + epoch 核对
                    │
              Safe Execute 原生逐动作事务
```

只新增缺失职责，优先 partial/现有类型扩展。以下名字是建议接口，不要求建立新项目或通用框架。

| 职责/建议位置 | 输入/输出 | 禁止事项 |
|---|---|---|
| `RouteSeedStore`，Runtime | 按战斗、本地玩家、策略保存少量不可变动作建议；从已有 continuations 获取回合分段 | 不持有 SearchNode、simulator、live Model、访问分支的委托；首版仅内存，不另造磁盘缓存 |
| `RouteRepair`，Search | 新根、候选 token、预算 → 重放结果、合法前缀、失败边界、可重新评估的候选 | 不读取 live；不修改已释放分支；不沿用旧分数或旧预测 RNG |
| `SearchRequestWorkTotals` 扩展 | 统一累计 repair、验证、搜索、物化工作和 deadline | 不给修复、scout、发布各发一份完整预算 |
| `CombatSearchCoordinator` 扩展 | 候选提交、全局 incumbent、现有成员切片、结果版本 | 不复制一套排序；不让候选显示顺序成为执行顺序 |
| Runtime 接收与部署扩展 | epoch、结果版本、当前动作事务状态 → 采用/拒绝/留种 | 不只重设 WorldVersion，不重复提交正在结算的动作 |

接口草案：复用现有 PlanAction/状态戳表达，下列占位类型需映射现有实现。

```csharp
// 所有字段必须值型化；ImmutableArray 本身不保证其元素没有可变引用。
record RouteSeed(CombatIdentity Combat, PolicyIdentity Policy,
    int Turn, ImmutableArray<ActionToken> Actions, SeedOrigin Origin);

enum RepairStatus { ValidatedCandidate, PartialPrefix, Rejected, BudgetExpired }
record RepairResult(RepairStatus Status, ImmutableArray<ActionToken> Prefix,
    CandidateValue? Candidate, string Reason, WorkDelta Work);

// 每次从新根创建独立分支，输出不携带 simulator 所有权。
RepairResult ReplaySeed(CombatRootSnapshot root, RouteSeed seed,
    SearchPolicySnapshot policy, SearchWorkAllowance allowance,
    CancellationToken cancellation);
```

必须区分三个键：观察 revision 用于并发过期；精确状态键用于已建模语义下的续接/去重；策略 identity 用于质量和药水等配置一致性。哈希命中不能替代必要的规范化状态比较。重放建议允许状态不同，执行授权不允许凭旧状态通过。

## 5. 分阶段实施

### P0：补齐默认模式候选入口与归因

改动：`CombatBeamSolver.FinalPlanOrdering.cs` 的候选保留门控，及对应合同；必要时提取纯 predicate 到 `MultiplayerLocalCrossTurnContracts`。允许两个本地跨回合模式保留值型重放候选，保持单人和 current-turn-only 语义不变。不要把 `HasActiveMultiplayerRouteSemantics` 全局改为 true，否则可能重新打开实验排序/预测。

本阶段保持 3 条候选、2 张普通牌和现有 refresh 范围。同时检查 `Phases.cs` 物化后候选赋值、Runtime Pending refresh 入场和本地 score 比较，保证默认模式确实走到重放，不仅列表非空。

验收：默认模式能保留候选；存活目标 HP/Block 改变可进入新根重放；目标死亡仍拒绝旧目标；Shadow/Team/Scenario/Carry 全部保持关闭。此阶段不宣称已解决跨回合重算。

### P1：增加跨回合“建议重放”，保留精确续接

改动位置：`SolverController.SearchLifecycle.cs`、`SolverController.Continuation.cs`、`SolverController.MultiplayerPlanRefresh.cs`、`CombatPlan.cs`，新增 Search 侧最小 `RouteRepair`。从 Runtime 抽出纯重放计算，主线程仅捕获和采用；后台只消费冻结根。

顺序固定：

1. 完全保留 `TryCreateContinuation`，精确命中直接走现有快路。
2. 精确失败先记录 reason，不立即销毁值型 route seed。战斗或玩家不同、策略不兼容直接拒绝种子。
3. 按当前回合取旧 continuation 动作；不得简单给旧动作 Turn 加一。真实牌实例、目标、药水槽位和 Choice 在新根逐项解析，身份无法确定即停止，不以同名牌猜测替换。
4. 从新根重放当前回合剩余动作，遇首个不可支持/不合法/不确定步骤停止。首版不跨新的回合边界；过长序列按 transitions/deadline 让出，固定张数不是新的执行上限。
5. 完整重放得到候选，按当前本地质量政策评估。被截断的前缀只是搜索种子，不能继承旧终局胜利/战损结论，不能跳过失败动作继续后缀。
6. 将候选与当前根的正常搜索候选比较。保留既有无药基线与 Smart 资格；不要只比较前两张牌分数就宣布长期不劣。
7. 使用新根物化后的新结果授权当前回合；其未来回合仍为投影。新回合再次匹配或重放。

首版支持已能精确 replay 的本地 PlayCard；药水/Choice 按现有模拟支持逐项接入同一接口，未支持时返回明确边界，不绕过原生执行和后态校验。RNG 改变允许重新尝试动作建议，绝不复用旧抽牌结果；牌不在手上就停止。

修复失败 → 当前根正常搜索，是明确定义的产品路径；模拟异常 → 记录并拒绝该候选，不伪造成功。开发验证若失败，按任务约束报告并处理，不能用回退掩盖回归。

验收：构造旧预测不匹配但动作仍有效的下一回合，证明未冷启动即可生成新根候选；另构造目标死亡/抽牌改变使旧建议失败，证明不会部署旧预测。只证明合法仍不足以自动采用：质量不确定时继续搜索。

### P2：热启动与更早可采用的 incumbent

复用现有 `SolverProgress`、`SolverInterimResultOrdering`、`SolverRouteAdoptionSeed` 和 result materialization，不新增第二套 UI 协议。

1. 修复候选先作为独立 incumbent，不直接占用或替换现有 Beam 槽位；合法动作枚举集合、Beam retention 和最终排序保持原规则。
2. 旧路线动作可以作为可关闭的稳定枚举提示；这会改变有限预算探索次序，属于质量敏感算法变化，必须独立 A/B，不能标成严格等价优化。
3. 成员安全点产出的候选经过现有正式排序、药水准入和新根物化后，才携带可采用的值型快照。不要从提前预览反向抓取已释放 SearchNode。
4. 同一 root/policy epoch 中只提升更优合格 incumbent；不同根必须重评，不比较旧根分数。发布以候选版本去重，只在改善或首个合格候选出现时触发，发布开销计入总预算。
5. 现有自动执行模式下，采用早期候选属于时机变化。先以诊断/现有手动采用路径验证；自动提前采用须单独确认启用，不能因“当前最佳”就承诺最终质量不下降。实现计划不增加用户按钮或改变默认执行行为。

预算初值：修复工作最多占当前请求 transition 配额的 5%，且不得越过统一 deadline；没有 transition 配额时使用现有节点与时间账户。该比例仅是待测内部实验参数，不是生产承诺；花掉的工作从正常搜索余额扣除。合格基线和最终物化必须预留工作，不能被修复耗尽。

验收分两类：仅发布时机变化要求相同固定工作最终质量/身份；种子排序变化要求参考样本最终质量不退化且达到参考质量更早。前者不能用无药未完成基线的药水结果冒充合格 incumbent。

### P3：仅在当前数据证明等待后，扩展跨家族调度

现有 E3 Smart 固定轮转与 early potion scout 继续使用。先测 `first_work_ms`：究竟是 Novelty、无药 Beam、一药/多药、不同 beam 成员谁在等待；若当前 scout 已解决，不重做调度器。

若仍存在 starvation：以已有 `SearchMemberExecutionSession.Step` 为单位扩展 Coordinator 固定轮转，沿用当前 256 committed parents / 1024 transitions 切片上限；它不是硬毫秒抢占保证，单次昂贵转移仍须等待现有安全点。

- 先覆盖已有可恢复的 Beam 家族。Novelty 若不能恢复，单独任务改造并证明连续/切片等价；不能每片从头重跑。
- 保留各家族既有分配、总 deadline、总工作和默认并发；改变调度不能等同缩小主 Beam。药水早探索可以暂存结果，但资格依赖无药基线时仍须等待正式比较。
- 相同输入与固定工作使用确定性轮转；先不引入收益预测器/强化学习或再次打开 E3B。
- 驻留成员遵守现有内存压力政策，测 paused session 内存；不得为“轮转更公平”让峰值失控。

验收：后置家族首次工作提前，达到参考质量更早；最终质量不退化，修复/轮转/物化总工作不超预算。没有收益就保持当前实现，不以架构更漂亮作为上线理由。

### P4：最后处理模拟热点和严格复用

只有 P0–P3 的 current-HEAD 分项数据证明收益空间后才做。优先现有 Fork/COW、指纹、Hook 枚举、排序/分配热区；每项单独提交、固定输入比结果与工作。

首版不做跨根 transposition table、整棵搜索树 re-root、经验性动作可交换剪枝、额外 DFS 或整套 MCTS 替换。旧节点的累计战损、药水成本、历史、策略、根归一化评分及 RNG 可能不再成立，搬树并非只改根指针。

若将来做严格转移缓存：必须证明完整输入状态、动作/Choice、模拟版本和策略相关上下文等价，缓存只含可安全实例化的不可变输出；先限单请求，再评估跨根。未知 Hook 视为不满足等价。不能为省模拟省掉语义字段或碰撞验证。

## 6. Runtime 状态机与并发约束

```text
StableRoot → ExactReuse? → Repairing → Searching → CandidateReady
                     候选采用时再次核对 epoch ──→ Executing → Settling
Settling → 后态精确匹配：继续当前前缀
Settling → 新稳定根/新回合：回 StableRoot
任意计算 → 新 revision：结果只能留种，不能直接授权
Unsupported / 无稳定根 → 等待或安全停止，绝不强行续打
```

一个战斗会话最多一个主搜索管线；修复是同一管线的有界阶段，不能每个网络事件各起一个后台任务。最新稳定根覆盖待处理旧根；取消后等待安全释放，避免旧任务回调覆盖新结果。不要通过加长 debounce 隐藏重复工作。

动作已提交但后态尚未稳定时，不替换执行中的事务、不重发动作。候选发布只影响下一次尚未授权的动作。退出战斗、换角色/策略、取消和异常时清理 session/种子；所有 Dispose 路径验证一次，不引入无限常驻模拟器。

## 7. 验证与上线准入

纯文档更新只做路径检查与 `git diff --check`。源码实现按下列用例选择受影响的最小集合，默认一次不超过 10 项，扩大范围先按用户要求取得批准。

1. 默认本地核心模式候选保留可达；四项实验预测能力不被启用。
2. 原精确 continuation 命中路径保持原行为。
3. 同回合仅存活目标耐久变化：重放生成新结果，旧授权不可直接复用。
4. 新回合不精确匹配但旧建议有效：修复成功，使用实际新根分数/后态。
5. 目标死亡、牌实例消失或 RNG/Choice 不匹配：停止旧建议，无虚假续接。
6. 药水资格/槽位变化：不跳过 Smart 基线，不重复消费；新支持类型单独验证。
7. 新 revision 在修复、物化、发布时到达：旧回调不能覆盖/部署新 epoch。
8. 取消/退出释放所有 simulator，种子不保留 live 引用。
9. 固定工作 A/B：合法性、质量、动作身份与工作量差异解释充分。
10. 真实 Host/Client：跨连续本地回合比较重算次数、首动作等待、后态校验与累计工作。

延伸现有 `tools/MultiplayerLocalCrossTurnChecks`、`tools/MultiplayerSafeExecuteChecks`、`tools/E0PinnedHarness` 和现有 runtime fixture；先定位各入口能否单项运行，不一键全矩阵。GUI 由用户操作，Agent 负责技术准备和证据核对。

性能验收预先固定基线 commit、输入、配置和预算。固定工作用于判断语义/调度，墙钟用于体验；建议同输入少量配对重复，报告每次值及中位数，样本少时不声称统计显著或可靠 P95。所有样本最终质量不得出现未解释退化；至少目标坏例的 `time_to_reference_quality` 或跨回合累计等待有可重复改善，且峰值内存不突破现有边界。具体毫秒目标在 P0 采样后确定，不虚构收益百分比。

若只降低 cold_search 次数却增加累计 replay 工作/等待，判定目标未达成；若只是更快执行较差路线，判定质量未达成。精确优化失败回滚该项，启发式优化不合格保持关闭。

## 8. 交给 GPT 的执行约束

每次只实施一个阶段，提交内容围绕一个边界。推荐提交拆分：P0 模式门控与诊断；P1 值型种子和新根重放；P2 incumbent 与采用链；P3 经数据证明的调度；P4 独立热点。不要一次改评分、搜索、缓存与执行器。

可直接复制的首个任务：

> 在当前 HEAD 核对本任务书 P0。修复 MultiplayerSinglePlayerCore 未保留 MultiplayerReplayCandidates 的门控缺口，追踪最终物化和 Runtime refresh 调用链，沿用既有候选数量、动作限制与安全执行。不要开启任何队友预测/团队排序，不放松 continuation 状态比较，不修改预算和 UI。扩展现有合同，只运行受影响的最小验证。报告生产默认是否真实进入 bounded refresh、仍有哪些拒绝原因、测试证据与限制。仅在完成该边界后提交并推送当前分支；不要顺手实施 P1–P4。

完成后更新本文阶段状态并链接实际证据，不复制大段日志；只有现状实质变化才更新 handoff。测试临时产物放项目 `.local/`，仅清理本任务产物；不扫描历史目录、不部署游戏、不发布版本。每次汇报写清改了什么、验证了什么、未验证什么。
