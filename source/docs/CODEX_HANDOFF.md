# Codex 当前交接

> 只记录当前生产状态、当前风险和下一任务。历史阶段过程、旧问题包结论和已关闭实验从 Git history 恢复，不在本文件维护流水账。

## 基线

- 项目记录版本 `4.42`；版本内容与功能提交见 [`PROJECT_VERSION_HISTORY.md`](PROJECT_VERSION_HISTORY.md)，上游适配来源见仓库根 `UPSTREAM.md`。
- CombatSolver `0.40.2`
- STS2 / RitsuLib pinned `0.107.1`
- .NET 9 / Godot 4.5.1 / `STS2_01071`
- 单人与多人共享主搜索/模拟核心、深度策略入口和评价范围描述；单人继续以战斗结束为预测目标，不套用默认多人的三回合上限。提速优先复用计算、去重和安全剪枝，保留完整路线质量。
- 生产多人默认使用 **local-single-core**：本地单人质量排序 + 多人真实状态/WorldVersion + 本地玩家执行权限。Team Objective、teammate forecast、Scenario/Robust 等实验多人预测由 `UseMultiplayerPrediction` 单独开启，默认不作为生产质量层。
- MultiplayerOnly 牌保留真实牌堆占位与抽牌距离，但不进入主动搜索/自动执行。
- 多人 Safe Execute 只部署本地玩家动作；队友观察不会获得部署权限。

本地代码层验证入口为 `tools/run-offline-validation.ps1 -GameDir <匹配的游戏安装目录>`：使用外部 0.107.1 DLL，不启动实际游戏，不要求另复制 game-body 二进制。入口包含静态门禁、Release、L1/portfolio 合同和 17 个 pinned 模式，并输出构建摘要与未覆盖边界。Coordinator 的补充审计与质量排序已分别归入 `Audits` / `ResultPolicy` partial，方法内容与预算不变。该入口的 PASS 不关闭下文 current-build Host/Client / UI 未验证项，权限不足导致的测试子项跳过会保留在摘要中。

多人纯代码验证新增 `MultiplayerInterleavingChecks`（L1 默认运行）：在确定性时间下遍历 10,493 个因果顺序，覆盖最多三个远端观察更新与稳定读/确认交错、重复观察、取消后回调、回合/路线/世界身份和一次性 EndTurn。直接链接生产观察与授权实现；失败用 `--replay <id>` 重放。生产观察器仍为主线程入口，200ms 窗口不变。该测试没有启动游戏、网络或完整 Controller，不替代真实 ENet 和原生动作归因。上游 archive/multiplayer 的 ENet 脚本要求 0.111.0，当前只作为后续适配参考。

## 当前任务：多人本地搜索响应

本任务以仓库源码和离线工具验证。用户允许仅在必要时使用 REA，验证范围仍为不启动游戏的定向检查。

实施顺序见 [本地搜索计划](multiplayer/LOCAL_SEARCH_PLAN.md)，完整目标仍在进行中。同回合刷新沿用既有 `SolverSearchSession` / Coordinator worker；根捕获和结果接纳仍由 Runtime 负责。候选在原生动作/捕获 barrier 后校验并深复制，消费时清除旧已发布结果，避免同回合去重或捕获失败使旧建议复活。现保留最多三条完整路线的纯值建议；后台先重放当前路线，再在原 192 节点/60ms 恢复份额内重评至多两条完整备选。当前路线有效也可选择新状态下更好的备选；只有全部未获准才进入前缀物化或普通搜索。提前获胜截断旧尾部，单条不可用不影响其他候选。

备选使用新根重新模拟后的质量，部分路线仍需末态等价和相同评价回合；不能增加原路线战损/保命消耗、改变成长/遗物验收值，或丢掉已证明的胜利。自身末态基线从尚存快照捕获；已释放的旧分支不为保留建议额外重建。自动恢复始终清除显式采用标记，未通过备选验收的胜利也不能经共享见证绕回结果。精确续用与独立 continuation 均已传递完整备选，按当前回合裁切动作并重建前缀；续用后再刷新也不会重放历史动作。独立跨回合入口保留原身份/世界版本/Targets RNG 准入，当前加备选共享原 600ms 软份额，失败直接进入 R1/普通搜索。

P2 已落实根前/根后的已兑现资源账本：`RouteResourceLedger` 从已有路径节点捕获纯值，不增加模拟转移；续用快照及当前/备选基线一起扣除过去回合的成长、实际保命消耗和已兑现局外价值。累计受伤与回复分开，修复用净 HP 差低估剩余战损的问题。全战斗战损上限、剩余药水成本和遗物最终计数保持各自口径；未知检查点或累计关系不一致拒绝相应旧证明，不把缺失值当零。共同窗口、末态估值和原实现对照也已完成下述代码层范围；尚未改变深度分配或承伤策略。

P2 现已增加纯值 `RouteEvaluationWindow`：捕获根回合、目标边界、实际回合/玩家阶段、终局及可解析状态，贯穿模拟快照、结果、预览、药水基线和恢复请求。续用按新根移动目标窗口，不把旧三回合的剩余两回合标成新的三回合。最终用药审核按窗口保留无药水基线，Smart 梯度审核也禁止用不同窗口的战损差宣称节省 HP；零成本生成药水和原有获胜救场分支保留。非终局备选恢复进一步检查阶段，不能只凭相同回合编号通过。范围字段不授予队友预测或执行能力。

单人和多人现共用 `SearchEvaluationPolicy` 及 `RouteEvaluationWindow`，单人的空目标明确表示完整战斗，续用后不会变成三回合预测。预览排序合并共同流程，Beam、最终排序、无药水基线及 Coordinator 共用胜负/生存/保命消耗前置比较。定向断言暴露并修复了旧 `CompareRecovery` 在无偷窃策略时仍先比胜负的问题：单人仍胜利优先，多人恢复原始战损在胜负之前的既定规则；没有引入承伤换节奏实验。

普通候选共用完整目标资格；没有完整目标时，搜索已提交的 `Frontier` 决定部分比较范围，传入最终保留、补齐和选择。最终选择先通过用药审核，再比较同一窗口内的候选；前沿没有合规代表时先选临时范围，不跨范围比战损。未解析候选只保留临时 Beam 建议。预览及 Coordinator 在完成/生存资格后返回明确的不可比较状态，不能用分数或可出牌动作规则绕过；Smart 基线复用也拒绝不可比较。单人和多人使用同一成本窗口规则，单人的目标仍是战斗结束。窗口排除单独诊断，不计入药水政策拒绝。

`CompleteEvaluationWindows` 在最终排序前为至多三条尚存模拟器的回合起点候选独立分叉，借用至多两条已覆盖所选范围的路线尾部，沿用原重放器而非新增搜索引擎。有完整目标时补齐完整目标，否则可补齐到已提交的部分前沿，仍保留未完成最终目标的标记。每个追加动作扣原请求节点份额并记录转移，时间沿用原预算；成功末态仍须经过正式政策审核。失败保持原候选/模板，异常、取消和最终审核失败释放新增快照。只支持本地卡牌/结束回合尾部，跳过已释放、回合中间及 turn-setup，不扩大执行权限。

P2 的约定代码层范围已完成。中间窗口未来值从末态重算，与实际 HP/资源账本分开。补齐限于最终阶段仍持有快照的候选，跨成员窗口不一致时保留已有结果。P3 和 P4 的独立实验均完成代表性验证，默认关闭。下一步 P5 用保留输入验证不同队友动作和目标变化下的重决策，并核对未获回复时的真实承伤代价。

P3 在原 Beam 完成回合保留后选下一层的候选。第二层名额为宽度的四分之三，第三层为一半，向上取整且至少五席，受现有候选数限制。先保留质量、即时进攻、启动、延迟伤害和防守代表，余下席位优先给不同首动作。已启动的路线先按实际保命消耗和受伤筛出最低成本组，再按原启动估值挑代表。复用原候选、模拟器、终局审核和快照释放流程。实验适用于本地核心多人 Beam，强制药水及已有接管/停止状态沿用普通路径，单人完整搜索走原调度。

P3 固定根对照使用 `p2-quality --selective`，复用启动、延迟和防御场景。早期实验误删低成本晚启动路线，按实际成本保护启动后修复。当前候选三个场景战损为 1/5/0，与固定深度相同，展开 88→70、102→79、89→73。此前单人开关隔离检查在第 6 回合获胜、战损 6、226 展开，动作和末态相同。各路线独立重放并核对根隔离。

`native-redecision` 通过实际 `ActionQueueSet`/`ActionExecutor` 让队友在每回合初次搜索后出一张合法 Strike，再由生产 Coordinator 在新根重放建议或重搜。双方结束回合及胜利结算走原生路径。离线宿主在 pinned MVID 下只替换出牌动画时钟、洗牌画面帧间隔、内存档位的路径查询和无场景的鼠标提示清理，异步原生异常转回宿主。保留原生牌堆、伤害、洗牌 RNG、钩子及结算。使用生产观察类配合确定性时钟检验版本拒绝，分离于真实 Controller 和网络；调度与耗时数据来自实际 Coordinator 回调。

60 HP Fuzzy Wurm 双人固定闭环，旧实验丢失第二回合的胜利路线，从第 3 回合零损失退到第 4 回合损失 1。增加即时进攻代表后，同 DLL、同根及同预算对照均第 3 回合零损失获胜。实际出牌序列、逐轮 HP、最终洗牌计数一致，双方各拒绝三个已被队友动作作废的版本。总展开 350→284，其中旧结果对应的展开 216→168，刷新 134→116。第 2 回合刷新约 85→73ms，其他两轮接近，单次样本只作逐例证据。旧结果仍可提供重放建议，展开量单列而非全算浪费。Release 与宿主构建均 0 警告/0 错误，DLL SHA256 `0C4859082D56CBCF209E74A62BE62016542BDE4D2F184DFB457331B68EF3E05D`。证据见 `.local/multiplayer-search-plan/p3-native/redecision-offense/comparison.json`。

P4 先发现多人实际战损排序仍用了旧的战略 HP 剪枝。首回合 Hemokinesis 获胜并由 Burning Blood 补血，战略缺口为零，导致下一回合的零实际战损胜利被剪掉。本地核心多人现绕过该战略界，单人沿用原界。修复后重跑 P3 原生闭环，双方仍第 3 回合零战损获胜，总展开 350→284。

`UseBoundedWinTempo` 只调整最终审核后的结果选择。当前实验限本地核心单成员 Beam、无可搜药水且无指定成长/遗物/资源回收目标。以同窗口的原排序胜利为固定参照，只有已证实更早的胜利、额外实际战损及末态 HP 差均至多 2，才进入选择。双方保命消耗和用药为零，最大 HP、成长、遗物计数及长期资源值匹配。筛选始终用原参照，逐次替换不会抬高上限。使用同一搜索、模拟和请求预算，早期预览沿用原排序。

`win-tempo` 同根、同预算、同 DLL 的原生对照中，关闭时第 2 回合零战损获胜，开启时第 1 回合获胜并受伤 2、战后回复 2。受控的 3 HP 快胜见证超过上限，结果仍为第 2 回合零战损。原生观测复用 `CombatReplayOutcome` 的 HP 事件账本，记录终局回复前的损失。Release 与宿主构建均 0 警告/0 错误。DLL SHA256 `E0C23C30306A9747D12D4DAB2B1E444062B8FD827D6CBD81D99909552F03DE09`，证据见 `.local/multiplayer-search-plan/p4-win-tempo/final/comparison.json`。净损失和更复杂场景留给 P5。

同回合刷新和独立跨回合 route replay / R1 均使用 `RecoveryBudget`，恢复、失败后的主搜索和审计共用解析时间覆盖配置后的总时间与原子节点上限。R1 探测后只分配剩余 profile，不重新获得整份预算。耗尽时仅保留已证明合规的完整结果，没有合格结果则不追加搜索或部署。R1 转移缓存通过 `finally` 清除正常、异常、取消和截止早退时的引用。审计准备的辅助展开曾扣节点但未进账本，现经 `PrepareAudit` 独立记为 `AuditPreparation`；总诊断数增加可能只是补录，不能直接解释为新增搜索工作。

`MP_PLAN_REFRESH_WORKER` 记录恢复与余量，`MP_PLAN_REFRESH_ACCEPT` 只在普通完成回调通过过期检查后产生。`SEARCH_RECOVERY_BUDGET` 仍只测 Coordinator 内部计算收尾。新 `SEARCH_REQUEST_TIMELINE` 由 Runtime 从请求入口贯穿根屏障/原生动作等待、根捕获、worker 终止、预览产生/提交、正式路线提交、结果接纳和取消/CTS 收尾；等待恢复继续使用同一编号。worker 完成在 Task continuation 采样，包含 GC scope 退出；callback 与 CTS 两个并发收尾者均完成后只输出一次。观测器只保留值，不拥有 live 状态或执行授权，提交给 overlay 不等于已显示像素。

Coordinator 在包括失败/取消的最终清理中，通过可选纯值 `RequestWorkObserver` 回传完整账本一次。未正式接纳/提交路线的请求记录总工作及是否已提交预览，不宣称全部无用；缓存/直接续用不借用旧结果的历史展开量。`SearchRequestObservationChecks` 的 16 项生产观测器/Task 合同覆盖多次延迟、不同发布时点、启动前取消、CTS 清理竞争和重复封存。原生 Coordinator 的 DOP=1/4 观测开关对照保持完整动作、最终状态、质量和逻辑工作；未运行完整 Controller 或真实 UI。

`U0U1PinnedHarness multiplayer-refresh` 在资源账本版本已通过 399 项检查/58 次独立重放，本批未重跑。完整备选与跨回合传递覆盖保留；资源口径新增 23 项/4 次，使用原生版税模型和两个未来续接点核对已兑现成长，再从受控新回合根独立重放。注入旧口径基线时备选因 `alternative_growth_changed` 被拒，恢复到剩余口径后直接获胜，展开 1→0；这是固定夹具，不是普遍提速证据。保命、回复、第三方成长与机会损失的算术合同另列，过去药水历史仍为合成夹具，不冒充原生 EndTurn/Controller。四个旧固定根冷搜和同回合恢复的动作/质量/工作与上一构建一致。

上一批窗口比较的 Release 为 0 警告/0 错误，DLL SHA256 为 `D3A370E1137994D489569DB56C480B6FC8DD4C4DE1EFFCED1F5F34077E16E045`。`multiplayer-window` 通过 26 项检查、13 次独立前缀/完整路线重放、一条指定路线物化及一次完整搜索。保留完整窗口补齐与一步预算耗尽回归；新增部分前沿选择不受输入顺序影响、一步补齐至部分前沿的独立末态核对，以及跨窗口/缺失窗口不能用低战损或高分替换的合同。部分补齐只扣 1 展开/1 转移，窗口仍为未完成最终目标。固定双角色根以单人策略仍在第 9 回合获胜，307 展开、战损 32，独立完整末态一致。证据在 `.local/multiplayer-search-plan/partial-windows/final/`。生产排序 portfolio 82 项通过；E0 消费者构建 0 警告/0 错误，P3 比较工具对不可比较输出明确失败，未运行其整组 A/B。没有全量矩阵、测速或游戏启动；Controller/网络/UI、原生队友闭环和普遍提速仍未验证。

P2 末态校准已修正回合开始效果的重复机会：共用窗口的可出牌阶段表示本回合开始已经结算，`StrategicEffectContext.FutureTurnStarts` 从预计剩余可出牌回合中扣除这一次。恶魔形态只估计后续新增力量；当前力量已在原力量估值中。创造性 AI 和熔炉也只估计未来触发。获胜/死亡末态不再估计战斗能力的未来收益，实际成长、回复和成本仍走原账本。敌人绑定的毒、灾厄及末日进度现由 `EnemyBoundDelayedValue` 在各目标剩余 HP 内封顶，再累加；同一目标上的过量/重叠效果不再冒充对其他目标的进度。它仍是启发式，不能证明减伤、斩杀或药水收益。16 回合启发式上限、搜索深度和预算不变，单人继续完整战斗预测。

上一批五个固定输入的单项 A/B（`fbeb2bac` → `76fc677f`）已确认根、预算相同且路线独立重放一致。单人启动在第 6 回合获胜，战损 6、226 展开；多人启动/保留/延迟/防御的战损分别为 1/0/5/1，展开 88/96/102/89，前后相同。四组动作及末态完全一致；延迟组根估值 100→5，目标改变，末态敌人 HP 分布 29/22→16/35。这些只是三回合内的结果，尾部对照另列。

上述证据在 `.local/multiplayer-search-plan/delayed-value/`。防御正式用例为 6 HP、明确可存活的防御牌组，独立重放防御见证后再检查搜索结果；普通牌组的死亡记录在 `baseline/defense-standard-unproven.json`，其存活可能性未证明。保留牌用例为人工加 Retain 的原生 Strike，非真实卡组覆盖。

本轮统一了战斗内未来估值的终局边界：获胜/死亡后，持续能力、球、潜在启动、保留攻击/手牌、回放、下回合资源、延迟和反应伤害均不再获得未来价值；实际成长、受伤/回复和状态记录不变。仅达到多人三回合边界不会清零这些值，单人目标仍是完整战斗。

诊断可通过 `continueRouteSearchForTesting` 在原 `ReplayContinuationRoute` 后继续同一个完整搜索，前缀逐动作消耗原请求节点预算，未增加搜索引擎或执行能力。该选项默认关闭且限定完整战斗目标；普通路线采用仍仅物化审核后的重放，定向检查确认 0 展开。一节点预算无法重放两次结束回合时明确拒绝，不回退获得新预算。

上一批 Release 的 DLL SHA256 为 `FAD0A1CE2B52D7A33D7C65AFB8B6945452ED082C2BD6C440B1041859A3E05141`。`strategic-tail` 通过 20 项检查、9 次独立原生模型重放及一次预算耗尽续接；单人启动仍第 6 回合获胜，战损 6、226 展开。两条延迟路线在相同根、预算和评价器下固定各自 12 动作前缀并继续完整搜索，均第 7 回合获胜；旧前缀总战损 22/402 展开，新前缀 17/474 展开，完整末态均经独立重放核对。证据在 `.local/multiplayer-search-plan/tail-boundary-final/`，这支持该固定场景的模型内收益，不支持普遍提速或未知队友下的最优性。本批未重复该尾部检查。

整体 P2 对照已从 `39d7df05` 独立导出源码并构建，与当前实现使用相同游戏 DLL、根、预算及实际重放窗口。旧搜索源码未改；适配器只补记旧版缺少的窗口诊断。五组结果中，单人启动第 6 回合获胜，战损 6/226 展开；多人启动、保留、延迟、防御分别为战损 1/0/5/0、展开 88/96/102/89。四组动作和完整末态完全相同；延迟组仅目标分布改变，旧前缀/末态也与前述尾部对照记录一致。固定场景不能证明普遍质量或速度收益。

对照发现并修复了低血量防御的 0→1 损失退步：已到达窗口的实际 HP 为 6，但未出牌的后续威胁投影为 -5；旧窗口筛选误认为没有生存范围，退回只取最高分。现在窗口资格、范围筛选、补齐、预览及 Coordinator 使用实际模拟生存，威胁排序仍保留原口径。修复后回到原零损失路线、89 展开，未增预算；防御夹具现明确检查零损失见证和搜索结果。

当前 Release 与受影响宿主构建均 0 警告/0 错误，DLL SHA256 `3D971827189D94F816E67F983A75EA77A39948C72AB49FB602662CA11A4CC046`。`multiplayer-window` 27 项检查/13 次独立重放通过，保留单人策略第 9 回合完整获胜；修复后五组固定对照均独立重放一致且 live 根未变。证据在 `.local/multiplayer-search-plan/p2-whole/fixed-comparison.json` 及 `fixed/`，失败版本另保留在 `candidate/` 和诊断日志，不覆盖原记录。未运行全量矩阵、重复测速或安装 Mod。

原生离线执行现已由上面的宿主桥接与闭环验证覆盖。真实 Controller、网络归因和 UI 延迟仍为独立证据边界。P5 尚未完成。

## 上游适配

2026-11-05 复查上游主线仍为 `5c773caa`。参考 `6e62cd1b` 完成混沌药水成本适配：分支 COW 区分开局库存、普通获得和混沌生成，生成瓶（含自动消耗/连锁混沌）成本为零，普通补槽恢复付费。来源进入状态/库存键；强制槽位只由原始库存瓶满足，生成龙涎香不追加珍贵药水溢价。最终排序、逐回合费用和续接共用带回合的只读消耗账本，保路比较不再累计已过回合的成本。Smart 准入给合格混沌瓶计入补槽动作容量，并旁路不再成立的“次数×根单瓶价格”剪枝；节点、时间、内存预算不变。来源以冻结请求根为边界：旧计划续用保留原成本，新捕获根把现有瓶视为库存，不写游戏或存档标签。

离线 `entropic-cost` 覆盖 pinned 原生药水工厂序列、Fork/来源键、普通补槽、自动消耗、连锁生成、龙涎香、Smart 准入、完整搜索/独立重放及费用标注。跨回合部分使用原生模型重放、生产标注器和合成保留结果验证续接记账；现有路线采用入口仍拒绝药水，不扩大多人执行能力，也不代表真实 UI/Host/Client 验证。
其中龙涎香在 0.107.1 原生模型库不存在，仅由纯策略合同验证溢价规则；不计作原生效果验证。混沌专项有 41 项检查（含完整 Coordinator 的强制基线/Smart 分层及独立重放），策略合同 24 项。组合入口 24 步、35 组 L1、82 项 portfolio 和 17 个 pinned 模式通过；基础设施符号链接权限子项明确跳过。非混沌固定夹具的质量保持，部分等质路线和逻辑工作随状态键改变而变化，不宣称路线逐项不变或普遍提速。

2026-11-02 对比到 `5c773caaec742bc55588bc9ee21c361326a732f0`（上游 0.50.1 / 游戏 0.111.0），选择性适配卡牌 COW 身份、牌堆移动失效、星费清理、批量变换/选牌顺序、空手触发作用域、终局监听器捕获、格挡溢出、手牌上限身份、动态变量访问桥和根集合稳定性。保留 pinned 0.107.1、本地玩家边界及全 RNG 诊断；未整仓合并。已同步 origin/main 的候选身份修复，并修正相关旧夹具、加入错误版本拒绝和动作列表隔离断言。

状态适配的 Release 与 `upstream-port` 12 组合同、card-choice-identity / continuation-replay / completion-scope / rolling-review / request-hydration / choice-rng / continuation-audit 通过。原生选牌只核对 pinned 字段形状，真实 UI 时序、完整原生效果/执行检查点差分与 Host/Client 未在本轮验证。额外回合/金币/临时属性等原生语义及其余 GC/UI/回放改动仍需独立适配，明细见根 `UPSTREAM.md`。既有 NoGC 恢复与 AdoptRoute 实机未完成项继续保留。

`4.42` 默认接入请求级完整无药胜利见证、同根共享胜利界、有限回复证明及 Smart 无资格分支剪枝。回复证明独立核对本机 `0.107.1` DLL（MVID `97f10687-c306-4798-ab75-8b9f23f34dfb`），只接受封闭的原生卡牌/能力/遗物/药水和 FuzzyWurmCrawler/Nibbit 来源；未知模型、附加效果、第三方原生补丁或风险分支退回完整回复余量。单人 Smart 按实际槽位代价准入并统一机会成本排序，避免小幅省血或早获胜绕过药水代价。既有保留席位新增破盾进攻、安全弃牌续招和延迟斩杀探针；精炼成员只有真实 Runtime 回收检查点时才替代累计分配内存估算。无 Beam/节点/时间/内存扩容。

新数学界旁路成长/遗物目标、被盗资源保护、强制药水、team objective 和多人 raw-loss-first 路线；多人原质量轴及 Smart 准入保留。共享见证仅属于同一请求、同一个根对象，来源必须是收尾后的完整合规胜利，不保留模拟器、旧根或执行授权。未引入上游完整调度器、广泛组件回复证明或成长资源分桶。

`upstream-search` 17 个请求及 17 次完整末态独立重放通过；包含 ABBA、共享界实际命中、第三方补丁拒绝和未知回复回退。人工 100HP/无遗物场景 expanded/transitions 为 520/3634 → 258/1758，两瓶火药场景为 671/9071 → 277/3901，获胜质量一致。此轮开启增量验证，只证明固定夹具工作量减少，不证明普遍耗时收益。药水策略、portfolio 82 项、多人跨回合 86 项、rolling-review / continuation-replay / completion-scope / request-hydration 回归通过；原生执行、UI 和真实 Host/Client 仍未在本轮验证。

## 已完成主线：Rolling Horizon Reuse

实现档案：[`Rolling_Horizon_Reuse_Architecture.md`](Rolling_Horizon_Reuse_Architecture.md)

阶段顺序固定为：

`A → B → C → D → E → F → G → H`

当前状态：

- **A 现状与测量：完成。**
- **B 延迟影响合同：完成。**
- **C 战斗级 R0 转移缓存：完成。**
- **D 新根恢复 R1：完成。**
- **E 前台/后台分离：完成。** E1–E4-A 已闭合；E4-B 在最新 INFESTED_PRISMS / MYTES 实机包中同时覆盖 regression 拒绝、improved 接纳与 Smart Potion 约 10s 降级上限。
- **F 情景预热：F0 影子测量已落地；默认 local-single-core 不依赖它，F1 暂停，保留为显式实验多人预测栈的后续选项。**
- **G SSD 冷存储：延期。** 当前优先验证同场战斗质量/响应，暂不把 SSD 持久化加入默认热路径。
- **H 质量与响应验收：完成。** H1 固定输入 A/B 无质量/固定工作回归；H2 已由真实多人 INFESTED_PRISMS / BYRDONIS 日志闭合高血复用 → 斩杀窗口重算。Rolling Horizon A→H 主计划完成。

`CombatSolver_Quality_First_Next.md` 继续用于坏路线、执行质量和回归定位，但不覆盖 Rolling Horizon 的主阶段顺序。

待实施专项：多人战前预计算与 Boss 通关优先；静态审计和已有代码基础见 [总体架构实现记录 §11](CombatSolver_GPT_Architecture_Plan.md#11-多人战前预计算与-boss-通关优先2026-09-30)。仅完成静态可行性审计；先统一 Boss 质量排序，再验证多人离线恢复，未开放能力或修改相关源码。Showcase 已取消。

### 通用非终局后态缓存：成本分解与首项优化完成，默认关闭

`SearchPolicySnapshot.UseRequestTransitionHydration=true` 时，Coordinator 为同一 local-core 请求创建最多 32 项的普通 PlayCard 后态缓存，冷搜索/兼容成员/药水审计可共享；不依赖 R1。首版仅捕获回合内、完整普通动作前缀不超过 8 张、无 Choice/checkpoint 的非终局转移。首次只留值元数据，第一次重复仍真实模拟并验证，之后才从封存原型 Fork、重新 Snapshot/评分；完整路径、目标、策略、状态/RNG/损失上下文不一致则拒绝，结束/异常/取消后释放。R1 和生产预算/授权保持原合同。

Release、`U0U1PinnedHarness request-hydration` 与 `rolling-review` 通过。Pinned 单人夹具使用 local-core policy 的冷请求 + 两层 Smart Potion 审计得到 17 entries / 14 validated keys / 14 hydration hits / 0 mismatch；完整路线、最终状态、质量、260 expanded / 904 logical transitions 与 cache-off 一致，另覆盖并发 Fork、隔离、容量、严格验证、目标/策略身份及取消释放。不是真实 Host/Client PASS。

已增加请求级线程安全成本统计：构键、索引、父/后态校验、保存、原型 Fork、命中 Fork/Snapshot、真实普通动作 replay 的耗时/分配/次数；仅 `MeasurePhasePerformance=true` 输出到 `RequestTransitionHydration.Performance`。保存包含原型 Fork，真实 replay 包含普通 Fork/Snapshot；各线程阶段是累计耗时，不能相加当墙钟。命中数表示实际跳过的普通 PlayCard 模拟，逻辑转移仍计数。

热点是完整状态校验。未首验的条目现在直接走真实 replay，只在观察结果时完整校验一次父态；首验及碰撞/RNG/路径拒绝合同保持。DOP=1 定向诊断中，药水审计父态校验 59→45 次、1,762,256→1,343,544 bytes，命中仍 14；抽牌审计 40→30 次、1,297,504→974,064 bytes，命中仍 10。无扩容、减预算或裁搜索。

最新常规模式对照：每场景四个独立进程 ABBA，每进程各预热一次 off/on 后测 10 个全新请求，`DOTNET_TieredCompilation=0`、详细计时关闭；未注明的行 DOP=1，下表为进程中位数的中位数。所有请求及进程间根、路线、最终状态、质量、边界和逻辑工作一致。

| Pinned 场景 | off / on 墙钟 | on 耗时变化 | on 分配变化 | 实际跳过模拟 | expanded / logical transitions |
|---|---|---|---|---|---|
| 低重复，关闭 portfolio/药水 | 19.71 / 21.21ms | +7.6% | +11.4% | 0 | 99 / 291 |
| 两层 Smart Potion 审计 | 56.09 / 57.36ms | +2.3% | +8.0% | 14 | 260 / 904 |
| 抽牌 + 低能量药水审计 | 95.91 / 96.70ms | +0.8% | +3.5% | 10 | 334 / 1515 |
| 两层药水审计，DOP=4 | 42.03 / 43.75ms | +4.1% | +8.5% | 14 | 260 / 904 |
| 三敌多段攻击 + 药水审计 | 177.37 / 181.53ms | +2.3% | +6.3% | 32 | 405 / 2466 |
| 三敌多段攻击，DOP=4 | 113.36 / 117.43ms | +3.6% | +5.8% | 26 | 411 / 2472 |

新增 `multi-hit` 为 pinned `EXOSKELETONS_WEAK` 三敌开局，原手牌追加 WHIRLWIND / TWIN_STRIKE，沿用两瓶 Fire Potion、Beam=8 / 600 节点上限和无 Burning Blood 的审计配置；不是自然捕获的实机快照。DOP=1/4 各四进程、合计 80 个完整请求均与各自 cache-off 的根/路线/最终状态/质量/边界/逻辑工作一致，战损均 11；不要求不同 DOP 的工作量相同。三敌样本满 32 项仍未获得净收益，现有范围的扩容/默认启用继续停止。

Release、结构门禁、`request-hydration`（含并发统计、未首验惰性校验和默认关闭诊断）通过；DOP=4 药水审计固定输入也保持完整结果/工作一致。尚未证明净提速，因此继续默认 false，停止扩容和推广，不进入本功能的实机启用验证。当前统计只支持定位成本，不证明真实 Host/Client 或普遍性能。下一候选应先寻找确有昂贵重复转移的自然输入；否则按 [加速实现记录 §12](CombatSolver_GPT_Architecture_Plan.md#12-多人搜索加速2026-10-01) 转向其他已测热点。

复跑：同一 `U0U1PinnedHarness` 的 `request-hydration-benchmark-off/on --fixture low-repeat|shared-audit|draw-repeat|multi-hit --iterations 10 --dop 1 --out .local/...`，成本诊断另加 `--measure`。逐进程按 off/on/on/off 顺序运行，不并发 benchmark。Benchmark 与缓存合同入口分开，仍共用固定策略；JSON 含环境/JIT/预热、GC、根与逐请求状态、结果和分配，并在详细模式保留该请求的现有 `SEARCH_PHASE` 日志。临时证据位于 `.local/request-hydration-cost/`，不作为新增默认门禁。

S0 已补齐全请求成员阶段统计：既有 ledger 收尾点在 worker drain 后提交一次阶段贡献，结果与日志均含 `RequestPhaseMetrics` / `SEARCH_REQUEST_PHASE`。成员标注 baseline / beam refinement / Smart Potion / continuation seed / route replay / novelty；未覆盖的分类保留 `Unclassified`。三敌 DOP=1 捕获 baseline 120/568、两层药水 141/873 与 144/1025，合计 405/2466；DOP=4 为 411/2472。缓存 off/on、计时 off/on 的完整结果和逻辑工作一致，阶段汇总守恒；取消前无工作、DOP=4 yield 后取消均只贡献一次。`Disposed` 是真实提前退出状态，不能记为完成。

2026-10-01 已完成单场真实 Host/Client 成本采集：`daeb135` Release、owned Lab、Client warm-up 后重启、两玩家 local-core、缓存关闭、正常并行配置。初次与用户点击重新计算分别记录 5 个已完成成员（Novelty / baseline / 三层 Smart Potion）、13988/66094 与 15074/70505 expanded/transitions；首次前台 5.073/4.873s，请求墙钟 16.049/15.160s。Graceful 停止两端后，全部 JSON 完整，成员工作与独立 E0 member telemetry 一致，全部阶段耗时/分配/次数逐项守恒。两请求 Novelty 工作不同，不是固定工作 A/B，也不是缓存实机收益或部署正确性 PASS。

第二次请求的线程累计热点：Snapshot 10.620s、Action 9.484s、RoundAdvance 5.173s、Fork 4.842s、Fingerprint 3.950s、Prune 1.795s；阶段有嵌套，不能相加作墙钟。Fork/Action 累计分配约 1.067/1.389GiB，不是峰值内存。辅助枚举/根捕获不在成员统计内，协调开销仅含原有回收统计。原始日志留在该实例，定向核对与汇总在 `.local/request-hydration-cost/live-phase-summary.json`，不新增默认门禁。

退出取证已增加 `RunManager.CleanUp(bool)` Prefix：原生 Run 仍有效时采集 `run_cleanup`，同会话只排队一次；诊断异常保留失败事件且不阻止原生清理。后续 `CompleteCombat` 不再从已释放 Run 补采 `combat_end`，明确记录提前采集或原生状态不可用，不把早先检查点重标成终态。Release/结构检查通过。`c018c8c` owned Lab、Client 预热后重启、两端 Graceful 退出的会话 `2ca5341bebf2401eac2aa5133a657801` 已记录 `RUN_CLEANUP_CHECKPOINT queued=True`、`COMBAT_END_CHECKPOINT_UNAVAILABLE reason=captured_before_run_cleanup` 和 FIFO 收尾 `BUG_REPORT_COMBAT_CACHE_RELEASED checkpoints=6`，无捕获/后台序列化失败。日志及现有 FIFO 合同支持捕获与序列化收尾；本轮 `full=False` 未保留该检查点的导出字节，归档内容与恢复验证仍 UNVERIFIED。

同场两次 cache-off 请求的首个前台为 5.068/5.004s、墙钟 15.489/15.713s，expanded/transitions 为 13848/65287、14427/67712；仍为两玩家 local-core，未执行路线。初次 5 个成员 Completed；手动重算为 3 Completed、1 Canceled、1 Disposed，Smart Potion `stop=deadline`，未将提前退出记为完成。Graceful 后 10989 行 JSON 完整、journal error=0，成员工作与独立 E0 ledger、全部阶段累计量逐项一致；原先仅接受全 Completed 的临时审计不适用于本轮，定向审计保留真实退出状态。结果保留 3 回合投影、预计战损 16、TurnLimit；工作/退出范围与旧构建不同，不是提速 A/B，也不是部署正确性 PASS。临时汇总在 `.local/rng-restore-cost/live-phase-summary.json`，不新增默认门禁。

Snapshot/Fork 首项优化移除了 `PredictionExtensions.Clone/ToRng` 构造器按旧 Counter 的无效推进，随后仍逐字段恢复 Counter 与四个生成器状态字。`U0U1PinnedHarness rng-restore` 覆盖 4 seeds × 5 counters、3840 次后续随机值/状态比较及父 RNG 隔离，另通过 request-hydration 与 rolling-review。Cache-off 固定输入 before/after：普通三敌 DOP=1/4 各四进程 ABBA、每进程预热后 10 请求，176.95→177.57ms / 114.10→112.68ms，尚无稳定明显收益；抽牌 DOP=1 单进程对为 95.23→94.27ms，只作质量检查。

新增 `multi-hit-high-counter` 压力夹具沿用三敌输入、先将真实 Shuffle RNG 推进至 Counter=10000；DOP=1 四进程 ABBA、每进程 10 请求，214.37→176.92ms（-17.5%），404 expanded / 2465 transitions、战损 11。所有旧/新请求的完整根、路线、最终状态、质量、边界及逻辑工作一致，JIT/预热设置同原基准；压力夹具不是自然实机样本，收益不外推到普通计数器。临时对照在 `.local/rng-restore-cost/`。预算、并行度、排序、模拟语义与缓存默认不变；新 Release 实机搜索及退出日志采集已完成，尚无普遍提速证据。Snapshot 剩余成本拆分见下节；退出检查点字节归档/恢复作为独立未验证边界。

### Snapshot 剩余成本拆分：完成，仅增加可选诊断

沿用 `MeasurePhasePerformance` / `SEARCH_REQUEST_PHASE`，新增 `SnapshotEvaluation` 覆盖所有 Snapshot 调用；原 `Snapshot` 只覆盖现有展开计时点，不能混用调用数。内部八个不重叠部分为敌人/覆盖、Fingerprint、ThreatProjection、牌与收益估值、持续效果、未来资源、敌人控制、结果；结果内另测 `SnapshotReachableHand` / `SnapshotConstruction`。未改变评分、键字段、预算、排序、部署及缓存默认。详细计时仍默认关闭；表中为每模式预热后 3 请求的平均线程累计时间，占比以 `SnapshotEvaluation` 为分母，括号中的子阶段不得再与父阶段相加。

| Pinned 输入 | SnapshotEvaluation | Fingerprint（其中 CombatFingerprint） | 手牌可达价值 | 未来资源 | 对象构建 |
|---|---:|---:|---:|---:|---:|
| 两层药水审计，DOP=1 | 13.84ms | 42.8%（16.7%） | 13.2% | 17.1% | 2.1% |
| 抽牌审计，DOP=1 | 24.67ms | 45.0%（16.8%） | 14.7% | 14.2% | 1.9% |
| 三敌多段攻击，DOP=1 | 48.80ms | 42.7%（19.3%） | 12.5% | 11.3% | 1.7% |
| 三敌多段攻击，DOP=4 | 84.40ms | 47.2%（22.2%） | 9.1% | 9.2% | 1.8% |

旧 DLL / 新 DLL 计时关闭 / 新 DLL 计时开启的 DOP=1 三输入，加 DOP=4 三敌的开关对照，合计 33 个请求的完整根、路线、最终状态、质量、边界和逻辑工作逐项一致；不比较跨 DOP 工作量。Harness 检查每成员全部 Snapshot 子阶段调用数、非重叠量上界及 worker 汇总守恒；Release、request-hydration、结构门禁通过。复跑用原 `request-hydration-benchmark-off --fixture shared-audit|draw-repeat|multi-hit --iterations 3 --dop 1|4 --measure`，证据在 `.local/snapshot-cost/`。这是成本诊断，少量顺序样本和详细计时开销不能证明净提速或实机收益。

### 根历史计数预聚合：已实现，新构建实机成本采集完成

`AppendFingerprint` 定位到 calculated history 的重复扫描及捕获 lambda。现在根捕获时按原谓词预聚合生成牌、闪电球、未格挡受击、虚无出牌和抽牌数；Fork 共享只读根计数，本地主行动玩家用值快路径，预测事件仍由各分支累加。未知玩家及非玩家伤害保留原扫描；没有删指纹字段或改变次序。定位用的额外细分计时已移除。

对照基线 `d3109b8`，每输入四个独立进程 before/after/after/before，每进程 off/on 预热后测 10 个 cache-off 请求；详细计时关闭、`DOTNET_TieredCompilation=0`，逐进程校验实际 DLL hash。160 个请求的完整根、路线、最终状态、质量、边界及逻辑工作一致；不比较跨 DOP 工作量。下表为进程中位数的中位数。

| Pinned 输入 | 旧 / 新墙钟 | 耗时变化 | 分配变化 | expanded / transitions |
|---|---:|---:|---:|---:|
| 三敌多段攻击，DOP=1 | 178.76 / 169.69ms | -5.1% | -1.2% | 405 / 2466 |
| 抽牌审计，DOP=1 | 92.66 / 89.86ms | -3.0% | -1.3% | 334 / 1515 |
| 长历史压力，DOP=1 | 59.73 / 52.88ms | -11.5% | -1.3% | 261 / 905 |
| 三敌多段攻击，DOP=4 | 112.44 / 111.71ms | -0.6% | -1.2% | 411 / 2472 |

`history-repeat` 仅在不变棋盘记录 2000 次原生抽牌，是人工压力输入。Benchmark 从已捕获根计时，不包含新增根预聚合成本；普通样本波动与收益不能外推为实时多人净提速。Release、`root-history`（混合原生/预测事件、未知玩家/非玩家回退、Fork 隔离、新根更新）、`request-hydration`、9 项 `rolling-review`、DOP=4 Snapshot 阶段守恒及结构门禁通过。临时对照在 `.local/combat-fingerprint-cost/`，JSON 记录实际 DLL SHA256，不新增默认门禁。

`23366d0` 已完成 owned Lab 新构建采集：Client warm-up 后重启（PID 25724）、Host PID 25212，两玩家 local-core、cache-off，会话 `75cc89df84e6452e9045ae4225c7ad54`。三次请求的首次前台为 5.114 / 5.123 / 4.818s，墙钟为 16.901 / 15.979 / 11.964s，expanded/transitions 为 13965/65618、14563/68591、15103/70177；最后一次为用户点击重算。每请求 5 成员，首次与重算全部 Completed，中间为 3 Completed、1 Canceled、1 Disposed，保留真实状态。两端 Graceful 退出后 16872 行 JSON 完整、journal error=0；独立 E0 成员工作、全部阶段累计量和每成员 Snapshot 分区/内部子阶段守恒通过。退出前捕获 queued=True、FIFO 收尾 checkpoints=6，无捕获/序列化失败；`full=False`，检查点字节归档/恢复仍 UNVERIFIED。临时汇总为 `.local/combat-fingerprint-cost/live-phase-summary.json`。

本轮请求存在不同工作量、系统内存压力及 GC 回退，不是新旧版本固定工作 A/B；没有执行路线。新构建实机收益与部署正确性仍 UNVERIFIED。手动重算 SnapshotEvaluation 累计 11.284s，其中 Fingerprint 3.546s（CombatFingerprint 0.601s）、SnapshotStrategicEffects 3.348s；嵌套阶段不能相加作墙钟。持续效果定位与后续边界见下节。预算、并行度、排序、缓存默认不变；暂不做快照对象池、放宽指纹或扩大后态缓存。

### 持续效果上下文：减少分配，新构建实机成本采集完成

人工 `strategic-repeat` 在原 crawler 输入挂载 Dark Embrace / Corruption / Feel No Pain / Strength，并向抽牌堆添加 6 张牌。临时细分计时定位到上下文构建（19.44 / 22.15ms）；Corruption 已确定技能消耗时，现在跳过无须读取的原生 Exhaust 关键词，小刀复用仍检查牌自身关键词。没有跨 Snapshot 缓存、评分或模拟语义变化；临时细分计时已移除。`StrategicKeywordChecks` 134932 案例比较整个上下文，覆盖全部需求位、技能/固有消耗、小刀、第三方牌及两次评估间修改。

对照 `83d5e2b`，五输入各四进程 ABBA、每进程 off/on 预热后 10 个 cache-off 请求，详细计时关闭、`DOTNET_TieredCompilation=0`，实际 DLL hash 已核对。200 请求的完整根、完整动作值（含 Choice/目标）、最终状态、质量、边界与工作逐项一致。Benchmark 原 `SequenceEqual` 把 Choice 列表按引用比较，现改为完整动作 JSON 值比较，并输出 `fullRoute`，没有放宽路线对照。

| Pinned 输入 | 旧 / 新墙钟 | 耗时变化 | 分配变化 | expanded / transitions |
|---|---:|---:|---:|---:|
| 三敌，DOP=1 | 167.55 / 166.62ms | -0.6% | 约 0% | 405 / 2466 |
| 抽牌，DOP=1 | 90.31 / 91.14ms | +0.9% | 约 0% | 334 / 1515 |
| 持续效果，DOP=1 | 254.21 / 256.86ms | +1.0% | -1.3% | 419 / 2515 |
| 持续效果，DOP=4 | 168.93 / 166.73ms | -1.3% | -1.2% | 419 / 2515 |
| 三敌，DOP=4 | 111.58 / 111.30ms | -0.2% | 约 0% | 411 / 2472 |

仅证明分配减少，尚未证明稳定墙钟提速；人工夹具不替代自然多人输入。Release、标量合同、request-hydration、9 项 rolling-review、持续效果 DOP=4 Snapshot 阶段守恒及结构门禁通过。临时对照在 `.local/strategic-effects-cost/`，不新增默认门禁。

`4ecef09` owned Lab、Client warm-up 后重启（PID 2332）、Host PID 14908，已完成两玩家 local-core/cache-off 会话 `419e223620be4038bbaf4ea153688a35` 的三请求采集。首次前台 5.074 / 5.002 / 5.003s，墙钟 16.377 / 11.839 / 14.955s，expanded/transitions 为 12933/62379、15049/70736、14970/70433；第三次明确记录 `generation=3 reason=Manual`。每请求 5 成员，首次为 3 Completed、1 Canceled、1 Disposed，后两次全 Completed。Graceful 退出两端后 14364 行 JSON 完整、journal error=0；成员工作与独立 E0 ledger、全部阶段累计量及 Snapshot 分区/内部子阶段守恒通过。退出前捕获 queued=True、FIFO checkpoints=6，无捕获/序列化失败；`full=False`，检查点字节归档/恢复仍 UNVERIFIED。临时汇总在 `.local/strategic-effects-cost/live-phase-summary.json`。

三请求均有系统内存压力 GC 回退，工作量不同，不能与上一构建作为净收益 A/B；没有执行路线，实机净收益/部署正确性仍 UNVERIFIED。手动重算 SnapshotEvaluation 累计 9.298s，其中持续效果 3.172s、Fingerprint 2.958s（CombatFingerprint 0.509s）。关键词查询的后续实现与当前下一步见下节；预算、排序和缓存默认保持不变。

### 上下文关键词：复用分支查询，新构建实机成本采集完成

`StrategicEffectContext.Build` 与 `WithExhaustDrawTiming` 现在传入当前 `CombatPredictionState`，复用既有 `PredictedCard.HasKeyword`：无全局修改者时读本地关键词，有修改者时保留原生 Hook 和递归隔离。没有新增跨节点缓存或改动能耗公式。原生 Hex 分支还复现了旧 getter 通过真实 Owner 查询关键词导致的抽牌时机偏差，现改为捕获分支的 Hook；因此此项同时修正状态来源，不能宣称所有场景语义完全未变。临时细分计时已移除。

`StrategicKeywordChecks` 的 134932 个标量案例通过；可选 `--native` 扩展既有 harness，将生产上下文的单关键词查询替换为完整 `GetKeywords` 集合，比较 462 个完整上下文及抽牌时机、840 次关键词查询和 32 个 DOP=4 分支。覆盖 Hex 正向修改/移除、Corruption、可写卡牌、NoDraw/DarkEmbrace 顺序及父/真实状态隔离；264 个上下文与旧原生 getter 一致，198 个 Hex 上下文暴露旧状态来源差异并与捕获分支 Hook 一致。此 oracle 共享标量公式，只验证关键词路径与状态传递；标量公式由独立旧算法合同验证。

对照 `72f5787`，五输入各四进程 ABBA、每进程 off/on 预热后 10 个 cache-off 请求，详细计时关闭、`DOTNET_TieredCompilation=0`，实际 DLL hash 已核对。200 个请求完整根、动作值（含 Choice/目标）、最终状态、质量、边界及工作一致。

| Pinned 输入 | 旧 / 新墙钟 | 耗时变化 | 分配变化 | expanded / transitions |
|---|---:|---:|---:|---:|
| 三敌，DOP=1 | 171.60 / 173.89ms | +1.3% | -0.0% | 405 / 2466 |
| 抽牌，DOP=1 | 94.33 / 90.59ms | -4.0% | -0.0% | 334 / 1515 |
| 持续效果，DOP=1 | 250.92 / 234.19ms | -6.7% | -5.4% | 419 / 2515 |
| 持续效果，DOP=4 | 176.91 / 163.16ms | -7.8% | -5.3% | 419 / 2515 |
| 三敌，DOP=4 | 113.77 / 110.78ms | -2.6% | +0.0% | 411 / 2472 |

持续效果人工输入本轮耗时降 6.7%～7.8%、分配降 5.3%～5.4%，不能推广为自然多人收益。Release、request-hydration、9 项 rolling-review、DOP=4 Snapshot 阶段守恒及结构门禁通过。临时对照在 `.local/strategic-context-cost/`；生成 oracle 仅在显式原生合同构建中编译，不成为生产依赖或默认门禁。

`f47c169` owned Lab、Client warm-up 后重启（PID 8908）、Host PID 8712，已完成两玩家 local-core/cache-off 会话 `7cb3743a52c34105ba550b1ec1c51ec0` 的初次搜索和 `generation=2 reason=Manual` 重算。首次前台 5.303 / 5.004s，墙钟 16.510 / 11.903s，expanded/transitions 为 12510/60868、15040/70621。每请求 5 成员；首次为 3 Completed、1 Canceled、1 Disposed，重算全部 Completed。两端 Graceful 退出后，9606 行 JSON 完整、journal error=0；成员工作与独立 E0 ledger、全部阶段累计量及 Snapshot 分区/内部子阶段守恒通过。退出前捕获 queued=True、FIFO checkpoints=6，无捕获/序列化失败；`full=False`，未验证检查点字节归档/恢复。临时汇总在 `.local/strategic-context-cost/live-phase-summary.json`。

两请求均触发系统内存压力 GC 回退，工作量不同；本轮只验搜索和日志收尾，实机净收益与部署正确性仍 UNVERIFIED。手动重算 SnapshotEvaluation 累计 10.347s，其中 SnapshotStrategicEffects 2.593s、Fingerprint 3.626s（CombatFingerprint 0.679s），嵌套阶段不能相加作墙钟。费用查询的后续修正与当前下一步见下节。

### 上下文能耗：绑定预测分支，新版实机成本采集完成

`StrategicEffectContext.Build` 现在接收当前 simulator，技能/Power 费用复用既有 `GetEnergyCostValueWithModifiers`，关键词继续使用其 State。旧 `GetWithModifiers(All)` 会通过真实 Owner / 牌堆读取费用修改；原生对照复现了 26 次差异。保留 X 的零估值、负值截零和所有标量公式；此项修正评分输入的状态来源，可能改变路线，不是纯等价优化。未扩大缓存或调整预算、并行度、排序规则和部署授权，通用后态缓存仍默认 false。

Release 0 warning / 0 error；134932 个标量案例、462 个原生关键词上下文、840 次关键词查询 / 32 个并行分支通过。费用合同通过 180 次原生费用查询、792 个完整上下文 / 48 个 DOP=4 分支，覆盖免费技能/Power/攻击、Corruption、Veilpiercer、VoidForm、局部费用、X/负费用、牌堆移动、Power 移除/消耗及真实 Owner 漂移；Power/history 指纹、父分支与真实夹具隔离通过。原生 oracle 仅在无头夹具中串行临时挂载克隆牌，结束恢复夹具，不是生产依赖。request-hydration、9 项 rolling-review 与结构门禁通过。

对照 `ab4014e`（源码 DLL 为 `f47c169`），五输入各四个独立进程 ABBA，预热后每进程 10 个 cache-off 请求；关闭详细计时、`DOTNET_TieredCompilation=0`，实际 DLL hash 与根/DOP 均核对。原有全夹具严格比较在持续效果工作量变化处失败，保留失败，另行分类核对：普通三敌 DOP=1/4 和抽牌共 120 请求的完整动作值、最终状态、质量、边界与工作一致；持续效果 DOP=1/4 共 80 请求各自版本内完整结果/工作稳定，但新旧路线不同。

| Pinned 输入 | 旧 / 新 expanded / transitions | 结果与验收 |
|---|---|---|
| 三敌，DOP=1 | 405 / 2466 → 同值 | 墙钟 274.99→260.16ms（-5.4%），分配约不变 |
| 抽牌，DOP=1 | 334 / 1515 → 同值 | 墙钟 144.67→150.48ms（+4.0%），分配约不变 |
| 三敌，DOP=4 | 411 / 2472 → 同值 | 墙钟 217.44→208.14ms（-4.3%），分配约不变 |
| 持续效果，DOP=1/4 | 419 / 2515 → 178 / 1082 | 两版均预测战损 0 的胜利；结束回合 3→2、边界 TurnLimit→None；工作不同，不报告提速百分比 |

计时采用进程中位数的中位数，不证明稳定或实时多人收益。另跑新版持续效果 DOP=4 的 3 个详细请求，成员/阶段和 Snapshot 分区守恒通过；完整所选路线（含 Choice/目标）从原根独立 replay，最终 ContinuationStamp、零战损与第 2 回合胜利一致，真实夹具未改变。临时结果为 `.local/strategic-energy-cost/`，`correctness-summary.json` 保留不同工作范围；不新增默认门禁。

`9579c83` owned Lab、Client warm-up 后重启（PID 30672）、Host PID 3996，已完成两玩家 local-core/cache-off 会话 `8044d9d1835041ceae241df4ece2760a` 的初次搜索及 `generation=2 reason=Manual` 重算。首次前台 5.142 / 5.003s，墙钟 18.511 / 17.363s，expanded/transitions 为 11330/53596、13099/61671。每请求 5 成员，均为 4 Completed / 1 Canceled，Smart Potion `stop=deadline`；手动请求在 Graceful 停止前已完成。两端已正常退出且 owned 进程均 Absent；11899 行 JSON 完整、journal error=0，独立 E0 成员工作、全部阶段及 Snapshot 分区/内部子阶段守恒通过。所选路线的模拟重放无差异；不是实际执行验证。退出前 queued=True、FIFO checkpoints=6，无捕获/序列化失败；`full=False`，检查点字节归档/恢复仍 UNVERIFIED。定向核对与状态在 `.local/strategic-energy-cost/live-phase-summary.json` 和 `live-capture-state.json`。

请求工作及退出范围不同，不能作为新旧固定工作 A/B；未执行路线，新版实机净收益、自然输入质量及部署正确性仍 UNVERIFIED。手动重算 SnapshotEvaluation 累计 7.407s，其中 Fingerprint 3.108s、ProjectedShuffle 1.612s、SnapshotStrategicEffects 1.324s；ProjectedShuffle 包含在 Fingerprint 内，不能相加当墙钟。投影成本拆分及后续实现见下节；S1 专项实机验收与 S4 调度仍待完成。

### ProjectedShuffle：复用卡牌纯值，新版实机成本采集完成

三输入（普通三敌 DOP=1、持续效果 DOP=1/4）各 3 个详细请求定位 CardValue 约占 ProjectedShuffle 累计时间三成。现在仅在投影路径使用 `CardChoiceSupport.CardValue(PredictedCard)`，在既有 PreviewStorage 中复用原生牌自身 Damage / Block / Cards / 类型派生的 double 值。MutablePreview、显式失效、升级及 COW 继续沿用现有缓存失效边界；Fork 只共享不可变 Preview 的元数据，Clone 保持新存储，第三方牌及可外部修改附加模型实时计算。没有修改排序/洗牌、卡牌指纹、位置权重、Math.Round、RNG 消耗或评分公式；不增加独立跨节点表、缓存范围、预算、DOP 或部署授权，通用后态缓存仍 false。临时细分计时已移除。

`projected-shuffle` 合同通过 450 次纯值查询、288 个原生 StableShuffle / 完整指纹 / 加权值 / 全 RNG 对照、32 个冷查询 DOP=4 分支，覆盖重复/空列表、分数值、修改/升级/Clone/COW、可外部修改模型禁用以及父/真实夹具隔离。Release 0 warning / 0 error，CardHookReceiverChecks 78 项、request-hydration、9 项 rolling-review、结构门禁通过；新版持续效果 DOP=4 的 3 个详细请求成员/阶段及 Snapshot 分区守恒、所选路线独立 replay 通过。

对照 `48cf087`（源码 DLL 为 `9579c83`），五输入各四个独立进程 ABBA，每进程 off/on 预热后测 10 个 cache-off 请求；关闭详细计时、`DOTNET_TieredCompilation=0`，实际 DLL hash 已核对。200 个请求完整根、动作值（含 Choice/目标）、最终状态、质量、边界和工作全部一致。下表为进程中位数的中位数。

| Pinned 输入 | 旧 / 新墙钟 | 耗时变化 | expanded / transitions |
|---|---:|---:|---:|
| 三敌，DOP=1 | 187.85 / 182.00ms | -3.1% | 405 / 2466 |
| 抽牌，DOP=1 | 108.67 / 98.72ms | -9.2% | 334 / 1515 |
| 持续效果，DOP=1 | 112.60 / 110.57ms | -1.8% | 178 / 1082 |
| 持续效果，DOP=4 | 83.11 / 83.81ms | +0.8% | 178 / 1082 |
| 三敌，DOP=4 | 159.85 / 155.72ms | -2.6% | 411 / 2472 |

分配增加约 0.003%～0.015%，来自 Preview 元数据；不能把本轮墙钟变化全部归因于 CardValue 或宣称普遍/稳定提速。详细模式下值查询累计时间下降约三至四成，仅支持定位。临时对照在 `.local/projected-shuffle-cost/`；不新增默认门禁。

`3f83a34` owned Lab、Client warm-up 后重启（PID 38128）、Host PID 38188，已完成两玩家 local-core/cache-off 会话 `bd790a7149b846eda3762d10c386779a` 的初次及 `generation=2 reason=Manual` 重算。首次前台 5.065 / 4.486s，墙钟 9.243 / 7.558s，expanded/transitions 为 13380/63029、14395/67309；每请求 5 成员均 Completed，Smart Potion `stop=complete`。两端 Graceful 退出后 2291 行 JSON 完整、journal error=0；独立 E0 工作、全部阶段及 Snapshot 分区/内部子阶段守恒通过，所选路线模拟重放无差异。退出前 queued=True、FIFO checkpoints=6，无捕获/序列化失败；`full=False`，检查点字节归档/恢复仍 UNVERIFIED。临时汇总与状态在 `.local/projected-shuffle-cost/live-phase-summary.json` 和 `live-capture-state.json`。

手动重算 SnapshotEvaluation 累计 6.372s，其中 Fingerprint 2.094s、ProjectedShuffle 1.076s、SnapshotStrategicEffects 1.545s，阶段有嵌套。两请求各记录 `no_gc_starts=3`，上一构建对应采集为 0，工作及成员退出范围也不同；不能把墙钟下降归因于此项优化或当作固定工作 A/B。未执行路线，实机净收益、自然输入质量及部署正确性仍 UNVERIFIED。后续持续效果上下文的同一纯值复用见下节，不扩大后态缓存或修改预算/DOP/GC 配置。

### 持续效果卡牌价值：复用既有纯值，实机成本采集完成

`StrategicEffectContext.Build` 在 simulator 非空时复用前面 ProjectedShuffle 已得到的 `CardValue(PredictedCard)`，只减少 Damage / Block / Cards / 类型的重复读取；无 simulator 的标量/旧原生对照保留未缓存 getter。Ceil、最低值 1、全部聚合字段、关键词和能耗公式不变；沿用已有 Preview/COW 失效及第三方/外部模型回退，不新增缓存或调整预算/DOP/GC/排序/部署授权。

原生上下文 oracle 现将纯值查询替换为未缓存 CardModel 计算，并覆盖预热后分数 BaseValue 修改：134932 个标量案例、462 个完整关键词上下文、180 次费用 / 792 个完整上下文及并行隔离通过。Release 0 warning / 0 error，request-hydration、9 项 rolling-review、结构门禁通过；3 个持续效果 DOP=4 详细请求的阶段/Snapshot 分区守恒及所选路线独立 replay 通过。

对照 `3f83a34`，五输入各四个独立进程 ABBA，off/on 预热后每进程测 10 个 cache-off 请求；详细计时关闭、`DOTNET_TieredCompilation=0`，实际 DLL hash 已核对。200 请求完整根、路线（含 Choice/目标）、最终状态、质量、边界和工作一致；下表为进程中位数的中位数。

| Pinned 输入 | 旧 / 新墙钟 | 耗时变化 | expanded / transitions |
|---|---:|---:|---:|
| 三敌，DOP=1 | 177.31 / 178.61ms | +0.7% | 405 / 2466 |
| 抽牌，DOP=1 | 97.42 / 95.59ms | -1.9% | 334 / 1515 |
| 持续效果，DOP=1 | 98.59 / 96.74ms | -1.9% | 178 / 1082 |
| 持续效果，DOP=4 | 64.90 / 61.96ms | -4.5% | 178 / 1082 |
| 三敌，DOP=4 | 121.53 / 123.98ms | +2.0% | 411 / 2472 |

分配约不变，不能把所有墙钟变化归因于此项复用或外推为稳定/实时收益。临时证据在 `.local/strategic-card-value-cost/`，不新增默认门禁。

`65c57be` owned Lab、Client 预热后重启，两玩家 local-core 的初次/手动重算为 10479/49782、14438/66867 expanded/transitions，首个前台 5.137/5.002s、请求墙钟 18.918/8.503s。初次为 4 Completed / 1 Canceled、药水 stop=deadline；手动为 5 Completed、stop=complete。全部成员工作、阶段累计量和 Snapshot 分区守恒；两端 Graceful 后 6627 行 JSON 完整、journal error=0，手动请求完成早于停止。11 次所选路线模拟回放的逐步标量对照无差异，不代表实际执行或完整后态等价。

手动重算 SnapshotEvaluation 累计 6.578s，其中 Fingerprint 2.202s（嵌套 ProjectedShuffle 1.132s）、SnapshotStrategicEffects 1.467s。两请求 no_gc_starts 为 0/3，工作和退出范围也不同，不能作为固定工作 A/B。退出检查点 queued=True、提前采集标记及 FIFO checkpoints=6 收尾无失败，full=False 的归档/恢复仍 UNVERIFIED。实机净收益、自然输入质量及部署正确性仍 UNVERIFIED，通用后态缓存继续默认 false。

### S1 实机：跨回合能量隔离通过

`cc9bdc0` owned Lab 会话 `118e77086e894e23b13c3ba07a6ebdd8` 已完成 Client 预热重启及双端 Graceful 收尾。实战进入 T3 后 2.122s，旧 T2 搜索成功回放 48 步；T2 EndTurn 后 expected/actual energy 均为 4，3 次 Burning Pact 选牌成功。15 次所选路线回放、283 步已记录 HP/block/energy/stars/hand-count 全部一致，5460 行 JSON 完整、error=0。覆盖上轮 live 回合推进导致能量 4→5 的边界，但不代表完整后态等价或旧 47 步动作逐项复现。

三个请求分别为 13363/61561、8785/39115、10105/49062 expanded/transitions；5/3/5 个成员均 Completed，独立 E0 工作、全部阶段及 Snapshot 分区守恒，全部 footer 早于 Graceful stop。首前台 5.073/5.123/4.340s、墙钟 9.618/7.248/8.842s；输入与工作量不同，不构成提速 A/B。

T1 实际部署 8 张牌、原生 Safe EndTurn 并清除旧授权；T2 因本地 HP 预测 68 / 实际 81，以 `field_changed:hp` 拒绝严格续用，从新根做 2 动作 R1 seed 后搜索。`Reactive Carry B` 与 `Joint Mismatch`（local_state_mismatch）均 PASS。未触发 `MP_LOCAL_XTURN_ROUTE_REPLAY status=accepted`，因此 Targets-only 跳过完整搜索、后续部署及 S1 全项仍 UNVERIFIED。

生产能量查询用两处原生 ModifyMaxEnergy Prefix，只在线程内预测作用域读取分支回合，保留原 Hook 顺序；真实调用不变、无新增 AbstractModel。144 次原生对照、1280 次 DOP=4 查询及 4 次完整 EndTurn、相关回归/Release/结构门禁通过；启动 64 applied / 0 ignored / 0 failed。异常成员记 Faulted、重复 Dispose 只贡献一次。旧实机失败与原生反例保留在 `.local/reroot-runtime-acceptance/`，本轮核对及完整归档在 `.local/turn-energy-runtime-verify/`；归档 CRC、6 组 checkpoint/replay-state/native-state/run-state 完整性通过，恢复仍 UNVERIFIED。

### S1 Targets-only：新根重放及后续部署 PASS

同一生产 DLL 的会话 `d21bb8ccc2c54f9bae6fd186918093cf` 中，T2/T3 敌人 HP 比预测各低 6，local-core 仅以 `non_shuffle_rng_changed:targets` 拒绝严格续用。新根重放分别接受 6/3 动作、5.1/1.7ms、`quality=equivalent_partial`，预计战损均 16；完整请求各只有 1 个 Completed RouteReplay 成员、0 expanded / 6或3 transitions。fresh-search marker 到结果路线 capture 为 371/57ms，包含恢复编排但不包含 GUI 展示；纯 replay 耗时不能当完整响应时间。

T1/T2/T3 分别部署 8/2/2 张本地牌，各有新 request/route 授权、逐动作原生校验、Safe EndTurn 与清除授权。`RouteReplay -MinReplays 2`、`Reactive Carry B -RequestId 1`、`Reactive Carry C` 和 `Joint Mismatch` 均 PASS。T4 旧路线已耗尽，正常重新搜索；保留 2 Completed / 1 Canceled / 2 Disposed，未把提前退出记为完成。4 请求全部成员、阶段及 Snapshot 分区守恒；两端 Graceful 后 6698 行 JSON 完整、error=0，10 次所选路线回放无已记录标量差异。完整归档 CRC 和 6 组四类状态产物通过，恢复仍 UNVERIFIED；证据在 `.local/s1-route-replay-live/`。

此轮证明两次实际跳过完整搜索并正确部署，不是通用后态缓存或固定输入速度 A/B，不代表 S1 全项完成。既有 Joint validator 新增 RouteReplay 模式，按同一日志的请求窗口核对 Targets-only 新根、唯一零展开 replay ledger、评估范围、新路线授权及原生动作/结束回合；合成正例和旧授权、错请求/回合、混合账本、非法质量及缺失证据反例通过。

### S1 目标死亡：截断旧 seed、新根恢复与部署 PASS

同一 DLL 会话 `3217e9e264b54bc5b27e9392354570df`（SCROLLS_OF_BITING_NORMAL）中，T2 敌人集合从 combat ID 2/3/4/5 减为 2/3/4。严格续用拒绝后，R1 同战斗/玩家与 roster subset 准入为 none；10 个旧动作仅重放前 5 个，第 6 个 TAUNT 指向已死亡 ID 5，以 action_unavailable 截断。新根生成独立候选，最终选择 seed 派生的预计完整胜利、12 战损路线；新路线 TAUNT 指向存活 ID 3。T2 实际部署 12 张牌，全部目标属于新集合，Safe EndTurn 与清除新授权完整。Host 未结束 T2 已足够验收此边界，不代表后续胜利。

`TargetDeath`、`Reactive Carry B -RequestId 13`、`Joint Mismatch` 均 PASS；新 validator 从 fresh marker 分别读取搜索代次与路线代次，避免跨战斗后 29/2 被误认为同一计数器。合成 plain/JSON、独立代次、替换敌人、死亡目标、未截断 seed、旧授权及错误账本反例通过，上一 Targets-only 实机日志回归仍 PASS。两个请求为 11256/110414、19256/159254 expanded/transitions；前者 2 Completed / 1 Canceled，后者 4 Completed，独立 E0、全部阶段与 Snapshot 分区守恒。两端 Graceful 后 12660 行 JSON 完整、error=0，6 次所选路线的 97 步已记录标量一致；归档 CRC 与 6 组四类状态产物通过，恢复仍 UNVERIFIED。证据在 `.local/s1-target-death-live/`；本轮仍运行主搜索，不是零展开 replay 或提速证明。

### S1 斩杀窗口：公开伤害失效、新根搜索与部署已核对

同一 DLL 会话 `028f45b262b04d2bb4666f42ba70275c`（FABRICATOR_NORMAL）中，Host 的 MIND_BLAST 使存活敌人 ID 2 从 126/360 降到 113/360，格挡为 0，前后均 `in_lethal_window=true`。Client 本地状态与已记录 RNG 不变；公开打牌同时改变 HC 历史，因此不宣称纯 HP-only stamp 等价。旧路线失效，搜索 generation 11 / route generation 4 从新根产出不同路线；新 request 4 授权部署 6 张牌，逐动作原生校验、Safe EndTurn 和授权清除完整。

定向 journal/native 断言审计通过，两端 Graceful 后 5939 行 JSON 完整、error=0；4 请求全部成员、阶段及 Snapshot 分区守恒，保留实际 Canceled/Disposed。新根请求首前台 5.007s、墙钟 6.741s，不能与不同输入比较为提速。7 次所选路线共 72 步已记录标量一致，归档 CRC 与 6 组四类状态产物通过，恢复仍 UNVERIFIED。证据与审计在 `.local/s1-lethal-window-live/`，journal SHA256 `72448b568e6ec81131b93746bdbc9632b844ea6ad7a6273fe81309b9dce62fdc`。旧 MP2B normal smoke 禁止自动 EndTurn，Reactive Carry B 要求下一回合 fresh search；本样本不满足这两种入口范围，保留其输出，不把它们报为 PASS。

### S1 显式采用：物化候选标记已修复，重复失效实机仍 UNVERIFIED

旧 DLL 会话 `077e0ab339f6473ea08f49ea78a40133`（AEONGLASS）中，T6 点击采用 candidate 1，54ms 后 capture 仍为 SearchCompletion；首次后续 CardGeneration 529→535 在 capture 后 1012ms 才发生。唯一 SEARCH_RNG_REPLAN 在 T5、采用前，没有 SEARCH_RNG_ADOPTION_RECOVERY，因此不验收连续采用失效。T1–T6 共 26 张本地牌、6 次 Safe EndTurn 及授权清除完整，Reactive Carry B（request 3）PASS。两端 Graceful 后 22083 行 JSON 完整、journal error=0，10 请求成员/阶段/Snapshot 分区守恒，48 次路线回放共 767 步已记录标量一致；native 退出有 Godot 资源泄漏提示，本轮无 BUG_REPORT_EXPORTED 标记，归档 CRC/恢复仍 UNVERIFIED。证据在 `.local/s1-explicit-adoption-live/`，journal SHA256 `cdfa22e4ca4f6b62193ed016c3d9fee55bf49c0c40ab7fc26281d9dfdbfb74a7`。

根因是已物化 incumbent 的采用 seed 返回普通完成结果；Coordinator 选择和 Runtime 最终收口现只在显式 AdoptRoute 请求下设置 RouteAdoption，保留屏幕所选身份，不增加部署授权。新增回归在旧 DLL 明确失败，新 DLL 的 continuation-replay、completion-scope、lifecycle/结构检查与 Release（0 warning/error）通过；两次额外 RNG 新根重放保持采用范围、质量、不同路线身份和 live 隔离。DLL SHA256 `6B679A6E3EBE9D9527212ED4543F5946C019517518828F93B09D4FF565B1036E`，当前修复仍须实机复测。

下一步重测显式采用：Host 先开始连续推进共享 RNG，Client 搜索未结束且按钮可用时点击“采用当前路线”，Host 在采用/恢复期间继续生成牌并保持回合；Client 等恢复后执行。要求同一采用意图两次独立失效、各次新根恢复和最终新授权部署；仅采用完成后发生变化不计此专项。预算、DOP、GC、缓存与能力默认不变。

## 阶段 D 当前实现

### R1 新根恢复

- Exact continuation 可直接续用仍满足严格 continuation 合同的旧路线。
- Exact continuation 失败时，可从旧路线捕获当前真实回合的普通本地 PlayCard 前缀作为 R1 seed。
- R1 必须从**新 live root**逐动作 replay；不继承旧 Score、旧 SearchNode、旧 simulator 或部署授权。
- R1 probe 不扣减普通 Beam 的 MaxExpandedNodes / SoftTimeBudget。
- 完整合法胜利只可建立安全 incumbent bound；未形成胜利但合法存活的 probe 只可提供 ordering hint。

### D3.1

Request-local retained-state evaluation shadow 已落地。Exact/path-aware 对照只做验证，不跳过 replay/evaluation。

### D3.2

BeamRankScore 首验后复用已落地。第一次 exact key 命中仍现算并核对；同 key 后续调用才允许复用。冲突/mismatch fail-closed。

### D3.3 / D3.3B / D3.3C

- D3.3：最多保存 32 个 request-local exact 非终局普通 PlayCard 后态；首次 baseline 命中仍真实 replay。
- D3.3B：改为 **per-key fail-closed**。坏 key 只拒绝自身，不再关闭整次请求。
- D3.3C：通过首验的 transition hydration cache 生命周期已延长到同一 request 的 supplemental audits 结束；baseline 和 request-tail 分别输出 telemetry，最终再释放原型 simulator。

D3.3C 已由 THE_OBSCURA NORMAL 实机包 `c20503100021438b8502afb9582cd611` 闭合。第一次 fresh re-root 的 request-tail 为：

```text
validated_keys=1
hydration_hits=1
output_mismatches=0
rejected_keys=0
reuse_disabled=false
```

同包第二次 fresh re-root 出现单 key `output_state_key` mismatch，并由 D3.3B 正确独立拒绝（`hydration_hits=0 / output_mismatches=1 / rejected_keys=1 / reuse_disabled=false`）。因此 exact hydration 已证明既能真实命中，也能在坏 key 上 fail-closed；原 D3.5 retained frontier/subtree 方案保持暂停，不再阻塞阶段 D 收尾。

### D3.4 / D3.4B / D3.4C

这些均为 shadow/诊断，不改变搜索行为：

- D3.4：exact ordered retained frontier 对照。
- D3.4B：frontier miss 按 turn / actionCount / nodeCount 分类。
- D3.4C：同 `turn + actionCount` 的 exact retained-node subset overlap。

完整 fresh re-root 实机已经出现：

```text
R1_FRONTIER_SUBSET
turn=3
action_count=4
probe_nodes=4
baseline_nodes=60
intersection_nodes=0
probe_subset=false
```

因此原 D3.5“恢复 retained frontier/subtree”方案**暂停**。当前证据不足以证明恢复这些节点不会重新注入 baseline 已淘汰候选，不为提高命中率放宽 exact 等价条件。

## 当前 R1 admission 边界

- Exact continuation 仍要求完整 CombatIdentity 严格一致。
- R1 seed admission 允许：同一战斗 seed、同一玩家集合，并且 actual enemy roster 是 expected enemy roster 的子集。这只用于覆盖队友提前击杀/移除敌人后的 fresh re-root。
- 新增/替换敌人、换玩家、换 seed、WorldVersion 未推进、scaling/card constraint 变化仍拒绝 R1 seed。
- R1 replay 中失效动作自然截断/拒绝，失败回到正常 cold search。

## 最新实机证据

### ENTOMANCER ELITE

旧构建实机曾得到：

```text
entries=32
first_validations=2
validated_keys=2
hydration_hits=0
output_mismatches=0
rejected_keys=0
reuse_disabled=false
```

这证明当时瓶颈更像 cache 生命周期/重复访问不足，而不是 key 一致性；D3.3C 因此将已验证 cache 延长到 request tail。该批包早于 D3.3C，不能作为 D3.3C 最终验收。

### DECIMILLIPEDE ELITE

最新测试走的是：

```text
resume_kind=exact_continuation
reused=true
expanded=0
```

并允许 remote enemy HP decrease / shared finished-play drift 的现有兼容续用。该包证明 exact continuation 路径正常，但**没有触发**：

```text
state_mismatch
→ seeded_search
→ R1_REROOT_RECOVERY
```

因此仍不能验收 D3.3C request-tail hydration。

## 当前结论：Rolling Horizon A→H 已完成

### H1：固定输入质量 / 固定工作 A/B — PASS

历史基线 `d745b0018e27f2013f7df580bde99523f1ec12ed` 与 current H1 build
`1d0860ff3161172da006567c689b002deb368270` 在 GitHub Actions run
`36685079529` 上使用同一 pinned STS2 / RitsuLib 0.107.1、DOP=1、相同 Beam/节点/时间预算。
simple / draw_energy / teammate 各 4 个 clean-process ABBA 样本均满足：

- 质量、完整动作路线、expanded nodes、transitions 完全一致。
- 无 TimeLimit 样本。
- final publication 中位数变化：`+0.368% / +0.559% / +1.736%`，均低于 2% 固定工作回归线。

### H2：真实多人连续回合 — PASS

INFESTED_PRISMS_ELITE `164e79ed1a85499592363e80497d26b1` 在同一真实 Host/Client 战斗内覆盖两个必验边界：

- **高血稳定复用**：T4 continuation 验证记录
  `living_enemy_hp_decrease_drift=true allow_living_enemy_hp_decrease=true`；14ms 后
  `SEARCH_REUSED validation=compatible_remote_enemy_hp_and_shared_state`，
  `reason=remote_enemy_hp_decrease resume_kind=exact_continuation`。对应 RESULT 为
  `expanded=0 / total_transitions=0`，没有为普通队友伤害支付一次完整搜索。
  BYRDONIS_ELITE `c4a2e1a74385400caadc99262d4d63f2` 又在 T2/T4 独立出现同类
  `remote_enemy_hp_decrease` continuation reuse，作为交叉证据。
- **斩杀窗口立即敏感**：INFESTED_PRISM 在 T4 从 144→125 HP 时，本地手牌/能量保持不变，
  `in_lethal_window false→true`、`enemy_hp_route_changed=true`、route version `14→15`；
  347ms 后启动 fresh search。随后 125→116 HP 又使 route `15→16`，851ms 后启动第二次 fresh search。
  这两次是斩杀窗口内有意的同回合重算，而不是高血阶段的无意义重算。
- **低血 continuation 不放宽**：T5 continuation 见
  `allow_living_enemy_hp_decrease=false`；预测 17 HP、实机 11 HP 被
  `SEARCH_REUSE_MISS / MP_LOCAL_XTURN_CONTINUATION_REJECTED reason=local_state_mismatch`
  拒绝，随后从新 live root 启动 fresh search，并由 R1 仅建立安全 incumbent/hint。
- T5 新搜索约 345.6ms 出现可刷新候选，约 564.7ms 发布 final；最终仍为 22 战损、
  `combat_ended_turn=6`。当前战斗无 `SEARCH_FAILURE` / `FAIL_CLOSED`。
- 当前战斗总计：1 次 exact continuation reuse、3 次 continuation miss、3 次 R1 recovery、
  显式 `resume_kind=cold_search=0`；共有 6 个 reactive fresh-search 事件，其中包含初始根和
  斩杀窗口内两次同回合重算。该数字保留为后续响应优化基线，不解释成“零重算”。

该实机包产生于 `7f7cc95d` 后、F0 前。到当前 H 收尾 HEAD，负责该行为的
`MultiplayerCombatObjectivePolicy`、`MultiplayerClientProbe` 与 local-core continuation 合同未修改；
`SolverController.SearchLifecycle.cs` 的后续差异只加入 F0 shadow 观察，默认 local-core 的影子采样随后还被关闭。
因此 H2 使用的是与当前生产 gate 等价的真实 Host/Client 证据；H1 另行覆盖 current HEAD 固定输入质量/工作量。

### 主线状态

Rolling Horizon **A、B、C、D、E、H 均完成**。F0 已完成影子测量但 F1 不进入默认 local-core；
G SSD 冷存储按当前产品目标延期。F1/G 作为可选研究项，不再阻塞 A→H 主线关闭。

下一步不再按阶段机械增加算法。后续只根据真实问题包处理可复现的质量/响应问题；目前已知可继续观察的是
斩杀窗口内连续队友伤害可能触发多次同回合 fresh search，但不能通过重新忽略斩杀线来消除。

## 当前未验证边界

- CONSTRUCT_MENAGERIE_NORMAL `52af3616…`：T2 点击采用 candidate 88（27 动作、0 药水、预计战损 19）后，生成 RNG 328→333 使旧根失效，原代码丢弃采用意图再搜约 48s。现保留显式采用动作/质量基线，从新根做既有 128 动作 / 600ms 重放；完整胜利且战损不增、奖励/遗物/保命资源及药水合同通过才保留 RouteAdoption，部分路线仍须原终态证明，失败回退搜索。普通自动零战损门槛不改；重复 RNG 失效继续保留采用意图，不追加执行授权。Release 与 `continuation-replay` 新增采用/质量拒绝/live 隔离检查通过；原包同场 Host/Client 采用恢复待验证。
- SCROLLS_OF_BITING_WEAK `2d81b689…`：T1 收尾修复已在实机包生效（3 回合 / 2 continuation）。T1→T2 队友回合结束后敌人 HP -6 / Targets +1（符合招架盾效果），旧路线新根重放遇 route_boundary 后回退；旧日志缺具体边界，现补充边界与相邻动作诊断。T2→T3 另有钟摆抽牌预测错误：其原生 AfterPlayerTurnStart 抽牌不属于 ModifyHandDraw，却被当作根贡献扣除，计数 0 的根使后续少抽 1 张（烙印）。已去除错误抵扣；`pendulum-draw` 覆盖 3 个根计数 × 3 个未来回合、原生 hook 与 live 隔离。T3 搜索期间队友 CardGeneration 324→328 / CardSelection 37→38 的 fresh replan 仍必要。Release / 定向回归通过；原场当前构建 Host/Client 续用仍待验证。
- SCROLLS_OF_BITING_WEAK `03fa54df…`：T1 队友换牌使共享 CardGeneration `313→319`，敌人与本地手牌不变，触发 RNG-only fresh replan。另有收尾范围混淆：`current_turn_seed` 的 1 回合 / 0 战损 / 敌人 243 HP 挤掉跨回合结果，并误标自动 `CurrentTurnAdoption`。现要求前台后备覆盖完整展示动作及适用评估回合，避免短前缀以眼前战损覆盖长路线；自动收尾保留 SearchCompletion，显式当前回合接管照旧。Release、`completion-scope` 合同通过；原场质量与多人部署仍待 current build 实机验证。
- DEVOTED_SCULPTOR_WEAK `49947939…` / `40e88d94…`：队友招架盾在 T2 结束时格挡 14，额外伤害 6 / Targets RNG +1；不能据 T3 差额认定本地红披风漏算。新增高血 Targets 偏差的新根路线重放（128 动作、600ms 软预算、无 Beam 展开）：完整胜利满足既有采用门槛，或部分路线终点本地完整状态相同、战损不增且敌人仅存活血量下降，才跳过完整搜索。未通过则保留 R1 / 普通搜索；严格续用和部署 RNG 校验未改。队友未来格挡未知，不默认预测招架盾。Release 与 `U0U1PinnedHarness continuation-replay` 通过；仍需当前构建 Host/Client 验证 `MP_LOCAL_XTURN_ROUTE_REPLAY status=accepted` 及部署。
- ENTOMANCER_ELITE `e3fd63e…` 高血重算：T2/T3 仅记录敌人 HP、HC 和 RNG 差异，`allow_living_enemy_hp_decrease=true`；HP/HC 已被兼容判断接受，随后在 RNG 字段拒绝，说明还有非 Shuffle RNG 差异。旧诊断只输出该字段第一个变化的流，不能确定具体流或归因队友/模拟。T4/T5 另有真实手牌/牌堆差异。现增加 `local_core_reject_reason` 并输出全部 RNG 差异，不放宽复用规则；Release、`continuation-audit` 和原 `choice-rng` 通过。下一步用新构建问题包定位具体随机流，再做同根同动作的预测/实机对照；可修模拟误差才修改预测，队友变化继续从新根复演已有 R1 候选，不跳过校验。
- BOWLBUGS_NORMAL `69faf6312f3f49f9b428a0b264a0c9e3`：搜索期间共享生成 RNG `244→249`，旧路线仍预测无色药水选“秘密技法”，实机候选却含“金斧头”。local-core 有效性现保留非 Shuffle RNG；仅 RNG 失效时从新根重算，并保留已请求的执行意图。`U0U1PinnedHarness choice-rng --out <ignored-path>` 的 10 个定向合同及 Release 编译通过；自动选牌恢复仍需当前构建 Host/Client 实机复验。开局遗物选择流程未改。
- E2/E4-B 已由 current build 实机闭合。滚动比较规则的“等战损更早胜利”已有 pinned 合同，但仍缺一份明确跨回合（如 T9 对 T11）current build 实机对照；该项继续作为质量 smoke，不作为 F0 影子测量的行为依赖。
- U5/U6 历史 Host/Client observation → fresh replan 的部分真实多人边界仍不是 pinned replay 可替代的证据。
- GitHub Issue #8：多人 Safe Execute 的 Headbutt / turn-start Choice 仍需 current HEAD Host/Client 复验。
- GitHub Issue #9：Vicious 战略估值修复仍需 comparable current multiplayer root 复验。
- `fix/test-subject-phase-transition-guard` 仍有未并入 `main` 的独立 ContinuationLifecycleGuard 修复；在确认被现有逻辑替代或合入前不删除该分支。

## 仓库维护边界

- 当前工作树只保留生产源码、长期规范、可重跑测试/fixture、当前计划和单一 handoff。
- 日志、问题包、benchmark 结果、一次性审计和阶段流水账不进入当前树。
- 历史事实优先从 Git history 或外部问题包定向恢复。
- 分支清理只删除已并入或明确被新实现取代的历史分支；有独立未审修复的分支保留。
