# Codex 当前交接

> 只记录当前生产状态、当前风险和下一任务。已完成阶段过程从 Git history 恢复，不在本文件维护流水账。

## 基线

- CombatSolver `0.40.2`
- STS2 / RitsuLib pinned `0.107.1`
- .NET 9 / Godot 4.5.1 / `STS2_01071`
- 单人与多人共享生产搜索/模拟核心。
- 多人部署权限只属于本地玩家；不会部署队友动作。
- MultiplayerOnly 牌保留真实牌堆状态与抽牌距离，但不进入主动搜索/自动执行。

## 当前多人架构

1. **共同搜索核心**：单人完整路线与多人本地跨回合共用 Beam、Novelty、成长、遗物、药水和长期收益基础。
2. **团队目标/情景**：Team Objective、Shadow teammate forecast、Scenario Matrix / Robust 和 Carry 统一属于实验多人预测栈。生产默认关闭该栈，使用真实多人战斗根 + local-single-core；仅“设置 → 常规 → 多人模式 → 启用多人预测算法（实验）”一个总开关控制是否启用整套预测栈。
3. **交错预测**：实验预测栈开启时支持 local → forecast-only teammate → local 的 detached 模拟；队友节点没有 deployment authority。U5 reverse-order 等价探针只在详细诊断开启时运行；生产搜索不再为纯日志额外 Fork/重放每条队友路线。队友联合预测与 Robust 情景复评继续作为内部搜索阶段保留，但不再暴露独立用户开关，避免与总开关形成无效/矛盾组合。
4. **路线刷新与跨回合热启动**：轻量 HP/Block drift 可对少量保留候选做 bounded refresh；精确 continuation 仍优先。进入新回合后若精确状态不匹配、但战斗/本地玩家/多人规则身份兼容，默认 local-single-core 会把旧路线当前回合的普通牌建议从真实新根重放成额外 Beam seed，同时保留正常冷根。建议第一步不可用即冷搜，中途失效只保留已验证前缀；首版不跨药水、Choice 或下一回合边界。
5. **Safe Execute**：逐本地动作使用原生提交、稳定等待和 predicted/live semantic post-state 对照；P1 seed 只影响搜索探索起点，不能复活旧 request/generation，也不会直接授权旧动作。
6. **药水**：本地药水已接入多人 Safe Execute；药水槽、策略面板、Smart/保护/强制与单人共用。多人只读取/消耗本地玩家药水，玩家类目标只允许自己，攻击/状态药仍可作用敌人；是否值得消耗药水固定按本地玩家的单人药水基线判断，队友战损不能改变用药资格。
7. **遗物策略**：多人复用单人遗物模拟、计数器和策略面板，但策略所有权只绑定本地玩家。队友持有的遗物不会显示为“已持有”，也不会生成本地遗物计数目标；队友遗物的真实战斗效果仍可作为战场状态参与多人预测。
8. **特殊牌策略**：多人复用单人的本地特殊牌策略。至亮之炎的最大生命消耗额度只统计本地动作玩家自己的使用，队友使用不会占额度；成长机会只扫描本地牌组及本地复制来源。依赖战斗历史计算变量的 Gold Axe、Voltaic、Tear Asunder、Pull From Below、Murder、Supermassive 在多人也保留状态指纹，不再因 PlayerCount > 1 被关闭。

U0–U6 的实现与 pinned/合同阶段均已完成。U5/U6 的部分真实 Host/Client observation → fresh replan 边界仍保留为运行时未验证项，不由 pinned replay 代替。

## Quality-first 当前状态

- **第一项：bounded multiplayer refresh**：已实现并通过 pinned/合同；真实多人正反例仍可继续补证据。
- **第二项：本地药水执行闭环**：已实现并通过 pinned/合同；真实多人自动喝药仍属于实机验证边界。
- **第三项：实际坏路线定位**：进行中。原则是只修复能由当前 HEAD 问题包证明的路线质量缺口，不凭感觉调整 Robust/Beam 权重。

已确认的第三项样本：

- 历史 ANGER 样本：未完成多人路线曾被 `AngerCopiesGenerated` 的最终排序过早惩罚；已有定向修复。
- 历史 X1 T3 空推荐：等价质量下 `ActionCount` 短路线曾压过当前回合实际出牌；已有定向修复。
- Beam retention A/B 诊断已接入：同一真实候选池同时观察 TeamObjective 与 legacy 单人排序，不改变生产选择。
- 最终排序归因新增始终可用的 `MP_QUALITY_LAYER`：记录原 baseline 胜者、Scenario 后的原始 baseline rank、Chance 后最终 baseline rank，并直接标记 `baseline|scenario_robust|shadow_chance`。它不依赖 U4 全矩阵完整，因此 E4 严格提前淘汰时也能判层；原 `MP_QUALITY_SORTING` 继续只承担完整 U4 三策略对照。
- CEREMONIAL_BEAST T6：VICIOUS 的精确模拟原本存在，但战略估值低估未来抽牌收益；现按可达 Vulnerable 触发次数计入 `CardAccessPotential`，相关合同/pinned 已通过。
- Headbutt immediate Choice 共用 `NativeChoiceRuntime`；当前实机样本已有完整自动选牌链。另一个跨回合 TurnStart Choice 缺口已独立补上 planned choice replay。

最近与上述代码相关的 compatibility / pinned 0.107.1 验证均通过。仓库治理已移除历史资料/生成输出，并删除旧后台在线状态、跑局统计上传、服务器更新检查和自动 Showcase 上传链；搜索/模拟算法本身未因此改变。

- TUNNELER real-multiplayer 连续问题包（`8ee4...` / `0c44...` / `426b...`）确认了两个不同现象：`Offering+` 并未被搜索器禁用，历史最终路线多次真实选中 `4:C:OFFERING`；最新 T4 状态已有无需 Offering 的同回合斩杀，因此不增加“见祭品必打”规则。相反，T1 `Inflame` 在 3 能量手牌中可用且最终路线仍留下 1 能量，三份包的最终路线均没有 `INFLAME`。Inflame 的 `StrengthPower` 模拟已存在于 `CorePowerSupport`；缺口是完成胜利后早期 persistent setup 价值归零，终局代表选择再用 Score/ActionCount 偏向少打一张牌。现统一给 Beam completed-victory representative 与 FinalPlanOrdering 增加“历史 peak persistent setup”低优先级 tie-break；仅当斩杀回合晚于当前搜索根回合时生效，避免为了已经到手的本回合斩杀额外刷 Power。

## 搜索效率 E0（2026-09-25）

- 已按 `CombatSolver_Search_Efficiency_Math_Plan` 接入 E0 观测，不改变 Beam/Robust/动作排序/预算：最终候选可追踪首次成为可比较候选、完成当前评估上下文、被选中、首次发布四个时间点。
- 请求级遥测复用既有 `BeamWidthPortfolioTelemetry`，给每次实际 Solver 成员分配 member id，并记录 Beam 宽度、SecondRankBand/BaseScoreOnly、Novelty、真实展开/转移和成员耗时。
- Shadow、Scenario Matrix、最终 materialization 与 annotation replay 记录调用次数和独占时间；Scenario Matrix 统计显式扣除嵌套 Shadow 时间，避免父子计时相加造成双算。
- 热路径不为每个节点写日志/格式化路线/做文本哈希；只有节点第一次进入可比较候选边界时才建立 `CandidateOrigin`。origin 存在 Solver 私有 `ConditionalWeakTable`，不进入 `SearchNode` record 的 equality/hash；同动作的 materialization/注释 clone 显式沿用 identity，新增动作则建立新 identity。
- 最终请求会输出 `SEARCH_E0_TIMELINE`、`SEARCH_E0_MEMBER`、`SEARCH_E0_PHASE`；Pinned 0.107.1 生产协调器采样 run `36031790808` 已得到下表。两个可离线复现样本的最终赢家都来自第一个 `potion_disabled#1` 成员，而不是后续 Beam 宽度精炼成员：

| 样本 | 来源成员 | 生成 ms | 评估 ms | 选中 ms | 发布 ms | 状态 |
|---|---|---:|---:|---:|---:|---|
| 简单攻防 | `potion_disabled#1` | 778.718 | 815.425 | 816.561 | 2023.430 | PASS |
| 抽牌/能量（含 Offering） | `potion_disabled#1` | 1157.420 | 1213.259 | 1214.584 | 1308.441 | PASS |
| 队友配合（LOUSE_PROGENITOR） | `potion_required#3` | 52265.851 | 52305.547 | 52305.549 | 52311.969 | PASS |

- 简单攻防从“已选中”到“首次发布”额外等待 **1206.869 ms**；抽牌/能量样本为 **93.857 ms**。这两个单人样本仍证明存在“已选中但延迟发布”的 E1 型现象。
- current HEAD `23a28ed3` 的真实双人 LOUSE_PROGENITOR 样本给出不同结构：最终赢家来自第三个 `potion_required#3` 成员，`52265.851 → 52305.547 → 52305.549 → 52311.969 ms`，selected→published 仅 **6.420 ms**。该候选首次生成时请求已经运行约 52.266 秒，因此这次多人慢搜索的主要延迟不是发布，而是赢家直到后续 portfolio 成员才出现。按效率计划的分支条件，该证据指向 **E2/E3**，而不是先做 E1。
- 同一 pinned run 已重跑 attempt 2，并且整条 Release/E0/U0-U1/U2/P0-P1/历史 A-B 链全部 PASS。第二次 E0 仍由 `potion_disabled#1` 产生最终赢家：简单攻防 `924.454 → 984.287 → 985.911 → 3102.190 ms`，抽牌/能量 `1740.239 → 1806.158 → 1807.911 → 1924.488 ms`。墙钟绝对值有明显波动，但“首成员早已生成/选中，发布更晚”的结构结论重复出现。
- attempt 1 的 P0/P1 尾部曾在 5 秒墙钟边界失败；同一提交 attempt 2 通过，且固定工作量测试始终通过，因此归类为墙钟波动，不作为 E0 搜索语义回归。
- E0 harness 的 detached 双玩家根仍只作为负向保护证据，不计入多人验收。2026-09-25 的真实双人 `LOUSE_PROGENITOR_NORMAL-73a5e5f8...` 问题包补齐了严格验收：`solverInformationalVersion=0.40.2+23a28ed360bbeb6c146b37fa42bccab94ee14885`，存在真实 MultiplayerProbe / MultiplayerAdvisor runtime marker，最终 E0 timeline 为 `player_count=2; route=MultiplayerLocalCrossTurn; team=True; scenario=True`，并实际执行 Shadow 与 Scenario Matrix。
- E0 实机闭环工具已补齐：`SEARCH_E0_*` 摘要会进入持久 process journal，timeline 显式记录 `player_count`；`source/tools/multiplayer-lab/validate-e0-search-efficiency-results.ps1` 可直接读取 JSONL/日志目录/ZIP，只在存在真实多人 runtime marker（或 Probe `PlayerCount=2`）且同一候选确实跑过 Shadow + Scenario Matrix 时输出 PASS。离线 detached harness 不满足该条件。
- LOUSE_PROGENITOR final candidate：`candidate_id=61670`，`expanded_at_generation=7762`，`turn_depth=10`；selected member `potion_required#3` 为 `elapsed=13573.462 ms / expanded=7815 / transitions=39850`。请求级 phase 总计：Shadow **1935 calls / 976.021 ms**，Scenario Matrix **2 / 27.827 ms**，materialization **3 / 18.734 ms**，materialization replay **3 / 9.503 ms**。当前执行环境缺少 `pwsh`，未直接运行 PowerShell validator；已逐项按 current HEAD validator 源码的完全相同条件核对，确定会进入 `E0_MULTIPLAYER_PASS` 分支。
- 2026-09-25 收到真实双人问题包 `THIEVING_HOPPER_WEAK-9df3f90a...`：Client Probe 明确记录 `players=2`，且 Scenario Matrix 已实际运行两轮；但最终候选排序在 `PolicyActionToken(TeammateForecast)` 抛 `ArgumentOutOfRangeException`，因此本次没有形成完整 E0 selected/published 时间线，不能计为第三个 PASS。根因是通用动作 token 只处理 PlayCard/UsePotion/EndTurn；现已为 `TeammateForecast` 增加稳定 token（含远端玩家、牌、目标），不改排序或搜索语义。
- **E0 已关闭（2026-09-25）**：三类样本齐全，且第三类为 current HEAD 的真实双人 Host/Client 运行证据。后续不再为了 E0 继续采样，除非改动再次触及搜索成员顺序、候选生成/评估/发布遥测或 E0 validator 语义。

## 搜索效率 E1（2026-09-25，已关闭）

- E0 已确认两个可离线复现单人样本存在“正式终局候选已选中，但仍等待 materialization / annotation replay 才首次发布”的真实延迟；E1 只修这个已证明的发布缺口，没有提前采用未完成 Scenario 复评的候选。
- 当前实现只在 production `FinalOrdering.Select` 已完成、Scenario rerank（若启用）与 deterministic block-potion insertion 已确定后，生成不持有 simulator 的正式路线预览并通过既有 `SolverProgress` 发布；完整 `SolverResult` 仍继续完成 replay、遗物标注和压平。
- 早发布的正式预览不会携带 `SolverRouteAdoptionSeed`，因此不会留下闭包访问仍存活或随后释放的 `SearchNode/SimulationSnapshot`。Coordinator 只有在该候选通过现有全局 `SolverInterimResultOrdering` 展示准入后，才记录 `Published` milestone。
- 固定 0.107.1 A/B（run `36102487372`）：
  - 简单攻防：baseline selected→published **2195.050 ms** → E1 **7.592 ms**。
  - 抽牌/能量：baseline **124.567 ms** → E1 **6.805 ms**。
  - 两个样本均 `sameRoute=true`、`sameQuality=true`、`sameWork=true`；最终动作、战损/药水/结束边界，以及 expanded / transitions 都未改变。
- E1 计划中的“消除重复评估”按证据条件处理：当前没有确认到同一正式 evaluation context 下 Scenario Matrix / final ordering 被重复复评的生产缺口，因此没有为了完成阶段引入跨候选/跨根缓存，也没有缓存可变 simulator。现有请求内缓存继续沿用。
- 最终验证：compatibility run `36102487522` 全 PASS；Pinned run `36102487372` 的 Release、E0/E1 A/B、U0/U1、U2、P0 contracts、P0/P1 runtime 与历史 P0 A/B 分类链全部 PASS。
- **E1 已关闭。** 后续若出现“同一正式上下文重复复评”的新证据，再单独加入严格键控的请求内不可变结果缓存；当前不扩大缓存面。

## 搜索效率 E2（2026-09-25，已关闭）

- 第一切片 `d92d25bb` 建立 `SearchStepStatus` / `SearchWorkAllowance` / `SearchStepResult` / `IResumableSearch` 与完整父节点安全点；第二切片把 incumbent、frontier/completed、active/ended/nextPlays、fallback、父节点游标和内存高水位集中进成员状态。
- 第三切片从 `3d60b7f0` 开始把整个搜索成员改成真正可离开调用栈再恢复的 `SearchMemberExecutionSession`：`SolveCore` 由 iterator state machine 保存阶段位置，session 的 `Step(allowance)` 在完整父节点/并行 wave 提交后返回 `Yielded`，下一次 `Step` 从同一成员状态继续。
- 当前代码 HEAD `a77d4085`：主 Beam portfolio 成员已由 `CombatSearchCoordinator` 按 **256 个已提交父节点**为生产切片连续 Step；当前仍一次把同一成员跑完后才进入下一个成员，因此 E2 没有改变 portfolio 成员顺序、选择规则或 E3 调度语义。
- `SimulationNotificationIsolation` 只覆盖每次 `Step` 的真实执行区间，session 暂停时不会继续占用通知隔离；诊断字段继续保持既有 `frontier=` / `ended=` 格式。
- deterministic P0 fixed-work A/B 已直接对成员 session 验证三种执行方式：continuous、1-parent slice、8-parent slice。三者完整 18 个 action token 逐项相同，均为 `NodeLimit`、projected loss=0、final HP=66、enemy HP=4、expanded=1200、choice branches=0、continuations=3、transitions=5958、committed parents=1200。
- 实际恢复次数也被门禁验证：1-parent 为 **1200 yields**，8-parent 为 **150 yields**；因此不是“开了接口但没有真的暂停”。
- 生命周期门禁 PASS：cancel probe 在提交 1 个父节点后取消，累计父节点不增加且所有 live simulator 已释放；Dispose probe 同样在 1 个父节点后释放所有 live simulator，并拒绝后续 resume。
- 最终验证：compatibility run `36095849302` 的 static-consistency 与 L1 contract-tests 全 PASS；Pinned 0.107.1 run `36095849283` 的 Release、E0、U0/U1、U2、P0 contracts、P0/P1 runtime、历史 P0 A/B 全链 PASS。
- 本阶段始终没有改变 Beam 宽度、评分、Robust、药水/遗物/特殊牌、多人数值语义或执行权限。
- **E2 已关闭。** 后续除非再次修改 member-session 状态所有权、安全点、取消/Dispose 或累计预算语义，否则不继续在 E2 增加可恢复搜索改造。

## 搜索效率 E3（2026-09-25，已关闭）

- **交付结论：生产采用 E3A 固定轮转；E3B 自适应不启用。**
- Smart 用药层复用 E2 `SearchMemberExecutionSession`，每个工作片最多 256 committed parents / 1024 transitions。多个 exact-potion 成员可同时驻留；暂停成员不累计自己的搜索预算钟，请求级 deadline 仍按真实墙钟统一约束。
- fixed 与 serial 的 deterministic P0 门禁完全等价：动作序列、boundary、战损、终局 HP、敌方 HP、结束回合、显式药水数一致；总工作均为 **1536 expanded / 6310 transitions**，两个 `potion_required` 成员均为 **2146 / 2172 transitions**。
- fixed 确认是真轮转而非提前建 session：第二个用药成员在约 **646.5 ms** 首次获得真实工作，而第一个成员约 **1053.9 ms** 才完成；serial 中第二成员要等第一个约 **653.2 ms** 完成后才开始。
- E3A 已在 `3073c757` 作为 full-search 生产默认启用；不新增 UI，不改变目标函数、Beam 排序、Robust、Smart 用药资格、遗物/特殊牌、多人数值或部署权限。Novelty 与没有 E0 证据的其他 Beam 组合未在本阶段重构。
- E3B 保留原计划的实验实现：同硬约束类别内用 `Δq/Δt` EMA，完整胜利/消除死亡风险使用独立优先级，并保留探索份额、重要候选优先和确定性 tie-break；内存压力可把驻留成员收缩到 1。
- 本轮 pinned 资格结果：`AdaptiveSameQuality=true`、`AdaptiveSameFixedWork=true`，但 `AdaptiveBonusSlices=0`，因此 `AdaptiveTriggered=false`、`AdaptiveReducedWork=false`、`AdaptiveQualified=false`。该样本没有证明 adaptive 比 fixed 更早达到质量或减少工作，所以按计划保持关闭，不为了“自适应”增加生产复杂度。
- 最终验证：compatibility run `36100277486` 的 static-consistency / L1 contracts 全 PASS；Pinned 0.107.1 run `36100277430` 的 Release、E0、U0/U1、U2、P0 contracts、P0/P1 runtime 与历史 P0 A/B 分类链完成。P1 的 5 秒 timed 样本在已胜利状态触及 TimeLimit，但 5000-node fixed-work 两种目标均 PASS，历史分类为 `FIXED_WORK_PASS_TIME_BOUNDARY`，不作为搜索语义回归。
- **E3 已关闭。** 后续若没有新的 time-to-quality 数据证明 adaptive 稳定优于 fixed，不重新打开 E3B。

## 搜索效率 E4（2026-09-25，已关闭）

- 生产继续使用原 **Robust** 固定四压力情景语义；E4 只改变这些情景的复评组织方式，不修改团队目标、Beam 排序、药水/遗物/特殊牌、多人牌过滤或部署权限，也没有提高默认总预算。
- 严格模式现在先完整评估第一个可用 current-decision 作为 incumbent。后续候选继续复用当前 U3 的一次共享 Shadow 搜索，但情景 replay 按 incumbent 中更可能暴露弱点的压力顺序执行。
- 对部分已评情景构造合法的 Robust 乐观下界：存活/终局字段只使用已经不可逆暴露的坏结果；worst-loss / worst-player / team-loss / enemy-durability 使用已观察最大值；未知 mean-loss 明确取 **负无穷**，未知情景从不按 0 或“良好结果”写回矩阵。
- 只有该候选在“未评情景全部取得最理想值”的情况下，仍按现有完整字典序严格落后于一个已完整评估的 incumbent，才允许停止剩余 replay。等价/tie 情况不会剪枝；被跳过的格子保持 `Unknown`，并记录 `strict_eliminated / strict_reason / skipped_scenario_replays`。
- 最终 Robust 排序允许“完整候选 + 已被严格证明淘汰的候选”形成闭合比较；普通预算中断/Unsupported 造成的 `Unknown` 仍 fail closed 回原 baseline。U4 nominal-reference / bounded-risk 诊断只有所有候选都完整时才运行，不把 E4 缺失格子伪造成完整矩阵。
- 小型完整枚举门禁覆盖：严格渐进与全量复评选择同一 Robust 胜者、保留同一 baseline tie-break；另有“前三个压力情景都更好、最后一个情景才把候选翻成劣势”的反例，严格模式必须看到最后一格后才能淘汰。另一个明显劣势候选在首个压力情景后可安全跳过剩余 **3** 次 replay。
- E4 的近似“首动作一致率 C”没有进入生产：计划本身规定它不是正确率/置信度，也不是严格停止证明。若未来要启用，必须作为单独近似策略做 A/B，不混入本次保持结果等价的优化。
- 验证：compatibility run **36104370041** 的 `static-consistency` / `contract-tests` 全 PASS；Pinned 0.107.1 run **36104358801** 的 Release、E0、U0/U1、U2、P0/P1 与历史分类链全部 PASS。
- **E4 已关闭。** 当前实现能真实省掉的是已生成四压力路线之后的部分 scenario replay；共享 Shadow 搜索仍按一次/候选完整计费，不把这段成本伪装成 E4 收益。
## 搜索效率 E5（2026-09-25，已关闭）

- **生产只启用 E5A 策略引导动作枚举；E5B 中途估值不启用。** 本阶段只改变合法卡牌动作进入 replay 的先后，不改变动作集合、每节点候选额度、Beam retention / final ordering、Robust、Smart 用药、搜索预算或部署权限。
- 串行 Expand 与并行 PrepareCardActions 统一复用同一份 prepared-action 枚举，避免两条搜索路径以后再次出现动作顺序漂移。
- 动作顺序使用确定性只读提示：**估计可立即斩杀 > 当前存在预计掉血时的防御 > 每资源战略价值 > 原始战略价值 > 低资源成本 > 原始稳定顺序**。战略价值复用已有 CardChoiceSupport.CardValue；目标生命、当前能量/星能只用于排序提示，不参与剪枝或结果评分。
- 排序是完整候选集上的稳定优先级，不会因为提示分低而删除动作。legacy 手牌顺序保留为 pinned A/B 测试入口，不暴露用户设置。
- Pinned 0.107.1 E0 A/B（run **36106327175**）两个代表根均保持 sameQuality=true、sameRoute=true：
  - simple：legacy **836.554 ms / 369 expanded-at-generation**；E5 **947.748 ms / 369**。确定性生成工作量不变，约 111 ms 墙钟差按运行噪声处理，不把它宣称为收益。
  - draw_energy：legacy **1641.287 ms / 839 expanded-at-generation**；E5 **1633.226 ms / 823**，最终赢家提前 **16** 个展开节点生成，墙钟约提前 **8.062 ms**。
- 这组证据证明 E5A 至少能在抽牌/能量根上让最终优质路线更早出现，同时 simple 根没有增加生成所需展开数；但没有证据表明剩余主要瓶颈来自“路线已生成却因中途估值过低被 Beam 丢弃”。因此按计划不修改 Beam 中途估值，避免把动作排序与评分语义混在同一阶段。
- 合同增加纯排序门禁，覆盖斩杀、防御、价值效率和完全平手时的稳定原顺序。最终验证：compatibility PR run **36106409949** 的 static-consistency / contract-tests 全 PASS；Pinned run **36106327175** 的 Release、E0/E1/E5 A/B、U0/U1、U2、P0/P1 与历史分类链全 PASS。
- **E5 已关闭。** 后续只有新的真实坏路线证据明确显示“目标动作已经生成，但在中途评分/Beam 保留处被淘汰”时，才单独重开中途估值工作。

## 搜索效率 E6（2026-09-25，已关闭）

- **E6A 不启用新的 pre-replay 可交换顺序剪枝。** 当前生产模型没有一份已证明完备、同时覆盖 Hook、战斗历史、RNG、死亡/复活、Choice/调度以及多人观察机会的动作读写集；按原计划，未知 Hook 必须视为全局依赖。仅凭卡牌类型、目标或现有 `IsPure` 历史分类不足以证明 `F_B(F_A(s)) = F_A(F_B(s))`，因此不把经验性“看起来可交换”升级成硬剪枝。
- 生产继续保留现有 **完整 replay 后的精确状态去重**：`ExactTranspositionKey` 固定建模战斗状态，`TranspositionFrontier` 再以保守 Pareto label 区分药水/卖血/累计战损、团队与最弱队友损失、ActionCount/Score、路线 traits、边界/死亡/胜利、PredictionGaps 与 CombatProgress。它不能省掉首次 replay，但不会为了 E6 把未知副作用当作等价。
- 重新接通并修复 `TranspositionFrontierChecks`：合同现在跟随 current label 结构，验证完整等价 label 会合并；Boundary、PlayerDead、AllEnemiesDead、PredictionGaps、CombatProgress 不同不会被误并；同时用独立 comparator 做随机决策对照，并保留 singleton frontier 分配回归检查。该检查已加入 PowerShell/Bash contract 入口。
- **E6B 不启用局部 exact-kill DFS。** E0/E5 当前证据没有出现“主 Beam 候选池漏掉一条已知合法斩杀”：E0 的多人慢例是最终赢家直到后置 portfolio 成员才生成，E5 只证明动作顺序可让部分赢家更早出现。原计划明确要求只有 E0 证明 Beam 漏斩杀时才启动 DFS；现在加入会成为没有证据支持的额外搜索成本，并破坏“同一请求总预算”约束。
- 本阶段因此**不改变生产搜索行为、Beam/Robust/Smart 药水/遗物/特殊牌语义，也不增加默认节点、时间或情景预算**。E6 的交付是把两类高风险优化的启用条件锁死，而不是为了阶段编号强行加入近似剪枝。
- **E6 已关闭。** 未来只有新的 current-HEAD 问题包能证明“存在合法短窗口斩杀，但完整候选池没有生成它”时，才重开 E6B，并限定为共享现有请求预算的当回合/短窗口 DFS；若要重开 E6A，则必须先有覆盖相关 Hook/历史/RNG/死亡/调度语义的完备读写契约与反例门禁。

## 当前未验证边界

- 当前 HEAD 的真实多人 Beam retention A/B：需要在“明显不如手打”的合法局面上确认更好路线究竟在 Beam、portfolio、U3/U4 还是执行层丢失。
- U5/U6 的真实远端插入后 WorldVersion advance → fresh search → old request dormant。
- 具体连续抽牌/Choice 链仍应以新问题包为准，不把旧日志当当前代码证明。

## 下一任务

搜索效率 **E0–E6 已按证据全部关闭**。下一任务回到 Quality-first 第三项：只处理 current HEAD 新出现、且有问题包与合法手打前缀证明的明显坏路线；先沿 `FINAL_CANDIDATE → MP_QUALITY_SORTING → FINAL_SELECTION → Safe Execute` 定位丢失层，再只修改有证据的那一层。



## 2026-09-25 LOUSE_PROGENITOR T3 cross-family time-to-quality

- 新问题包 `ece84d5632d449f7b8717c9ef68f54df`：最终 4 回合路线在 T3 使用 `OFFERING+`，并通过 `ATTACK_POTION -> FIEND_FIRE` 压缩战斗。
- E0：winner `potion_required` 首次生成约 45.235s，最终发布约 46.439s；Novelty ~5.010s，无药 Beam ~37.903s 且 `TimeLimit`，一药水层 ~3.527s。结论：跨搜索家族的串行 starvation。
- 当前修复：Novelty 后插入 bounded exact-one-potion scout；它与主 Beam 共用同一总预算。完整 scout 在最终无药基线下重新校验后可复用，截断/不再合格则回退原 E3 Smart audit。
- 实机回归关注：T3 的 `potion_required` member 应显著早于无药 Beam 完成获得工作；最终路线质量不得退化。需要用户用当前 HEAD 重跑同类局面确认真实 time-to-quality。


## 2026-09-25 multiplayer quality rollback to local single-player core

- 5 个连续实战包显示跨战斗退化，不是单卡问题。四个 DECIMILLIPEDE 包合计 307 次 FINAL_SELECTION，233 次未发生 Scenario/Chance rerank，坏路线主要由 multiplayer baseline 直接产生。
- 最明显样本中本地 projected_hp 已为 -25/-28、all_players_alive=false，但 AdaptiveLethalTempo 仍按团队损失/敌方耐久区分死亡路线；这与用户“单人算法直接打多人高血量怪物反而更好”的 A/B 观察一致。
- production policy 已切换为 local-single-core：真实多人 root + 单人质量排序；team objective / teammate forecast / scenario reevaluation 生产关闭。多人网络、怪物语义、目标语义、安全执行与 continuation 不变。
- 旧多人质量层保留为测试/研究代码，可由测试直接 override SearchPolicySnapshot，不删除历史 U2/U3/U4 证据。
- 新诊断：`MULTIPLAYER_QUALITY_MODE mode=local_single_core team_objective=false teammate_forecast=false scenario_reevaluation=false`。


### CI contract migration for local-single-core

- `E0PinnedHarness --scenario teammate` now explicitly opts into Advisor capability before root capture, so the detached two-player root is an admitted local-player multiplayer search instead of an optional unsupported probe.
- The teammate E0 scenario now validates the **production** policy: `MultiplayerLocalCrossTurn` route semantics with Team Objective, teammate forecast and scenario reevaluation all disabled; any Shadow phase is a failure.
- `test-u2-search-kernel.ps1` was updated from the obsolete “production multiplayer keeps team objective enabled” assertion to the new local-single-core production contract. Experimental U3/U4/Shadow contracts remain intact and separately tested.


- local-single-core also disables `MultiplayerCarryRankingContext` inside FinalPlanOrdering when Team Objective is off. Carry remains available to experimental/team-objective tests, but production local quality no longer has a remote-risk tie-break after otherwise equal local routes.


- Pinned E1 early-publication A/B no longer treats exact expanded/transition equality as semantic correctness. The callback changes wall-clock overhead while portfolio refinement is time-gated, so exact work can differ even with the same route and quality. `sameWork` remains in evidence; route and final quality are still hard failures.


## 2026-09-25 multiplayer prediction master switch

- Added persisted `UseMultiplayerPrediction`; default is `false`, preserving local-single-core.
- General settings exposes one experimental toggle. Off: real multiplayer root + local single-player quality ordering. On: Team Objective + teammate forecast + Scenario/Robust + Carry ranking.
- Multiplayer objective selection is disabled in the UI while the master switch is off.
- E0 pinned coverage uses the same detached two-player root for both switch states; U2 static contracts verify the production gate.

## 2026-09-25 upstream backport

- Backported upstream PR #127 prediction semantics: Power/relic-driven power applications explicitly use a null card source, and removed creatures reject later predicted Power application.
- Adapted the Hand Drill hook to the pinned 0.107.1 path in `AfterDamageGivenMirrors`; the newer-version hook path is kept aligned as well.
- Backported upstream PR #134's bounded fresh-resource stand-pat lane: only the first 64 candidates in existing beam order receive the expensive cross-turn roll-out.
- Deliberately did not merge upstream UI, Loadout, ServerGC, or newer-game-version compatibility changes.

## 2026-09-25 upstream hot-path backport

- Backported upstream PR #125 fast lanes without changing search ordering or budgets.
- AfterBlockBroken, AfterCardPlayed, AfterAttack, and AfterModifyingHpLostAfterOsty now use the existing mirrored-hook participation mask instead of scanning listeners that cannot handle the hook.
- The hook enumerator now applies masks independently to segmented run-listener snapshots and has an unsuspended cleanup mode for paired attack state.
- Death lifecycle fingerprinting replaces per-node LINQ OrderBy allocations with stable inline sorting; COMBATSOLVER_VERIFY_FAST_LANES=1 can reconcile both fast lanes against the old behavior.

## 2026-09-25 ModelDb.GetId cache backport

- Backported the isolated route-preserving part of upstream PR #114: ModelDb.GetId(Type) now memoizes the immutable Type-to-ModelId result.
- Null and exception behavior stays on the native path; no ModelDb content or model instance is cached.
- Registered in the normal RitsuLib patch set and the OfflineSearchHarness patch inventory.
- No transposition-table limits, learned portfolio experiments, GC truncation, or other decision-changing PR #114 changes were imported.

## 2026-09-25 Crossbow generation cache backport

- Backported the measured Crossbow-only part of upstream PR #114.
- Crossbow now reuses the existing root-captured character attack pool through GetDistinctUnlockedCharacterAttacksForCombat; RNG selection, card instance creation, add-to-hand, and free-this-turn semantics are unchanged.
- The five turn<=1 relic generation sites remain on their existing code because upstream measured no meaningful search-path benefit there.

## 2026-09-25 multiplayer history-counter owner fix

- PR #118 incremental history counters were already present, but their owner binding still disabled counters whenever Players.Count > 1.
- Multiplayer detached roots now bind the counter owner to the unique RootActionPlayers entry (the local search player); single-player/non-root fallback remains unchanged.
- Shared finished-play count still records every simulated play, while owner-scoped draw/generation/orb/hit counters remain local-player scoped.
- E0's real two-player detached fixture now verifies GetCounters(local) on the root simulator and an ordinary fork.
- U2Runtime now installs ModelDbGetIdCachePatch so pinned 0.107.1 harnesses verify the new Harmony target.

## 2026-09-25 runtime gate reduction

- Multiplayer Safe Execute no longer performs a second one-action solver replay before every native action.
- Per-action continuation/remote semantic fingerprints are no longer authorization gates; action continuation now keeps only native-action capture, queue-idle, advancing WorldVersion, and stable WorldVersion.
- Safe-execution boundary snapshots no longer compute duplicate local/remote fingerprints.
- Multiplayer root capture no longer runs the full post-capture pile/orb/remote-fingerprint verification pass.
- Core search StateFingerprint remains intact for beam deduplication, transpositions, cycle detection, RNG/state identity, and other search semantics.

## 2026-09-25 continuation fingerprint removal

- Removed the dedicated multiplayer continuation remote-state fingerprint from SimulationSnapshot, continuation expectations, validation input, and reuse matching.
- Continuation reuse still requires the existing ContinuationStamp plus combat/local-player identity, multiplayer scaling/card rules, and an advanced WorldVersion.
- Search retention no longer computes a teammate-state fingerprint at every future turn boundary, and terminal continuation building no longer replays solely to recover that fingerprint.
- The carry-ranking fingerprint is intentionally left separate for now; it is not a continuation admission gate.

## 2026-09-25 probe and carry fingerprint cleanup

- Multiplayer probe now computes only the compact fingerprint that actually advances WorldVersion; the extra reactive-public and local-boundary fingerprints and their delta log were removed.
- The compact fingerprint value is no longer printed in the normal OBSERVED log; only the fact that WorldVersion advanced is logged.
- Carry Ranking no longer stores or computes duplicate PublicFingerprint / RemotePublicFingerprint values. It uses its captured immutable player/enemy arrays plus WorldVersion.
- The old MultiplayerContinuationRemoteFingerprint implementation and its probe wrapper were removed after continuation admission stopped consuming them.
- Evidence validators now key off compact WorldVersion advancement rather than the removed remote-public diagnostic hash.

## 2026-09-25 gate collapse

- Safe Execute post-action revalidation now has only five facts: native action captured, native queue idle, WorldVersion advanced, WorldVersion stable, and whether another planned action exists.
- Removed the retired hand-removal, energy/stars, target-identity, enemy-delta, remote-delta, and semantic-replay compatibility facts and their failure diagnostics.
- MultiplayerSafeExecutionBoundary now carries only WorldVersion and observation sequence; it no longer snapshots piles, powers, enemies, teammate state, HP, block, energy, or stars for every played action.
- Safe EndTurn preflight was reduced from ten duplicated gates to four: current combat lifecycle, local playable turn, no pending choice, and stable WorldVersion. Session.TryBeginEndTurn remains the single owner of turn, route-generation, authorization-state, and accepted-WorldVersion checks.
- Removed the retired RemoteOrUnknownChange post-action decision and enemy-token heuristic gate. Pre-action WorldVersion invalidation remains the stale-route boundary.
