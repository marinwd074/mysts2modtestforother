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

## 搜索效率 E0（2026-09-25）

- 已按 `CombatSolver_Search_Efficiency_Math_Plan` 接入 E0 观测，不改变 Beam/Robust/动作排序/预算：最终候选可追踪首次成为可比较候选、完成当前评估上下文、被选中、首次发布四个时间点。
- 请求级遥测复用既有 `BeamWidthPortfolioTelemetry`，给每次实际 Solver 成员分配 member id，并记录 Beam 宽度、SecondRankBand/BaseScoreOnly、Novelty、真实展开/转移和成员耗时。
- Shadow、Scenario Matrix、最终 materialization 与 annotation replay 记录调用次数和独占时间；Scenario Matrix 统计显式扣除嵌套 Shadow 时间，避免父子计时相加造成双算。
- 热路径不为每个节点写日志/格式化路线/做文本哈希；只有节点第一次进入可比较候选边界时才建立 `CandidateOrigin`。origin 存在 Solver 私有 `ConditionalWeakTable`，不进入 `SearchNode` record 的 equality/hash；同动作的 materialization/注释 clone 显式沿用 identity，新增动作则建立新 identity。
- 最终请求会输出 `SEARCH_E0_TIMELINE`、`SEARCH_E0_MEMBER`、`SEARCH_E0_PHASE`。三个代表局面的实际 E0 表（简单攻防、抽牌能量、队友配合）仍需用当前 HEAD 的可重放输入采样，因此当前只标 **instrumentation implemented / runtime evidence pending**，不据此提前进入 E1。

## 当前未验证边界

- 当前 HEAD 的真实多人 Beam retention A/B：需要在“明显不如手打”的合法局面上确认更好路线究竟在 Beam、portfolio、U3/U4 还是执行层丢失。
- U5/U6 的真实远端插入后 WorldVersion advance → fresh search → old request dormant。
- 具体连续抽牌/Choice 链仍应以新问题包为准，不把旧日志当当前代码证明。

## 下一任务

继续 Quality-first 第三项：

1. 用当前 HEAD 复现一个明确不如合法手打前缀的多人局面，并保留正式问题包/Client journal。
2. 沿 `FINAL_CANDIDATE → MP_BEAM_RETENTION_AB → MP_QUALITY_SORTING → FINAL_SELECTION → Safe Execute` 定位候选丢失层。
3. 只有真实候选池证明 TeamObjective/Robust 排掉更优合法路线时才改主排序；若最终选择正确但动作未执行，则回到 Safe Execute/Choice/continuation。
4. 比较生存、累计战损、最终 HP、结束轮数、药水支出与响应时间，保持总搜索预算不变。

每次只推进一个小阶段，并在结束时更新本 handoff；不要重新创建阶段流水账文档。
