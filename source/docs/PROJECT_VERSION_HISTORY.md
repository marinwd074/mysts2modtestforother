# 项目版本更新历史

> 这是 **mysts2modtestforother** 自己的迭代版本，不是上游 CombatSolver 的发布版本。  
> 基础代码仍以 CombatSolver 0.40.2 为起点；本表只描述这个 fork 从开始改造后的能力演进。  
> 版本号按里程碑归并，不要求一版对应一个 commit。细节需要时再查对应 Git 历史。

## v0.23 — Ordered Mutation companion 扫描优化
**2026-09-22**

- `TrySelectOrderedMutationSemanticCompanion` 不再先排序/物化全部 distinct outcome；改为 outcome 分组后直接单遍按“最大 semantic distance + quality tie-break”选择。
- coverage round-robin 预先计算最大轮数，去掉每一轮重复的 `families.Any(...)` 扫描。
- outcome 去重、质量比较、distance tie-break 与 round-robin 输出顺序保持不变。

## v0.22 — Ordered Mutation 组内代表选择优化
**2026-09-22**

- 新增稳定单遍 `SelectBestByComparison`，替换 3 处 `GroupBy -> OrderBy -> First`。
- 每个 outcome group 不再为了只取一个代表而构造完整排序；比较器相等时仍保留该组首项。
- 后续组间排序和 semantic-distance 排序保持原实现，因此对外顺序与 tie-break 不变。

## v0.21 — Ordered Mutation 分组扫描优化
**2026-09-22**

- continuation packet quality leader 从完整排序改为在保留 candidates 的同时单遍选最优。
- admission claim coalesce 从 `ToList + 排序 + 多次 Any/Select` 改为单遍同时选择 representative、收集 reason 和聚合布尔标记。
- 原 selected-first、packet comparer、reason 顺序与 exact-tie 首项稳定性保持不变。

## v0.20 — Ordered Mutation 保留分配优化
**2026-09-22**

- ordered-mutation continuation 的 survivor 选择改为单遍扫描，去掉仅为 `Aggregate` 服务的临时 `List`。
- sibling carrier 从 `ToList + OrderBy + ThenBy` 改为稳定单遍最小选择，保留原 RetentionRank / representative tie-break。
- obligation fulfillment 不再为每组创建 members/selected 两个 List 并排序，而是一遍同时维护 overall / already-selected 最优项。
- 所有比较器、优先级和 exact-tie 首项稳定性保持不变；只减少分配与 O(n log n) 排序。

## v0.19 — GC 热路径诊断降噪
**2026-09-22**

- 高频 `GC_LATENCY`、搜索分配预算和 `MEMORY_RECLAIM` 阶段追踪改为详细诊断模式才写入。
- 正常游戏不再为这些日志反复构造长字符串或调用 `Process.Refresh()`/进程内存采样；GC 决策本身完全不变。
- 无人测试、存在 `performance-recording.json`，或设置 `COMBATSOLVER_GC_DIAGNOSTICS=1` 时自动保留完整详细追踪。
- 错误/警告、等待、手动 GC 和最终回收结果日志仍保持默认可见。

## v0.18 — GC Policy 测试接口模块化
**2026-09-22**

- `SearchGcPolicy.cs` 中测试计数、暂停边界和故障注入接口拆到独立 `SearchGcPolicy.TestHooks.cs`。
- 真实 GC 生命周期/回收策略与测试控制面分离，行为不变，降低单文件上下文和后续误改风险。
- `CombatSolver.GcPolicyChecks` 显式编译新的 test-hooks partial，继续验证同一套生产 GC 逻辑。

## v0.17 — 历史报告与快照分配瘦身
**2026-09-22**

- 从当前树移除 86 个 dated performance / strategy / audit / issue / refactoring 历史报告（约 1.1 MB）；需要时从 Git history 恢复。
- 默认文档上下文进一步收敛到当前规范、版本兼容、运行证据和可重跑合同。
- 怪物 root snapshot 对没有 static-int 成员的怪物复用共享空字典，不再为每只怪物分配新的空 `Dictionary`。
- generated testing JSON 加入 `.gitignore`，防止一次性结果重新污染仓库。

## v0.16 — 多人怪物调度热路径收敛
**2026-09-22**

- 63 个 pinned 0.107.1 FanOutSafe move 从 3 套字符串分类函数合并为一次 `MultiplayerTargetMode` switch。
- 每次多人怪物 Move 预测只做一次目标模式分类；支持范围、owner-once 顺序、RNG/Choice 特例均保持不变。
- fanout 合同测试改为直接解析 `move -> target mode`，不再依赖函数布局或代码格式。

## v0.15 — 历史上下文与生成数据清理
**2026-09-22**

- 从当前工作树移除 101 个上游 release notes；本 fork 只保留自己的 `PROJECT_VERSION_HISTORY.md`。
- 移除 38 个一次性 performance / strategy JSON 与 patch（约 1.9 MB）；历史仍可从 Git 恢复。
- `.gitignore` 改为按目录禁止重新跟踪 performance / strategy 生成型 JSON/patch。
- 默认工作树进一步聚焦当前源码、当前规范和可重跑合同，减少 Agent/Codex 误读旧实验结果。

## v0.14 — 怪物目标调度模块化
**2026-09-22**

- 多人怪物不再在主 `MonsterMoveEffects.cs` 内维护完整目标路由；新增独立 `MonsterMoveEffects.MultiplayerTargets.cs`。
- 普通多人效果收敛为“当前玩家集合逐个调用既有单目标逻辑”，共享 preamble 只执行一次。
- 9 个 mixed move 已机械等价拆成 `ApplyPerPlayerTargetEffect` 与 `ApplyOwnerEffectOnce`；执行顺序保持 owner prelude → players → owner once。
- 2 个 RNG 特例和 Knowledge Demon Choice 继续显式处理；63 项 pinned 0.107.1 fanout 清单降级为兼容证明/分类依据，而不是未来扩展的架构模型。
- 已有怪物 HP / MaxHP 直接信任战斗 root snapshot；只有模拟中新生成/孵化怪物才调用游戏原生多人 HP scaling。
- 删除已经完成且会误导上下文的 `NEXT_LOCAL_01071_MONSTER_TARGET_AUDIT.md`。

## v0.13 — 仓库与运行路径瘦身
**2026-09-22**

- 清除旧 Multiplayer Console Fixture 整套 Runtime、参数、fixture 与 validator。
- GM Console 保持独立 test-only 模块，不进入 CombatSolver 正式程序集。
- Mod 初始化不再同步复制全部 Godot 日志；游戏日志改为真正导出问题包时再同步。
- 主项目默认项扫描排除 `docs/**`、`tools/**`、`.local/**`、`outputs/**`。
- 默认上下文改为最小真源，Handoff / Test Matrix / Development Notes 不再继续累积历史流水账。

关键提交：`e081a87e`、`b92a7b6f`、`57d5f714`

## v0.12 — 多人测试工具收口
**2026-09-22**

- 尝试过 CombatSolver 内置 Console Fixture 后确认单端状态注入会污染正式多人证据。
- 改为引入完整 TheBookOfAges / GM Console 作为 test-only 多人测试工具，保留 UI 与原生网络同步实现。
- 工具固定为独立 submodule，可整块删除，不污染正式 Mod。
- 删除已被替代的 Workshop/共享控制台安装脚本。

关键提交：`c0104828`、`1ae62d7d`、`d0f6ce65`

## v0.11 — 可读队友状态与怪物多人语义
**2026-09-22**

- 建立 locally readable teammate state，并坚持 `RootActionPlayers` 只控制本地玩家。
- 队友状态在单次搜索根冻结，真实未来本地回合重新 fingerprint；变化时 Fresh Search。
- 完成 pinned 0.107.1 monster target fanout 审计与实现：可安全 fan-out 的怪物效果多人化，未知 Choice 继续 fail closed。
- 增加 pinned monster static-member guard。
- 修复 Axebot Boot Up 错误读取不存在 `RespawnCount` 的搜索崩溃，用户实机确认恢复。

关键提交：`1a432f3d`、`9115977e`、`b875293a`、`efaa9682`、`7a3f8495`

## v0.10 — Multiplayer Safe Auto
**2026-09-22**

- 增加独立 Multiplayer Safe Auto 开关，只走已授权的 MultiplayerSafeExecute。
- 每个真实本地回合重新建立 SafeExecutionSession / deployment，EndTurn 后旧授权失效。
- Potion、Choice、Replay、teammate-target、未知语义、MultiplayerOnly 默认继续硬停止。
- Host/Client 真实连续 3 个本地回合 Smoke 通过：每回合 fresh request/search，原生出牌与 EndTurn，无第二次人工 Execute。

关键提交：`4275aff5`、`7b9f6349`、`b5f9c477`

## v0.09 — 0.107.1 卡牌与机制校准
**2026-09-21**

- 系统核对 0.107.1 卡牌、机制、硬编码常量与版本差异。
- 修正 Outbreak、Well-Laid Plans、Expect a Fight、Hyperbeam、Expertise、Flanking、Coordinate、Tank 等版本漂移。
- 完成 post-0.107.1 patch-note 卡牌审计。
- 建立 MultiplayerOnly 卡覆盖表和 Stage A/B/C 边界；未验证牌不提前进入 Safe Execute。

关键提交：`8722924d`、`c6cf4c3d`、`e5b6c879`

## v0.08 — 本地跨回合多人规划
**2026-09-20**

- 支持多人模式下只为**本地玩家**规划 T1→T2→T3。
- 保存 continuation，并在真实后续本地回合验证后复用。
- 远端公开状态变化、WorldVersion、目标身份变化等不匹配时拒绝旧 continuation 并 Fresh Search。
- 修复 T3 空推荐、route identity materialization、shuffle boundary 等问题。

关键提交：`04b854c7`、`11aa691e`、`194722a2`、`fc305fa8`

## v0.07 — Reactive Carry 与 Carry Ranking
**2026-09-20**

- Safe EndTurn 后自动进入下一真实本地回合 fresh search。
- 建立 Reactive Carry，不跨回合复用旧授权。
- 增加 Multiplayer Carry Ranking v1：只用可公开/可读威胁做最终 tie-break。
- R1 runtime 已验证；R2 decisive 仍保留为 UNVERIFIED，不阻塞主流程。

关键提交：`017a688d`、`c6d9f393`、`7332222f`、`7e3ea732`

## v0.06 — Multiplayer Safe Execute
**2026-09-20**

- MP-2A：受控单动作 Safe Execute。
- MP-2B：加入 SafeExecutionSession、WorldVersion/revalidation，支持安全连续动作并在远端干扰时中止。
- MP-2C：推广到 bounded N-action，设置动作上限并保持每动作后重新验证。
- 全部执行继续使用原生游戏动作，不建立自定义战斗网络协议。

关键提交：`6ce7de7b`、`bd1b5fe4`、`1daacd13`

## v0.05 — Multiplayer Advisor
**2026-09-19**

- 在 Probe 只读基础上加入显式 opt-in Advisor。
- 收紧本地回合/阶段边界，禁止把队友状态误当成本地可执行状态。
- 建立 Advisor lifecycle、rejoin、remote potion fail-closed 和只读证据。
- 真实 Advisor Smoke 通过。

关键提交：`37e7256f`、`ffad49b4`、`6b4f2bd8`

## v0.04 — Multiplayer Foundation / Phase 0
**2026-09-19**

- 首次系统审计多人可行性和能力边界。
- 建立 Probe 模式、session capability、world invalidation、硬 fingerprint 和 fail-closed 原则。
- 建立隔离 Multiplayer Lab、Host/Client profile、Phase 0 证据与 validator。
- 明确默认只读，不生成队友动作。

关键提交：`f5b63035`、`affe866e`、`3350ad64`、`8b828ebb`

## v0.03 — 模块化与仓库基础治理
**2026-09-18**

- 拆分 Solver search lifecycle、Runtime patch registration 等大职责。
- 建立架构边界检查、版本目标检查和更清晰的 Testing / Runtime / Search / Prediction 分层。
- 第一轮清理历史构建产物、冗余文档和仓库结构，为后续多人化减少耦合。

关键提交：`42259f7c`、`10684a23`、`15468ec7`

## v0.02 — STS2 0.107.1 适配基线
**2026-09-18**

- 加入 0.107.1 本体/Mod 兼容资料并集中目标版本配置。
- 适配 0.107.1 turn setup / precombat 等 API。
- 建立 target-version guard、Compatibility Smoke 和 Full Auto 基线。
- 从此所有版本语义修正以 pinned 0.107.1 DLL 为主要权威。

关键提交：`168bac5d`、`e55b533e`、`93da2944`、`e71e631a`

## v0.01 — 项目改造起点
**2026-09-18**

- 将 CombatSolver 0.40.2 作为本项目基础代码导入。
- 整理已有问题说明、测试资料与本地构建入口。
- 确立目标：先保证单人求解稳定，再逐步做 STS2 0.107.1 兼容和多人模式适配。
- 后续项目内部版本从这里独立编号，不跟随上游 CombatSolver 版本号。

起点提交：`4df37d45`

## 版本维护规则

- 只有出现**新的稳定能力边界或明显架构阶段**才递增本项目版本；普通修 bug 不单独升版。
- 当前源码事实优先级：源码/合同与真实证据 > `CODEX_HANDOFF.md` > 本版本历史。
- 本文件记录“发生过什么”，不承担当前任务交接；当前下一步只写 `CODEX_HANDOFF.md`。
- 旧版本详细 commit、日志和证据按需从 Git history 查询，不把它们重新复制进默认上下文。
