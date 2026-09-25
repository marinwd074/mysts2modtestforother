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
2. **团队目标/情景**：多人叠加公平 Scenario Matrix 和 Robust 目标；生产默认仍为 Robust。
3. **交错预测**：支持 local → forecast-only teammate → local 的 detached 模拟；队友节点没有 deployment authority。U5 reverse-order 等价探针只在详细诊断开启时运行；生产搜索不再为纯日志额外 Fork/重放每条队友路线。单动作 teammate forecast 不再固定先 Fork 一份只读 seed：候选先在原状态只读枚举，只有真实候选才按原语义逐候选 Fork；无合法队友动作时零额外 simulator clone。性能设置新增两个默认开启的多人专用开关：关闭“多人队友联合预测”会同时停用回合内 U5 与结束回合 Joint teammate forecast；关闭“多人 Robust 情景复评”会停用 U3 Scenario Matrix，并把预留节点预算还给主 Beam。两个开关都不改变单人模式。
4. **路线刷新**：轻量 HP/Block drift 可对少量保留候选做 bounded refresh；目标死亡、资源、Power、牌堆/RNG 等强变化走 fresh search。
5. **Safe Execute**：逐本地动作使用原生提交、稳定等待和 predicted/live semantic post-state 对照；旧 request/generation 不可复活。
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

## 搜索效率 E2（2026-09-25，第一切片）

- 已提交 `d92d25bb`：引入 `SearchStepStatus`、`SearchWorkAllowance`、`SearchStepResult` 与 `IResumableSearch`，先把串行父节点展开变成可恢复的确定性工作单元。
- 新的 `ParentExpansionSession` 只在一个完整父节点展开、子节点提交且父快照释放之后推进游标；取消发生在下一父节点提交前，不会越过这个安全点。
- 普通串行搜索使用 unlimited allowance，保持原来连续执行；`VerifyIncrementalSearch=true` 时强制每次只提交 1 个父节点，因此同一真实 `CombatBeamSolver` 会反复经历 `Yielded -> Step -> resume`，并沿用原有固定节点/转移预算。
- 会话自身保留累计父节点游标；分片之间不重置工作量。内置合同覆盖 continuous 与 single-parent split 的顺序一致、累计预算不重置、预算耗尽以及预取消不提交新工作。
- 本切片没有改变 Beam 宽度、评分、Robust、portfolio 成员顺序、药水/遗物/特殊牌、多人数值语义或执行权限；并行展开路径也保持原样。
- 验证已通过：compatibility run `36091943182` 的 static-consistency 与 L1 contract-tests 全 PASS；Pinned 0.107.1 run `36091943224` 的 Release、E0、U0/U1、U2、P0/P1 runtime 与历史 P0 A/B 全链 PASS。P0/P1 的 fixed-work 路径显式使用 `VerifyIncrementalSearch=true` + 单线程，因此真实搜索器实际执行了 single-parent pause/resume 切片。
- E2 **尚未关闭**：当前 `frontier/completed/current turn layer/playDepth/active/ended/nextPlays/incumbent` 仍由 `SolveCore` 局部生命周期持有，还不能把整个搜索成员交回协调器后再恢复。下一切片要把这些状态收纳为成员级 session，再做完整单成员 continuous-vs-sliced 结果/工作量 A/B。

## 当前未验证边界

- 当前 HEAD 的真实多人 Beam retention A/B：需要在“明显不如手打”的合法局面上确认更好路线究竟在 Beam、portfolio、U3/U4 还是执行层丢失。
- U5/U6 的真实远端插入后 WorldVersion advance → fresh search → old request dormant。
- 具体连续抽牌/Choice 链仍应以新问题包为准，不把旧日志当当前代码证明。

## 下一任务

继续 **E2 第二切片**，暂不进入 E3，也不回头做 E1：

1. 把 `frontier/completed/current turn layer/playDepth/active/ended/nextPlays`、fallback/incumbent、下一父节点游标与累计工作量，从 `SolveCore` 局部变量收纳到单个成员拥有的 resumable session；root 继续只读。
2. 协调器仍按旧策略把同一成员连续 Step 到结束；本阶段不交错 portfolio 成员、不改成员顺序、不加早停/上界，先隔离“状态可恢复”这一项变量。
3. 增加固定 deterministic work A/B：同一 root 连续 unlimited 与多段 allowance 恢复后，最终 actions、终局快照/BoundaryReason、候选顺序、expanded/transition 总量必须一致；取消后不得保留会继续扩展的 simulator，恢复不得重置预算。
4. 只有完整单成员 pause/resume 等价性闭环后才进入 E3 的 portfolio 调度；Beam/评分/Robust/药水遗物/特殊牌/多人权限继续冻结。

每次只推进一个小阶段，并在结束时更新本 handoff；不要重新创建阶段流水账文档。
