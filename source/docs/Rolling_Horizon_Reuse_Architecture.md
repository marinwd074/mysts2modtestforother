# 当前回合快速决策与持续搜索复用架构

日期：2026-09-27。初次核对基线：`7dd49ba6a26d7f5bfbf4b286f853c1580677f023`；提交前已接入并定向核对远端新增的 `d745b0018e27f2013f7df580bde99523f1ec12ed`。仓库：[CombatSolver](https://github.com/xwr20070408-cloud/CombatSolver)。

性质：面向 GPT 的设计与实施任务书，**尚未实现，也没有性能实测结论**。本次只修改文档。本文负责滚动时域与跨请求复用设计；当前生产事实仍以源码为准，既有质量问题继续由 [质量优先计划](CombatSolver_Quality_First_Next.md) 管理，不复制历史阶段状态。

## 1. 推荐方案

采用 **滚动时域决策 + 延迟影响闭合 + 战斗级持久搜索图 + 新根增量修复 + 后台持续推演**。

前台尽快给出当前回合可采用的路线，评估至少覆盖下一回合本地行动及其后的敌方结算；后台继续拓宽候选、解决延迟副作用、预计算可能出现的新根。真实状态变化时先重新定根、验证和修复，只重算受到影响的部分。未来路线是有条件预测，逐动作执行仍由现有 Safe Execute 校验。

这比“每次搜完整场再执行”更适合队友不断改变局面的多人战斗，也比“只算当前回合伤害”更能避免透支未来。不能保证所有目标同时最优：未知队友行为、指数分支和未建模语义不会因内存增加而消失。可追求的是**更早获得可用决策、保持既有质量保障、尽量保存已经付出的计算**。

这里“算完当前回合”指已形成并通过适用质量门槛的当前回合动作建议，不等于穷尽所有可能路线或证明全局最优。初版沿用生产采用门槛；允许有限时域非胜利路线进入正式采用，是单独行为变更，必须经质量对照和明确批准。

## 2. 现有实现与真正缺口

| 已核对入口 | 当前事实 | 本方案如何接续 |
|---|---|---|
| `src/Search/CombatBeamSolver.CrossTurnPlanning.cs` | 已有跨回合 probe、stand-pat 基线与语义变化保留 | 扩展为延迟影响闭合，不重写为纯单回合搜索 |
| `src/Search/CombatBeamSolver.Transpositions.cs` | StateKey 之外保留药水、累计战损、团队状态等 Pareto 标签 | 将状态、路径标签、评估上下文分开，不能只缓存一个最高分 |
| `src/Search/StateFingerprint.cs`、`CombatBeamSolver.StateEvaluation.cs` | 已有双 64 位状态指纹及牌/牌堆指纹复用 | 审计覆盖后复用；哈希命中还需语义相等校验 |
| `src/Runtime/SolverController.MultiplayerPlanRefresh.cs` | 有限候选重放：3 候选、2 动作前缀、192 节点、60ms 等现行边界 | 当前刷新可保留；长期图修复不能冒充已存在能力 |
| `src/Search/CombatSearchCoordinator.cs` | 已接入 continuation seed 的独立 incumbent probe | 先扩大成果存活范围，保留新根重放与准入边界 |
| `src/Runtime/SolvedRouteCache.cs` | 已有 value-only 磁盘路线缓存、版本/策略身份与容量上限 | 是路线缓存，不是可复用全部搜索边的持久图 |
| `src/Search/SolverSearchProfile.cs` | 统一 Beam、节点、分支和时间配置 | 增加内部调度职责，保持现有预算与并发保障 |

当前交接还区分默认 local-single-core 与实验队友预测栈。本方案不能借优化之名默认开启实验栈，也不能绕过多人专属牌、药水和部署权限限制。默认模式也能从本地副作用评估与新根复用中获益；队友情景预计算仅在该模式已获授权时运行。

远端新增提交已为 local-core 跨回合续用加入条件性“敌人仍存活且只掉血”兼容分支，由斩杀重算设置/窗口控制，入口在 `SolverController.SearchLifecycle.cs` 与 `MultiplayerLocalCrossTurnContracts.cs`。这属于已有策略性容忍，不是完整语义相等；本方案 R0 精确缓存不能直接把该兼容判断当命中证明。后续 A/D 阶段应保留其独立分类并验证阈值变化的决策质量，不重复实现，也不在本次文档任务修改行为。

缺口不是简单“搜索太浅”，而是：搜索成果主要依附一次请求；真实状态改变后可复用证据较少；短期决策与长程探索尚需解耦；跨时域候选缺少统一的延迟负债与可比性合同。

## 3. 看多少回合：按事件闭合，而非固定截断

网上上游复核后的约束：两回合只是优先完成的比较窗口，**没有证据证明它是《杀戮尖塔 2》的最佳固定视野**。先保留上游不限固定回合数的求解能力，只改变结果发布和增量工作的安排；窗口内非终局候选的正式采用须单独验证。详见第10节。

### 3.1 三种长度分开

| 长度 | 建议起点 | 作用 |
|---|---|---|
| 执行授权长度 | 每次提交一个本地动作，后态核验后再继续 | 防止队友变化使旧动作失效 |
| 前台详细评估窗口 | 当前本地回合 T、下一本地回合 T+1，结算到 T+2 本地行动开始前 | 覆盖当下收益与下一回合的真实代价 |
| 后台推演范围 | 优先 T+2/T+3 的关键事件，必要时延长或到终局 | 处理慢收益、晚触发负债，并为之后观察预热 |

以上是待验证的实验起点，不是新的生产回合上限。不得直接删除现有长线候选或削减原节点/时间预算。前台窗口完成后继续使用原搜索能力；后台范围按价值调度，可比通常窗口更远。

多人“结束回合”须指明本地 EndTurn、队友完成、敌方行动、下一回合触发分别何时发生；本地 EndTurn 不是可直接越过其他玩家的 live 授权。

### 3.2 延迟影响账本

为当前动作造成的未来义务建立 `DeferredImpact` 元数据：来源动作/效果、触发事件、最早/最晚触发边界、受影响资源、状态依赖、结算状态、估计区间及证据等级。该账本引用模拟状态里的真实效果，**不另造一套卡牌规则或再次扣血**。

重点覆盖：下一回合少能量/少抽牌、临时力量回收、易伤/虚弱持续时间、延迟自伤、牌堆污染、消耗关键牌、保留/抽牌顺序变化，以及持续能力的后续收益。

候选到达窗口末端后：

1. 由精确模拟确认已结算的影响退出账本。
2. 未结算但可能改变当前选择的影响，优先延长到相应事件并计算后果。
3. 无法继续的影响保留区间或 Unknown，不得按零代价处理。
4. 不只检查负效应，慢收益也要保留，否则会系统性拒绝铺垫牌。
5. 牌堆污染等跨很多回合的影响不能要求永远“全部结清”；用尾部估值、区间与定向 rollout 表示残余影响，明确不是精确终局。

示例（合成语义，不声称对应某张实际卡）：A 当前多打 12 伤害，但下一回合少 2 能量，导致多承受 18 伤害；B 当前少打 12 伤害，下一回合能完成防御。只按当前伤害排序会偏向 A，覆盖下一行动与敌方结算才可能正确识别 B。敌人已在当前回合全部死亡时，不再扣不存在的战斗内下一回合成本；战后仍持续的成本按原长期策略处理。

### 3.3 可比较的评分

对同一观察、同一情景与同一结算边界，记录：

`Outcome_H = (存活, 累计战损, 剩余HP, 敌人状态, 药水/资源成本, 已实现长期收益, 未结算影响, Coverage)`。

解释性标量可写成 `J_H = Σ已模拟成本 + V_tail(s_H, 未结算影响)`，但生产排序优先沿用既有目标向量及资源政策，不把所有指标随意加权。下一回合确定的负债不因折扣而缩小；不使用很小的 gamma 掩盖后续损失。尾部项只描述窗口外部分，禁止与已模拟伤害/效果重复计数。

不同深度的候选先补齐共同的关键事件边界再比较；不能拿 A 的一回合战损与 B 的三回合战损直接排序。未完成、未知效果、搜索耗尽、失败和真实死亡必须分别表示。

若成本区间有严格依据，满足 `U(best) < min L(other)` 时可证明在当前已覆盖候选/情景范围内胜出；启发式区间不提供该证明。Beam 已剪掉的路线不在证明范围内。连续多轮第一动作不变只能作为稳定性信号，不能写成最优性证书。

### 3.4 自动延长与停止前台等待

优先延长：尚未结算的高影响负债、可能致死边界、第一动作竞争接近、准备牌收益未显现、抽洗牌或怪物阶段转换。

优先后台计算：不影响近期第一动作的远期细节、依赖多次队友假设的长路线、尚未观察到的低优先级情景。未来分支再多也不能阻塞已通过既有准入的当前建议发布。

前台可以在正式可采用候选出现后结束等待，后台继续优化；若没有合格候选就保持“计算中/边界未解决”，不能为响应速度伪造安全结论。首个合格当前回合结果 1–2 秒、温缓存刷新 100–300ms 可作为实验目标，必须报告 p50/p95 与硬件，不是本次保证。

## 4. 持久搜索图架构

```mermaid
flowchart TD
    A[稳定真实快照与观察版本] --> B[战斗会话与新根绑定]
    B --> C[前台现有 Beam/Portfolio]
    B --> D[后台持续推演调度]
    C <--> E[战斗级搜索图与转移缓存]
    D <--> E
    E --> F[共同窗口评估与延迟影响闭合]
    F --> G[带版本及假设的候选]
    G --> H[现有逐动作 Safe Execute]
    H --> A
    E <--> I[异步冷数据存储]
```

### 4.1 所有权与数据结构

组件名是职责建议，实施时优先扩展同职责现有类。

| 建议职责 | 所属层 | 持有内容 |
|---|---|---|
| `CombatPlanningSession` | Runtime | 战斗身份、根 epoch、当前请求、战斗级缓存生命周期 |
| `SearchGraphStore` | Search | 不可变状态、转移边、反向依赖、各上下文 frontier |
| `TransitionMemo` | Search/Engine 边界 | 精确输入及动作到精确输出的确定性结果 |
| `HorizonController` | Search | 共同评估窗口、延迟事件和加深优先级 |
| `DeferredImpact` | Prediction/Search | 从真实模拟效果导出的残余影响及证据 |
| `IncrementalReconciler` | Runtime 编排、Search 修复 | 新根匹配、重放、脏评估传播与候选再选 |
| `PonderScheduler` | Search | 前台/后台任务、去重、取消及结果发布 |
| 冷存储适配 | Runtime 基础设施 | 纯值编码、索引、校验、版本和异步 I/O |

状态节点不持有 live Godot/游戏对象；不可变页和 COW 分支通过现有 Fork/remap 合同隔离。Worker 的临时可写模拟器私有，缓存只发布完成后的纯值结果。现有不能可靠序列化的模拟内容先不进入磁盘层。

图中允许回路，不能假设是 DAG。重复语义状态仍可能有不同累计损失、动作数、目标额度和历史；路径标签保持 Pareto 多样性。零成本循环使用既有进展/循环语义，禁止靠“访问过一次”直接剪掉所有回访。

### 4.2 四类键

1. `DynamicsKey`：未来转移所需完整语义，包括本地状态、可建模远端公开状态、敌人/意图、牌实例与牌堆顺序、RNG 流位置、回合/阶段、持续效果、触发器与相关历史。纳入游戏/Mod/模拟语义版本和知识边界。观察不到的内容标未知，不能编造精确键。
2. `TransitionKey`：DynamicsKey + 动作实例/目标/Choice + 环境动作/事件顺序/随机结果；环境转移不是只有本地出牌。涉及隐藏随机性时存条件分支或分布及模型版本，不能存成一个必然结果。
3. `EvaluationKey`：DynamicsKey + 目标/策略版本 + 情景集合 + 时域/覆盖 + 根归一化参数 + 路径标签。累计战损、已用药水及额度等不能因 re-root 归零；能与后缀成本安全拆分时才独立复用。
4. `ObservationEpoch`：控制部署新鲜度、异步任务归属，与缓存语义等价键分开。

指纹只用于索引，命中后核对规范化语义内容。不能使用对象地址或进程内随机 hash 作为跨运行身份。缓存精确转移不代表旧评分、旧根授权或“已搜索最优”可直接复用。

### 4.3 按安全强度分层复用

| 级别 | 条件 | 可复用内容 | 必须重做 |
|---|---|---|---|
| R0 精确状态 | 版本、完整语义、动作条件均一致 | 已验证转移、可恢复 frontier、后缀候选 | 根相关标签/目标重新绑定，部署新鲜度 |
| R1 新根路线重放 | 状态不同，旧动作可能仍有价值 | 动作序列/排序提示 | 从新根模拟、共同窗口评价、质量与部署准入 |
| R2 依赖证明 | 改变字段与纯函数完整依赖闭包无交集 | 局部纯计算结果 | 受影响结果与所有反向祖先评价 |
| R3 近似相似 | 只有相似牌组/局面 | 搜索顺序、宏动作提案、尾部估计先验 | 全部语义与合法性验证，不作剪枝或执行证明 |

默认先落地 R0/R1。R2 要覆盖间接 Hook、阈值、RNG 和事件次序依赖；“只变了一点敌人 HP”不足以证明无影响，可能刚好跨越斩杀阈值。不能将旧状态的 delta 简单贴到新状态上。

新真实状态在图中不存在时，为它建立新根；其他状态节点继续作为内容寻址缓存存在，不把不相等的旧根强行改名为新根。整树 re-root 是精确匹配的一种优化，不是所有队友变化的通解。

### 4.4 跨请求恢复与增量回传

搜索暂停点保存未展开 frontier、已枚举动作游标、所属 portfolio 成员/策略、深度和路径标签。`FullyExpanded` 必须绑定动作生成器版本、分支覆盖和环境情景；被 Beam 丢弃不等于已证明无价值。

新根到达后先精确查找，再重放已有少量优质候选，最后将未覆盖部分交给正常搜索。状态转移缓存共享，不同目标/情景的分数和完成标志隔离。改变子节点评估时，通过反向边标记祖先结果 stale，按需重新回传；循环区域使用明确的有限时域或现有循环语义，不能套用无环递归。

已有 frontier 若预算耗尽，记录暂停原因与完成程度；同一根可继续扩展，不能把“上次 Beam 没留下”当成永久闭合证明。

## 5. 队友不确定性与持续预测

不要押注一条长路线。保存少量可解释的条件策略：本地第一动作/短前缀相同，未来在**可观察分歧**之后分支，例如敌人是否被击杀、队友是否施加易伤、资源是否改变。相同观察历史下必须选相同动作，禁止每个未来情景偷看答案后选择不同第一动作。

默认本地核心可先预热预测中的本地后态和下一回合根；实验队友模型开启时，再预热名义配合、暂时不行动、目标提前死亡、关键增减益等情景。情景权重没有实测校准时只是压力覆盖，不称胜率或概率期望。保留现有 Robust 作为对照，不在缓存改造时顺便换风险偏好。

后台任务优先级可用“预计能改变当前选择的程度 × 可能被复用的程度 / 预计代价”排序；缺少概率时用序数等级，避免虚假精度。优先顺序：当前根关键竞争与负债 → 近期可能的新根 → 次优当前动作备选 → 更远期探索。后台也应拓宽，不是只把赢家一路延长。

并发实现使用现有 worker 能力，并在 detached 状态上运行；保留前台既有容量，后台消费额外可用资源，不缩减当前预算/并发。共享转移使用 single-flight 去重；不同 portfolio 的质量标签独立。任务按观察 epoch 发布：旧 worker 可以完成并写入语义合法缓存，但不能覆盖新根结果或恢复过期执行会话。

前台取消与战斗缓存销毁分开：新观察取消旧根发布资格，仍保留已完成纯值成果；用户停止、战斗退出、版本切换各自有明确生命周期。计划更新不得改变已经在途的原生动作，等待稳定后再接续。

## 6. 内存和 SSD 怎么用

资源宽裕时优先保留**可复用状态和转移**，而非无限保留完整对象树。

| 层 | 内容 | 策略 |
|---|---|---|
| 热 RAM | 当前根、近期后态、竞争路线、frontier | 不可变紧凑页、COW、分片索引；先测量再决定布局 |
| 温 RAM | 本场其余精确节点、已完成转移、评估标签 | 状态去重、共享牌堆/效果页，避免每节点完整克隆 |
| 冷 SSD | 可序列化的精确转移、状态页、版本化检查点 | 内容寻址块 + 索引、后台批写、校验和、原子完成标记 |
| 长期先验 | 常见动作组合、离线尾部估值样本 | 只引导候选，跨版本失效，不冒充精确状态命中 |

存储技术在测得对象大小、命中率和 I/O 后再选；首版可使用分段块文件与简单索引，不先引入数据库/GPU依赖。磁盘读写不处于逐动作提交的同步关键路径，主搜索不等待冷盘才开始找解。损坏/版本不符条目隔离并计数，按照明确设计作为缓存未命中，不改变求解语义。

即便愿意无限给内存，物理 RAM、带宽、GC、地址空间和磁盘仍有限，保留现有保护。容量估算：若平均状态+边+索引摊销 2KiB，1000 万项约 19GiB、1 亿项约 191GiB；这只是量级例子，需实测共享率和对象开销。禁止直接无界 `Dictionary<State, FullSimulator>`。

冷数据按版本与战斗分区；生命周期配置只管理本项目自己创建的缓存，不清理存档、游戏或其他缓存。内存/SSD失败不得导致错误建议；尚未缓存的数据仍可由原模拟器计算。大规模长期数据是否值得保留，取决于跨根实际命中和节省的模拟时间，而不是缓存体积。

GPU、学习型价值网络和 MCTS 可作为以后独立实验：现有 C# 规则/对象模拟不能直接搬到 GPU；学习器初期只排序和估计尾部，不作安全证明；MCTS 不是前置条件，先让现有 Beam 具备复用能力。不要同时换引擎、搜索器、目标函数和执行器。

## 7. GPT 分阶段执行卡

每次一个阶段、一个主要语义边界。阶段名不是必须创建的类。实施前核对当前源码与已有结果，禁止重复施工。当前任务只批准生成此文档；下列代码变更按后续明确任务推进，用户可见行为单独确认。

| 阶段 | GPT 要完成的修改 | 最小验收 | 依赖/回滚 |
|---|---|---|---|
| A 现状与测量 | 沿当前调用链确认候选发布时间、重搜原因、已有复用；对照第10节固定上游源码，区分已有能力、可移植改进及本 fork 独有边界 | 同一输入结果不变；区分冷搜、精确续用、重放 | 起点；仅诊断可独立撤回 |
| B 延迟影响合同 | 先对照上游 CrossTurnProbe、PowerCommitment，再扩展缺失的负债/覆盖元数据；核实已模拟效果而非重复扣费 | 延迟负效应、慢收益、当回合斩杀三类不双算、不漏算 | 先旁路对照，排序迁移独立提交 |
| C 战斗级 R0 转移缓存 | 审计键/所有权，抽取纯值转移结果；先只跨同战斗请求复用 | 开/关缓存逐状态和结果一致；RNG/Choice/版本变化不得假命中 | B 可并行概念设计，实施串行；开关可禁用 |
| D 新根恢复 R1 | 保留 frontier/候选，从真实新根精确接入或重放，回传受影响评价 | 精确命中、轻微 drift、斩杀阈值变化各走正确分支 | C；失败只撤缓存采用，原求解仍可用 |
| E 前台/后台分离 | 先发布已获准当前建议，后台恢复探索；引入时域/负债调度 | 旧 epoch 不发布、不部署；前台质量门槛不降低 | B–D；保留原完整搜索对照 |
| F 情景预热 | 在已启用实验栈内预计算近期可观察分支，保证同观察同动作 | 队友改目标/暂不行动后正确接入或重算 | E；不默认开启实验多人算法 |
| G SSD 冷存储 | 纯值编码、版本命名空间、异步写入/预取、校验与恢复 | 往返语义一致、损坏/旧版本不误命中、热路径无同步 I/O | C/D 成熟后；不让磁盘成为必要依赖 |
| H 质量与响应验收 | 固定输入 A/B，再真实 Host/Client 验证；记录收益及未达项 | 质量不被已知对照支配，响应/复用改善可重现 | 前面各阶段；不以合同替代实机 |

执行状态（2026-09-27）：

- **A 已完成**：沿现有发布/continuation/refresh 调用链完成现状测量，没有另造重复计时或复用层。
- **B 已完成**：`DeferredImpactCoverage` / `DeferredImpactOutcome` 只记录真实模拟已到达的覆盖边界和绝对结果，不参与生产排序。pinned 0.107.1 定向验收覆盖真实延迟负效应（Biased Cognition Focus `4 → 3`）、慢收益（Outmaneuver 下一回合 Energy `3 → 5`）以及当前回合斩杀（`CombatTerminal`，无未来攻击债务）；Release 与 harness 均 0 warning / 0 error。Borrowed Time 经固定上游/0.107.1 语义核对属于本回合费用修正，不作为跨回合负债样本。
- **C 已完成**：战斗级 R0 exact/normalized shadow 已通过同战斗跨请求与真实 fresh re-root 证据；非终局真实 hydration 仍保持关闭，避免把 simulator-free value snapshot 误当可展开节点。
- **D 已完成**：生产启用 R1 新根路线重放。完整合法胜利可建立安全 incumbent bound；未胜利但仍存活的 probe 结果可抽取新根已验证的当前回合普通牌前缀，仅用于 baseline Beam 枚举顺序提示。THE_OBSCURA NORMAL 实机已证明 request-tail exact hydration 可真实命中，同时坏 key 仍按 D3.3B 独立 fail-closed；原 D3.5 retained frontier/subtree 因 exact subset 无交集保持暂停，作为后续研究项而不阻塞主线。
- **E1 已完成**：INFESTED_PRISMS ELITE `f86f0df9c9ff478ab621f943a97e77bf` 来自 `0.40.2+a2a8cd26`，实机证明正式排序 preview 可在无执行 seed 时提前前台发布，generation 1 首次发布后后台继续真实工作约 115 秒，并从 candidate version 1 升级到 59；generation 2 覆盖执行授权 false/true 的分离与恢复，未见旧 generation 越界发布。
- **E2 已完成**：INFESTED_PRISMS ELITE `604411ffffe44ca388ece05d9eb8e594` 来自 `0.40.2+ddd43a3c`。实机出现真实 `replace` 后新 foreground version 发布；generation 3 连续两个更高战损候选均 `keep`，第二次仍引用原 `previous_version=5` 且该 generation 无 foreground publish，证明劣化后台候选不会抖掉当前建议。
- **E3 进行中（后台有效率测量）**：不改变搜索行为。E2 正式决策补充 member identity 与 request-relative 时间；请求结束记录 `SEARCH_E3_BACKGROUND_VALUE`，包含首次 foreground、后台墙钟、最后改善时间、决策计数，以及完全在 foreground 后启动成员的 elapsed/expanded/transitions 保守下界。该证据用于决定后续是否调整时域或 portfolio 优先级，不以猜测直接削减长搜。
- **E3 前置质量修复已实机通过**：KAISER_CRAB_BOSS `35db6c5d375e4f5d883d122a86acba84` 来自 `0.40.2+5b4c6eb1`。低血窗口 continuation 已 `allow_living_enemy_hp_decrease=false`，本场 `reused=0`；第 3 回合 Crusher 40 HP 时 fresh search 101 节点 / 40ms 直接选择 PERFECTED_STRIKE 斩杀。
- **E3 测量补全已发现 completion 质量倒退**：SCROLLS_OF_BITING_WEAK `5d0ac4d9b96b43c9bd4b7ffea8f79ed2` 来自 `0.40.2+df248c23`。generation 1 的最后 foreground 为 19 战损 / strategic deficit 17 / enemy HP 68，但 39.75s 后自然 final 为 39 战损 / deficit 23 / victory，并被实际捕获执行；`SEARCH_E3_FINAL_VS_FOREGROUND relation=foreground_better` 已直接证明自然完成边界绕过 E2 质量保护。
- **E3 completion guard 待实机确认**：MultiplayerSinglePlayerCore 在 SearchCompletion 时复用 E2 的 `CanPromoteDisplayedResult`；若 foreground 严格更优，则先精确 materialize 对应 RouteAdoptionSeed，再次比较，仍更优才返回该物化结果。该修复不改变 Beam、portfolio 或后台预算，只阻止更差 final 覆盖已证明更好的 foreground。
- **D3.1 shadow 已落地**：R1 probe 与 baseline 对 retained-state evaluation 做 request-local exact/path-aware 对照，固定上限 256 entries / 4096 observations；当前 `behavioral_reuse=false`，不跳过任何模拟或评价。D3.2 仅在真实 fresh re-root 证明 hit>0 且 conflict/mismatch 均为 0 后启用。

- **D3.2 已落地**：只对 Beam retention 的纯 `BeamRankScore` 做 request-local 首验后复用。第一次 baseline 命中仍现算并精确比对；同 key 后续调用才复用。任一冲突或 mismatch 立即关闭该请求的复用。真实 replay、state capture/fingerprint、候选集合和搜索预算均保持原路径。

- **D3.3 已落地**：R1 probe 最多保留 32 个 request-local exact 非终局普通 PlayCard 后态。baseline 首次相同 parent/action 仍真实 replay 并核对完整输出语义，后续重复 key 才可直接 fork 后态并重新 Snapshot/evaluate；冲突或 mismatch fail-closed。缓存只活到 baseline 结束，不进入 refinement/supplemental，不改变 Beam/节点/时间预算。
- **D3.4 shadow 已落地**：对 R1 probe 与 baseline 的 plain `PruneFinal` retained frontier 做 exact ordered 语义签名对照；不恢复 SearchNode、不跳过展开、不影响 Beam。签名严格包含路径与父链 retention 语义，复杂 Cycle/CrossTurn/OrderedMutation frontier 直接跳过。真实 fresh re-root 出现 exact frontier hit 后，D3.5 才允许尝试恢复 frontier 来减少 expanded nodes。
- **D3.4B 已落地**：对 exact frontier key miss 做 turn / actionCount / nodeCount 三层互斥分类，并记录最近 R1 probe key；仍为 `behavioral_reuse=false`。只有分类证据证明 frontier 差异可安全收敛后才设计 D3.5，禁止为了命中率直接放宽等价性。

- **D3.4C 已落地**：整 frontier key 不重合后，改测同 `turn + actionCount` 的 exact retained-node 集合交集；记录 R1 是否为 baseline 的稳定子集。当前仍 `behavioral_reuse=false`，不恢复节点、不减少展开。D3.5 的候选设计由该证据决定，优先考虑 exact 子集/子树恢复，而非整 frontier 替换。
- **D3.4C 实机结论更新**：最新完整 fresh re-root 样本在同一 `turn=3/actionCount=4` 下为 R1 4 nodes、baseline 60 nodes、intersection 0。原 D3.5“恢复 retained frontier/subtree”暂停，不再作为主线下一步；D3.3B/C 已由后续实机证据闭合，主线已进入 E1。
- **D3.3C 已落地并实机闭合**：ENTOMANCER 首轮证据曾为 `validated_keys=2 / hydration_hits=0`，因此把已验证 cache 延长到 request tail。随后 THE_OBSCURA NORMAL `c20503100021438b8502afb9582cd611` 的第一次 fresh re-root 得到 `validated_keys=1 / hydration_hits=1 / output_mismatches=0 / rejected_keys=0`；同包第二次对一个 `output_state_key` mismatch 正确按 key 拒绝且未关闭整个请求。阶段 D 因而不再等待更多 hydration 命中证据。
- **D3.4C admission 修正**：R1 seed 不再继承 exact continuation 的完整 enemy-roster CombatIdentity 门禁。仅允许同一战斗 seed/玩家集合下 enemy roster 由 expected 缩成 actual 子集；这是为了覆盖队友提前击杀导致的 fresh re-root。Exact continuation 仍保持完整 identity 严格一致，R1 仍必须从新 live root 逐动作 replay，失效动作自然截断/拒绝。
- **D3.4C blocker 修复**：实机 R1 已能建立 incumbent，但 R0 terminal memo 的 simulator-free terminal snapshot 被 Novelty 当作可读取 simulator 的节点，导致搜索失败。修复只跳过 terminal value-only 节点的 novelty fact capture；nonterminal 缺 simulator 仍 fail-fast。D3.5 在该修复通过实机前继续暂停。

建议实施顺序 A → B → C → D → E → F → G → H；先收获正确的短期评价和同战斗复用，再投入昂贵持久化。R2 依赖闭包复用是 D 后的可选独立阶段，未证明可靠时不影响主线交付。

给 GPT 的通用任务模板：

```text
实施 Rolling_Horizon_Reuse_Architecture.md 的阶段 <X>。
先核对当前 HEAD、适用规则及该阶段直接调用链，复用已完成能力。
本轮只处理 <边界>；保留现有预算、并发、质量与执行保护。
列出状态所有权、键/失效条件及最小验证，再修改。
每次测试默认不超过10项；扩大范围先征得同意。
不使用子智能体；不启动游戏GUI代用户操作。
完成后报告：改变行为、未覆盖边界、实际验证、提交与推送状态。
临时产物仅放项目 .local；只清理本任务创建的内容。
```

GPT 负责代码、精确合同、构建与允许的 pinned 回放、诊断分析、提交推送。用户负责目标冲突的产品决策，以及真实多人窗口中的建房、出牌和观察。需要实机时 GPT 先完成技术准备，每次只请求用户执行当前一步；不把部署和日志分析反交用户。

## 8. 验收设计与成功标准

以下是后续阶段的测试设计，**本次未执行**。每个测试批次最多 10 项，按改变边界选最小子集；不能把多个大矩阵包装为一个测试规避限制。

推荐一个最多 8 场的固定质量集：普通攻防、下一回合资源负债、慢收益准备、抽洗牌/Choice、当前斩杀免除后债、队友击杀目标、队友增减益改变价值、状态近似但 RNG 不同。语义场景须基于实际支持效果构造，不能只用合成评分函数证明真实质量。

缓存正确性另按阶段选窄合同：完整键碰撞校验、Fork 隔离、过期 worker、路径标签不丢失、磁盘往返/损坏；超过本轮10项先申请。源代码变化才构建/跑对应检查，纯文档只查链接与 diff。

| 指标 | 记录方式与判定 |
|---|---|
| 首次可用时间 | 稳定根捕获 → 通过适用准入且成功发布，不能用未经评价的首候选冒充 |
| 刷新时间 | 新根稳定 → 新 epoch 合格路线，报告 p50/p95 与分原因样本数 |
| 精确复用率 | 经语义校验的缓存命中 / 可缓存转移请求；重放提示与近似命中另记 |
| 节省工作量 | A/B 的实际模拟转移差及耗时；不能只报总命中数 |
| 后台有效率 | 被后续新根采用的后台成果 / 后台工作，同时记录无用推演成本 |
| 决策质量 | 存活、累计损失、最终HP、回合、资源逐维比较，不用单一分数掩盖退化 |
| 预测正确性 | 同输入 actual/simulated、缓存开关逐状态差分；未知独立标记 |
| 抖动与陈旧 | 同根推荐更换次数、过期发布/误执行必须为0 |
| 内存与磁盘 | 峰值、GC停顿、字节/唯一状态、I/O等待、跨根实际命中 |

做两类 A/B：固定工作量看算法质量，固定墙钟看用户响应。不得通过减少节点、Beam、情景、并发或检查取得“加速”；记录总工作与前后台分摊。缓存开关对照固定调度和输入时应语义一致；并发调度变动引起的路线差异必须能解释，且做同目标质量比较。

不要承诺绝对命中率或固定倍数。若先验命中低，先分析根差异和键覆盖，不能放松等价性条件；若深搜没有改善当前选择，调整后台优先级，不削减生产保障。效果不足则保持实验状态，不宣布目标完成。

## 9. 理论依据及适用边界

- [D* Lite，Koenig 与 Likhachev，2002](https://www.cs.cmu.edu/afs/cs/Web/People/motionplanning/papers/sbp_papers/integrated3/koenig_dstarlite_aaai02b.pdf)：增量规划复用先前计算。本文借鉴“变化后修复受影响部分”，但战斗是带随机、路径目标和动态分支的图，不能直接套其最短路保证。
- [Monte Carlo Tree Search in the Presence of Transition Uncertainty，AAAI 2024](https://ojs.aaai.org/index.php/AAAI/article/view/29994)：讨论转移模型不确定性。用于提醒区分模拟器确定性与队友环境不确定性，不构成迁移 MCTS 的性能证据。
- [A Local Monte Carlo Tree Search Approach in Deterministic Planning，AAAI 2011](https://ojs.aaai.org/index.php/AAAI/article/view/8039)：作为后续局部探索研究入口，不能替代本项目 Beam 基线对照。

本文具体窗口、数据结构、阶段划分与时延目标是针对当前仓库的工程建议，不是上述论文对本游戏的结论。最优先落地的是 **延迟负债不能漏算、精确成果跨请求保留、真实新根重新授权**；磁盘和更多计算资源用于扩大可靠成果复用。

## 10. 官方游戏与 CombatSolver 上游网上复核

核对日期：2026-09-27。本地基线为 `eb70db3`；通过 GitHub API 将本次上游读取固定为 [`d231e9e51a0e58d6bfa1373c6265cce13ffd9a45`](https://github.com/Torch1230/CombatSolver/tree/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45)。这是定向源码核对，不是上游全仓审计或性能验证。此前版本已检查本地实现，但未直接检查网上上游源码；本节补足该证据边界。

### 10.1 游戏、来源和版本边界

- 游戏是 Mega Crit 的《杀戮尖塔 2》。官方商店明确支持最多4人合作、多人专属牌与团队配合；这支持“队友变化是预测环境的一部分”，但不证明具体事件次序、共享 RNG 或某张牌的固定版本语义。[官方 Steam 页面](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/)
- 本 fork 的直接上游是 Torch1230/CombatSolver；其公开说明定位为单人求解器，使用预算约束的 Beam Search，已有跨回合预测与路线复用，没有固定回合数/洗牌次数上限。公开 README 当次列出的目标是 STS2 `0.111.0`，本 fork 固定 `0.107.1`，不能以相同 Mod 版本号判断兼容。[上游说明](https://github.com/Torch1230/CombatSolver/tree/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45#readme)
- 模拟底座还使用并改造了 Random Foreseer 的部分实现。应区分“模拟引擎来源”和“CombatSolver 的搜索/路线执行扩展”；不能把整个算法统一归为 MCTS 或从零开发。来源、许可仍由现有 [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md) 管理。

### 10.2 可直接指向代码的核对结果

以下链接均固定同一上游提交；表中“缺口”只针对本次读取范围，不把未找到等同于全仓不存在。

| 上游一手源码 | 核对到的机制 | 对本方案的修正或限制 |
|---|---|---|
| [CombatBeamSolver.Phases.cs](https://github.com/Torch1230/CombatSolver/blob/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45/src/Search/CombatBeamSolver.Phases.cs) | `SolveCore` 明确拒绝 `root.PlayerCount != 1`，使用独立根的求解生命周期 | 多人同步与新根复用不能直接套上游单人合同 |
| [CombatSearchCoordinator.cs](https://github.com/Torch1230/CombatSolver/blob/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45/src/Search/CombatSearchCoordinator.cs) | Beam portfolio、可选 Novelty、成员准入与剩余工作预算编排 | 保留主引擎；先测哪个成员产出好候选，不新增另一套主搜索器 |
| [SolverSearchProfile.cs](https://github.com/Torch1230/CombatSolver/blob/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45/src/Search/SolverSearchProfile.cs) | 含默认启用的 `ReallocatedRefinementPortfolio`、`StopPortfolioAtHpTarget`，以及标注离线实验的选项 | 属候选移植内容；不能将实验字段全部视为生产能力，不能直接导入会改变本 fork 预算分配的默认值 |
| [CrossTurnPlanning.cs](https://github.com/Torch1230/CombatSolver/blob/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45/src/Search/CombatBeamSolver.CrossTurnPlanning.cs) | stand-pat 对照、语义已改变但收益尚未显现的 probe 保留 | 本方案账本须扩展已有证据，不能声称上游只顾当前收益 |
| [PowerCommitmentRetention.cs](https://github.com/Torch1230/CombatSolver/blob/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45/src/Search/PowerCardValuation/Commitments/PowerCommitmentRetention.cs) | 按 commitment 家族、已实现/进展证据、未实现净值等选择保留候选 | B阶段先审计此机制与本地准备牌保留的重叠；它是保路机制，不是所有跨回合负债都已正确计价的证明 |
| [RoundTransition.cs](https://github.com/Torch1230/CombatSolver/blob/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45/src/Search/CombatBeamSolver.RoundTransition.cs) | 下一回合能量消费、EndTurn 重放、回合开始 Choice checkpoint | 下一回合成本可能已由模拟器执行；先查丢失在枚举、保留、边界或最终排序，禁止重复收费 |
| [Transpositions.cs](https://github.com/Torch1230/CombatSolver/blob/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45/src/Search/CombatBeamSolver.Transpositions.cs) | 同状态保留非支配路径标签 | 跨请求缓存必须保留路径目标维度，不能退化为 State→单一分数 |
| [SolvedRouteCache.cs](https://github.com/Torch1230/CombatSolver/blob/d231e9e51a0e58d6bfa1373c6265cce13ffd9a45/src/Runtime/SolvedRouteCache.cs) | 纯值路线落盘、游戏/求解器版本身份、64条容量边界 | SSD路线缓存并非新增概念；本计划新增范围是细粒度转移/frontier复用，不是再建相同路线缓存 |

本地定向路径查询未找到同名 PowerCommitment/PowerValuation 文件，不足以排除本地以不同名称实现等价效果；实施前必须继续按具体责任核对。上游下一回合能量字段也不能单独证明所有负效应都正确模拟。

### 10.3 调整后的开发取舍

1. **优先保留基础算法。** 在现有 Beam/portfolio/模拟器上做成果存活、发布与新根修复，不以通用规划论文替代真实调用链。
2. **先诊断再缩窗口。** 延迟损失缺失可能是模拟缺口、Beam提前剪枝、终局偏好或候选尚未完成评估；各层使用不同修复，不能统一靠增加惩罚项解决。
3. **先评估上游已有改进。** 能力牌 commitment 和 portfolio 调度分别作为独立可移植候选，与本 fork 的多人目标、资源策略和 `0.107.1` 语义对照，不整包合并。
4. **持续搜索复用仍值得实施，但收益待测。** 已有路线续用不等于任意队友变化后的完整搜索图可复用；R0/R1先行、R2依赖证明后置的次序保持。
5. **不硬编码两回合，也不承诺1–2秒。** 保留这些数值作为实验窗口/响应目标；官方与上游没有给出它们最优或必能实现的证据。

后续移植遵循 [UPSTREAM.md](../../UPSTREAM.md)：通用改进、版本绑定语义、fork冲突分别处理。此次没有移植上游代码、改生产默认或进行实机测试；没有下载整仓及创建研究临时文件。
