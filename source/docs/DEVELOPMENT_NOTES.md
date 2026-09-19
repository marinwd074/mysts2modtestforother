# CombatSolver 开发笔记（当前索引）

> 本文只保留最近维护批次的变更摘要、当前边界和维护入口；退役快照由 [文档历史索引](history/README.md) 说明并可从 Git 历史恢复。当前架构规范以 [`source/AGENTS.md`](../AGENTS.md) 和 [`ARCHITECTURE.md`](ARCHITECTURE.md) 为准。

## 2026-09-19：多人阶段收口与当前边界

- Vanilla Host + CombatSolver Client 的连接、只读 Probe、远端公开状态和双 Client 对照证据已写入 [`docs/multiplayer/evidence/phase0-matrix-2026-09-19.json`](multiplayer/evidence/phase0-matrix-2026-09-19.json)。MP-0 Core 为 `PASS`；Hardening 因退出/重新加入生命周期证据缺口保持 `INCOMPLETE`，完整矩阵保持 `UNVERIFIED`。
- MP-1 Advisor controlled Smoke 已 `PASS`：fresh `-bbfix` 运行记录 `SEARCH_COMPLETE=5`、`SEARCH_STALE=1`、`FAIL_CLOSED=0`、`SEARCH_FAILURE=0`；Probe `51/51` 为只读且无动作入队/自定义网络包。摘要见 [`mp1-advisor-smoke-2026-09-19.json`](multiplayer/evidence/mp1-advisor-smoke-2026-09-19.json)。
- Runtime 默认 `MultiplayerProbe`；`COMBATSOLVER_MULTIPLAYER_MODE=advisor` 才启用只读 Advisor 入口，`MultiplayerSafeExecute` 仍 blocked。当前边界包括 `SolverPerspective`、local-player-only root contracts、schema v2 Probe 证据和 Lab-only flush 策略；MP-0 lifecycle hardening 仍 incomplete。
- 本次收口以 `source/CombatSolver.json` 为唯一 manifest 来源；已解决问题单、旧适配审计和完整快照不再作为当前入口，历史内容由 Git history 保留。

## 2026-09-18：0.40.2 三份新问题包共因修复

- `AEONGLASS_BOSS` 的跨回合弃牌堆顺序偏移与 `SOUL_NEXUS_ELITE` 的 `VOID` 能量漂移共享 `FUEL` 预测补偿缺少抽牌这一个根因。预测现在按原生顺序先获得能量，再抽 `Cards` 张牌；`VOID` 等抽牌触发效果因此会在同一分支内生效，`FORGOTTEN_RITUAL` 与 `LUMINESCENCE` 仍保持能量专属路径。
- `BYGONE_EFFIGY_ELITE` 的 `SlowPower` 在 v0.107.1 只有 `SlowAmount` 动态变量，`DisplayAmount` 是由原版计算的只读属性。搜索出牌记录和回合清零均移除错误的 `DynamicVars["DisplayAmount"]` 写入，保留 `SlowAmount` 与伤害镜像计数的分支状态。
- Release 与 CompatibilitySmoke 构建均通过（0 errors，保留既有 2 条 `CS9113`）；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=119`，目标版本门禁 `0.107.1/0.107.1/STS2_01071`。0.107.1 `FIRST_TURN` CompatibilitySmoke 写出 20 动作、增量核验开启的通过结果。原始三份问题包的完整实机回放和 `src/Testing` 专用 `SLOW-TURN-RESET-FORK` 重跑未在生产兼容构建中宣称通过，留给用户使用新 DLL 实测。

## 2026-09-18：架构优化 Batch 17——文档事实瘦身与构建产物清理

- `source/AGENTS.md` 顶部移除已完成批次的“当前工作项”；`ARCHITECTURE.md` 将补丁注册所有权改为 `PatchRegistration.cs`，并明确 Harmony 原生参数条件编译是当前兼容边界。
- `DEVELOPMENT_NOTES.md` 与 `TEST_MATRIX.md` 明确为历史证据日志，移除测试矩阵重复的 Batch 8 标题；新增 `docs/history/README.md` 作为当前事实入口与历史证据边界索引。
- 文档改动不改变生产代码、搜索策略、测试输入或发布协议。Release 构建通过（0 errors，保留既有 2 条 `CS9113`）；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=122`；目标版本门禁 `TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- 删除 14 个工具项目的 `bin/obj` 可重建产物，回收约 50.5 MB；游戏本体、活动 Mod/运行数据、`.godot`/`.local` 和被历史报告引用的性能 JSON 均保留。清理对象可由下一次构建恢复。最终 DLL 已输出到 `D:\yingye\MODDEV\ports\upstream-0.107.1\release-0.107.1\CombatSolver.dll`，大小 4,134,912 bytes。

## 2026-09-18：架构优化 Batch 12——BeamRetentionPolicy Cycle partial 拆分

- 将循环 startup/exit portfolio、风险桶代表、探测族比较、票据租约和有界保留选择移至 `CombatBeamSolver.BeamRetentionPolicy.Cycle.cs` 的嵌套 `BeamRetentionPolicy` partial；`CombatBeamSolver.Retention.cs` 只保留剪枝调用、共享风险计算、跨文件桥接和最终票据结算。
- `CombatBeamSolver.CyclePlanning.cs` 继续拥有周期状态推断、族/回合账本、有限观察预算、生命周期证据与动作扩展；未把周期算法、预算或场景模型复制进 retention partial。
- 仅调整物理归属和实例调用限定，保留原候选遍历、风险桶、比较顺序、六条 portfolio 上限、票据租约和清理时序；未引入接口、服务、策略替换或并发路径。
- 验证与限制记录在[Batch 12 报告](performance/beam-retention-policy-cycle-split-20260918.md)。

## 2026-09-18：架构优化 Batch 13——AfterBlockBroken Hook 兼容边界

- 将 `AfterBlockBroken` 在 0.107.1 与旧目标之间变化的原生参数列表移至静态 `Sts2HookCompatibility`；镜像注册只消费兼容 helper，不在 Hook 镜像内保留版本条件分支。
- 只移动 API shape，不改变镜像注册、模型处理、预测效果或分配路径；未引入接口、服务、运行时反射或 per-node 分支。
- 验证与限制记录在[Batch 13 报告](performance/after-block-broken-hook-compatibility-20260918.md)。

## 2026-09-18：架构优化 Batch 14——回合准备补丁目标参数兼容边界

- 将 `SetupPlayerTurn` 与 `RunAutoPrePlayPhase` 的版本相关目标参数数组移至 `Sts2TurnSetupCompatibility`；回合准备补丁保留必须与 Harmony 原生参数形状一致的 Prefix 条件编译，但不再拥有 `CombatTurnState` 类型解析或目标签名分支。
- 仅收敛目标 API shape，保留反射方法、调用参数、补丁顺序和回合准备行为；兼容 helper 仍为静态字段，无搜索节点反射、接口或额外 per-node 分配路径。
- 验证与限制记录在[Batch 14 报告](performance/turn-setup-target-compatibility-20260918.md)。

## 2026-09-18：架构优化 Batch 15——GitHub 架构边界门禁

- 将 `verify-refactor-boundaries.ps1` 加入 `.github/workflows/compatibility.yml` 的 Windows 静态一致性 Job，与目标版本、项目 XML 和 `git diff --check` 同批执行。
- CI 现在会阻止 Search → Runtime/UI/Testing 依赖回流、partial 文件职责漂移、Retention 协调边界退化及兼容 API 边界回退；不改变生产代码、搜索策略或运行时行为。
- 本批次只增强持续集成门禁；本地等价门禁与最终 Release DLL 记录在[Batch 15 报告](performance/architecture-boundary-ci-gate-20260918.md)。

## 2026-09-18：架构优化 Batch 16——Entry 补丁注册职责拆分

- 将 `Entry.Initialize` 中完整的 RitsuLib patcher 创建、补丁注册顺序和必需补丁应用移至 `src/Runtime/PatchRegistration.cs`；`Entry` 只保留一次注册入口与既有 `DisableMod` 失败回调。
- 保留 0.107.1/旧目标条件补丁顺序、RitsuLib 调用和失败语义；不新增 BootstrapService、接口、DI、Service Locator 或运行时行为。
- 验证与限制记录在[Batch 16 报告](performance/entry-patch-registration-split-20260918.md)。

## 2026-09-18：架构优化 Batch 11——BeamRetentionPolicy CrossTurn partial 拆分

- 将跨回合保留的候选族键、投资风险分带、在途/新族代表选择、回退候选、比较器、探测启动和准入判定移至 `CombatBeamSolver.BeamRetentionPolicy.CrossTurn.cs` 的嵌套 `BeamRetentionPolicy` partial。`CombatBeamSolver.Retention.cs` 只保留剪枝阶段调用边界；`CombatBeamSolver.CrossTurnPlanning.cs` 继续拥有跨回合证据传播、stand-pat 基线和语义状态附着，不混入保留排序。
- 仅调整物理归属与外层调用限定，保留原候选遍历、风险分带、比较顺序、探测预算、排名清空和状态清理；未新增接口、服务、策略替换或并发路径。
- 验证与限制记录在[Batch 11 报告](performance/beam-retention-policy-cross-turn-split-20260918.md)。

## 2026-09-18：架构优化 Batch 10——BeamRetentionPolicy Mutation partial 拆分

- 按 P2-3 只移动有序变异的外层记录/工作项类型，以及嵌套 `BeamRetentionPolicy` 的完整 Mutation 方法图：组合碰撞、激活/代表选择、continuation packet、admission/observation、公平调度、lease transition、key-policy 校验。主文件继续保留构造器、共享排序/Beam/Pareto 辅助、`RankBest` 与通用策略；`OrderedMutationRetention.cs` 继续拥有预算常量、lineage/lease ledger、原子 pair、普通回退和最终提交。
- 迁移按源码块逐字核对：类型 152 行、首段协调方法 1,753 行、后续 Mutation 方法图 2,634 行；主文件剩余内容与拆分前完全一致。未新增字段、接口、服务或策略替换，不改变候选顺序、预算、RNG、搜索结果或终局裁决。
- Release 与 CompatibilitySmoke 编译均通过（0 errors，保留既有 2 条 `CS9113`）；目标版本门禁为 `0.107.1/0.107.1/STS2_01071`，结构门禁为 `REFACTOR_BOUNDARIES_OK search_files=120`。本批次额外私有 `FIRST_TURN` 运行尝试在 180 秒内超时，未作为通过证据；此前同版本首回合 smoke 的通过结果仍只证明启动/加载/首回合链路，生产 DLL 的实际可见游戏验收留给用户。
- 详见[Batch 10 报告](performance/beam-retention-policy-mutation-split-20260918.md)。

## 2026-09-18：架构优化 Batch 9——BeamRetentionPolicy Potion partial 拆分

- 只移动最终政策资格记录/比较、药水配额、药水谱系分组和 `UsesPotion` 到嵌套 `BeamRetentionPolicy` partial 文件；不改调用方、遍历顺序、配额、候选排序、RNG 或终局裁决，也不引入接口/服务/仓储。
- Potion 专属记录与方法逐段等价校验通过；Release 与 CompatibilitySmoke 构建均 0 errors（保留 2 条既有 `CS9113`），结构门禁为 `REFACTOR_BOUNDARIES_OK search_files=119`。详见[Batch 9 报告](performance/beam-retention-policy-potion-split-20260918.md)。

## 2026-09-18：架构优化 Batch 8——BeamRetentionPolicy Choice partial 拆分

- 按 P2-3 只移动路由／回合开始选择的血缘、上下文排序、保留排名和读取辅助到嵌套 `BeamRetentionPolicy` partial 文件；保留原类、字段所有权、方法签名和算法，不新增接口、策略服务或仓储。药水、变异、循环、跨回合和 Pareto 逻辑留在原文件，供后续批次拆分。
- 源码移动块逐段等价校验通过（290 + 171 行）；Release、CompatibilitySmoke 构建均 0 errors；结构门禁为 `REFACTOR_BOUNDARIES_OK search_files=118`。
- 固定 0.107.1 游戏进程 smoke 与 Batch 7 candidate-03 的 `expanded=3528`、`transitions=10156`、route identity、result identity 均一致；本轮单次耗时 2818.126 ms，不作为性能收益结论。详见[Batch 8 报告](performance/beam-retention-policy-choice-split-20260918.md)。

## 2026-09-18：架构优化 Batch 7——Hook 监听槽位热点

- `MirroredHookListenerLayout` 现在只在某个 Hook mask 首次被请求时建立有序位置索引；`HookListenerEnumerable` 按索引读取当前分支的完整监听快照。没有缓存模型引用，不改变重复成员、原生顺序、第三方/动态类型旁路或 `PendingChoice` 停止边界；`All` mask 保留完整顺序扫描路径。
- 该改动针对历史 0.107.1 热点审计中约 1.69 亿次位图槽位检查、约 361 万次成员交出的成本；它只优化监听遍历，不改变 Search 的 Beam、候选顺序、评分、RNG、预算或终局裁决。
- 固定 IRONCLAD/NIBBITS_NORMAL、`COMPAT1071`、Medium/beam 60、DOP1、5000 ms 的 Windows headless A-B-B-A-A-B：六次均为 `expanded=3528`、`transitions=10156`，路线和结果身份逐次一致。baseline 平均 `3169.203 ms / 370,614,600 B`，candidate 平均 `3145.065 ms / 370,677,333 B`，耗时约 `-0.762%`，分配约 `+0.017%`；后者视为噪声范围，不宣称分配收益。GC、>50/100 ms 帧均未出现新的异常尾部。首回合 0.107.1 smoke 另写出 20 动作、增量核验开启的通过结果。详见[Batch 7 报告](performance/compat1071-hook-index-20260918.md)。

## 2026-09-18：架构优化 Batch 6——搜索性能基线

- 在生产程序集保持 `src/Testing` 隔离的前提下，增加 `COMPAT1071_PERFORMANCE_BASELINE` 兼容 smoke。它沿现有 0.107.1 游戏内最小 fixture 调用生产 `CombatSearchCoordinator`，固定 5000 ms、DOP1 和当前 profile/Beam，只采集搜索工作量、分配/GC、主线程帧间隔及路线身份，不修改搜索热路径。
- 3+3 次交错样本、固定 commit/游戏/RitsuLib/fixture/seed/profile/beam/DOP/预算的汇总见 [搜索性能基线](performance/compat1071-search-baseline-20260918.md)。本批次没有 candidate 算法改动，因此第二组只作同二进制 control 重跑，不作加速结论；可见 Steam 帧时间仍需用户实际测试。

## 2026-09-18：架构优化 Batch 5——完整战斗生命周期 smoke

- 新增 `COMPAT1071_FULL_BATTLE` 兼容 smoke：沿用原生回合开始选牌、全自动部署和实际动作执行，将测试夹具驱动到真实 `CombatEnded`，再等待战斗引用释放屏障。
- Windows headless 写出 `PASS: native 0.107.1 full battle reached combat end; setup_turn=2; selected=True; deployed=True; next_turn=2; route_reuse=False; combat_in_progress=false; cleanup=search,deployment,turn_setup,gc; lifecycle=2>1; gc_ends=1; gc_losses=0`。控制器搜索/部署、回合设置会话和 GC/No-GC 活跃状态均清理；外层启动器因专用 smoke 不写常规 result 文件而报告 `exit_code=0` 收尾异常，未将其记为完整 unattended 请求通过。

## 2026-09-18：架构优化 Batch 2——生产与无人测试协议边界

- 生产程序集不再编译完整 `src/Testing/UnattendedTestProtocol.cs` 与测试活动 tracker；`PreCombatForecastWorker` 改用 `src/Runtime/TestingBridge` 下的最小请求、结果和 JSON 路径契约，测试预期字段、fixture 与断言继续留在测试侧。
- Release 构建、5 项 contract tests、结构边界门禁和目标版本门禁通过；代表性 Windows FIRST_TURN 兼容 smoke 写出 `PASS: native 0.107.1 first-turn search; actions=20; incremental verification enabled`。外层启动器随后因专用 smoke 不写常规 result 文件而以 `exit_code=0` 收尾异常，未将其记为完整 unattended 请求通过。

## 2026-09-18：架构优化 Batch 3——Runtime 目录整理

- 只移动文件并保持 namespace 与逻辑不变：问题报告进入 `src/Diagnostics/BugReports`，统计/在线通知进入 `src/Diagnostics/Telemetry`，Replay/Showcase 合并到 `src/Replay`；同步更新结构门禁和两个独立工具项目的源码路径。
- 主程序集 Release + CompatibilitySmoke 构建通过，结构门禁通过；FIRST_TURN smoke 写出同一版本的 20 动作通过结果。两个独立工具项目未执行完整编译，因为本地没有 `project.assets.json`，未擅自执行还原。

## 2026-09-18：架构优化 Batch 4——SolverController SearchLifecycle partial

- 纯移动 `RequestSearch`、搜索 worker 回调/结果发布、root capture barrier 延迟/取消、搜索引用释放和取消生命周期到 `SolverController.SearchLifecycle.cs`；保持 `internal static partial class SolverController`、方法签名、异步顺序、取消行为和字段所有权不变。
- Release + CompatibilitySmoke 构建和结构门禁通过；FIRST_TURN smoke 写出 `PASS: native 0.107.1 first-turn search; actions=20; incremental verification enabled`。专用 smoke 不写常规 result，外层启动器的 `exit_code=0` 收尾仍不记为完整 unattended 请求通过。

## 2026-09-18：架构优化 Batch 4——SolverController Deployment partial

- 纯移动全自动部署、动作执行、原生选择驱动、部署延迟、取消/完成和部署引用释放到 `SolverController.Deployment.cs`；保持 `static partial`、方法签名、动作顺序、取消行为和字段所有权不变。
- Release + CompatibilitySmoke 构建和结构门禁通过；FULLAUTO smoke 写出 `setup_turn=2; selected=True; deployed=True; next_turn=3; route_reuse=True; combat_in_progress=True`。专用 smoke 不写常规 result，外层启动器的 `exit_code=0` 收尾仍不记为完整 unattended 请求通过。

## 2026-09-18：架构优化 Batch 4——SolverController Continuation partial

- 纯移动续用校验、路线采用、回合准备续接预览、重算原因审计、状态差异和手动分歧记录到 `SolverController.Continuation.cs`；保持 `static partial`、方法签名、异步顺序、取消行为和字段所有权不变。
- Release + CompatibilitySmoke 构建和结构门禁通过；FULLAUTO + route reuse smoke 写出 `setup_turn=2; selected=True; deployed=True; next_turn=3; route_reuse=True; combat_in_progress=True`。专用 smoke 不写常规 result，外层启动器的 `exit_code=0` 收尾仍不记为完整 unattended 请求通过。

## 0.40.2：v0.107.1 问题包回归修复（2026-09-18）

- 复核 9 份新问题包：CubeX、Mawler 与 The Kin 的状态差异都指向同一张 `ROCKET_PUNCH`；Phrog 是部署身份中的有效费用变化；Ruby Raiders 是 `JugglingPower` 的旧 Hook 注册；Slimes 是 `RegenPower` 的旧 Hook 注册。
- `RocketPunch.AfterCardGeneratedForCombat` 的原生实现对满足条件的状态牌调用 `EnergyCost.SetUntilPlayed(0)`。镜像不再使用 `AddUntilPlayed(-1)`，避免已有临时／本地费用修饰时与原生费用状态分叉。Ruby Raiders 的 `AfterCardPlayed` 注册、Slimes 的 `v0.107.1` 条件排除和 Phrog 的仅费用键部署窄化保持有效。
- Release 编译通过；仅保留既有 `RegalitePredictionState` 未使用参数 `CS9113` 警告。Windows headless 已验证 0.107.1/RitsuLib 0.6.2/CombatSolver 0.40.2 启动及 60/60 补丁应用，但 CubeX `DeploySolver` 在 120 秒内未产出结果，未将其记为回归通过。

## 0.40.2：多策略路线搜索默认关闭与大战损引导（2026-09-17）

- 「多策略路线搜索（实验）」对新安装保持默认关闭；设置迁移版本提升到 246，但升级时完整保留玩家当前的开启或关闭选择。多宽度路线精炼仍默认开启且没有独立横幅，设置页的开关与状态反馈保持不变。
- 两条战损引导统一改为预计损失至少 8 HP 时显示，并将玩家文案改为「大战损」；7 HP 及以下不显示，避免玩家在前期普通小额掉血时同时收到过多信息。多策略开启引导还要求功能处于关闭状态；点击横幅会永久隐藏，主动开启功能也会视为已经处理该引导。

## 0.40.2：搜索进度改用请求级预算（2026-09-17）

- 修复搜索进度条在第一轮节点增长较快时几乎填满、后续路线精炼、药水审计或追加搜索仍在继续却长期停在末端的问题。进度现在按整次请求已经消耗的时间预算推进，不再把某一个子搜索的节点上限当作整次计算的总工作量；世界线计数、搜索预算、路线质量与停止条件均不变。
- 搜索仍在运行时进度最多显示到 95%，为软时间边界后的当前批次排空和最终候选复核保留明确余量；完成后继续切换到原结果状态。

## 0.40.2：变形池根快照缓存（2026-09-17）

- 新增根级变形候选池快照 `RootCombatTransformationPoolSnapshot`，按 `(player, cardPool)` 缓存 `CardPoolModel.GetUnlockedCards` 的**未过滤原始序列**（保持上游顺序与实例身份），并在 `Fork` 间不可变共享（`SimulatedCombatState._rootTransformationPools`）。缓存只覆盖玩家角色池与规范无色池；可变池、非规范池、外来玩家或外来约束一律回退上游路径，不做猜测。所有进入缓存的卡都要求原版程序集、非 mutable 且与 `CanonicalInstance` 同一实例。
- `TurnStartChoiceSupport.ResolveCapturedChoice` 的 `Transform` 分支改走 `CombatCardGenerationExtensions.CreateRandomCardForTransform`，命中快照时使用 sts2 已有的 options 重载 `CardFactory.CreateRandomCardForTransform(original, options, isInCombat, rng)`。逐分支的稀有度、`CanBeGeneratedInCombat`、`Id != original.Id` 与人数过滤仍由 `CardFactory.GetFilteredTransformationOptions` 执行。
- **RNG 语义不变**：`GetFilteredTransformationOptions` 在选取前一律 `.ToArray()` 物化，两条重载传给 `Rng.NextItem` 的都是 `CardModel[]`，`NextItem` 对数组直接使用否则 `ToArray()`，两者都只消费一次 `NextInt(0, length)`。因此缓存序列与上游同内容同顺序时，RNG 消耗逐字段相同。这是本改动唯一需要严格验证的语义点，已由契约覆盖。
- 该路径此前每个展开节点都重复执行 `GetUnlockedCards` → `FilterThroughEpochs` → `ModelDb.GetId` → `ModelId.SlugifyCategory`（3 次正则 + 文化敏感 `EndsWith` 走 ICU 排序）。本轮之前的 `perf` 采样显示 `FilterThroughEpochs` 覆盖搜索展开样本的 49%、`SlugifyCategory` 26%、`ModelDb.GetId` 46%。
- 职责边界：新增快照属于 Search 的根级只读投影，与既有 `RootCombatCardGenerationPoolSnapshot` 同层；接口在 `ICombatPredictionCardGenerationPoolSnapshot` 上扩展，卡牌生成扩展仍在 Engine。未改动 Runtime、部署编排、UI、设置或发布流程。
- 真实无头 A/B（基线 `41f9478`，同根、`VeryHigh`、beam 48、`--dop 1`、顺序 ABBA）：KAISER_CRAB_BOSS @2000 节点 18.78 秒 → 9.07 秒（2.072 倍）。**加速比随工作量上升**：另造 4 个厚牌组 Boss 根并把预算标定到基线单场 ≥20 秒后，KNOWLEDGE_DEMON_BOSS 54.11→14.76 秒（3.667 倍）、THE_KIN_BOSS 41.78→14.43 秒（2.895 倍）、KAISER_CRAB_BOSS 37.32→14.86 秒（2.511 倍）、THE_INSATIABLE_BOSS 23.63→10.79 秒（2.191 倍）。**基线 >20 秒的 4 个根加速比 2.191–3.667 倍，重场景下收益不缩水**；成因未做采样取证，只作为实测趋势。收益**场景相关**：不走变形路径的提前穷尽根只有 1.041 倍（silent-discard）、1.426 倍（QUEEN_BOSS）。**对照组**实验把同批 Boss 遭遇改用默认薄牌组（不注入厚牌组），三者全部提前穷尽、加速比 0.984 / 1.015 / 0.990 倍，即收益为零（其中略低于 1.0 的是 1–3 秒量级的噪声，不记作退化），确认收益只来自真正执行变形选择的战斗，不能当作全局面板。详见[本轮报告](performance/transform-pool-root-snapshot-20260917.md)。
- **并行度 8**（生产并行度）复测，预算 12000 节点、同根、顺序 ABBA：厚牌组 KAISER_CRAB_BOSS 46.27→17.91 秒（2.583 倍）、KNOWLEDGE_DEMON_BOSS 26.19→10.92 秒（2.398 倍）、THE_KIN_BOSS 34.40→15.80 秒（2.178 倍）、QUEEN_BOSS 7.26→3.89 秒（1.865 倍）、THE_INSATIABLE_BOSS 13.29→7.84 秒（1.696 倍）；薄牌组对照组 1.000 / 0.989 / 0.983 倍。并行度不会让收益消失，结论与 dop 1 一致。
- **DOP 8 不能提供字段级等价性证据**：`compare_results.py` 在 DOP 8 下报 `DIFFERENT`，但差异只有 `roundReplayPrefixCaptures` 与 `executionChoiceReuses` 两个调度相关复用计数器，`route` / `rootState` / `catalog` 全部 0 处不同。决定性证据是**基线自比**在 DOP 8 下同样在这一个计数器上不同（A1 vs A2：7808 vs 7794；B1 vs B2：7802 vs 7799），即取决于哪个 worker 先命中复用缓存，是墙钟调度产物而非决策输出。因此该差异是并行非确定性，不可归因于本次改动；字段级等价性仍以 DOP 1 的 7 根全一致为准。
- 瞬时分配同时减半：每节点总分配 2.06 MB → 1.04 MB。但**峰值工作集约 385 MB → 约 405 MB、峰值托管堆约 157 MB → 约 179 MB，没有改善、反而略升**。本轮只消除了变形路径的重复临时分配，**未触及节点局面的驻留内存**，最初「保存每个节点局面内存太大」的问题本次未处理；峰值上升的成因未取证，不记作结论。
- 等价性用仓库自带 `tools/OfflineSearchHarness/compare_results.py` 对跑，**7 个根全部逐字段一致**（crab@2000 170 字段、KAISER_CRAB_BOSS@6000 242、silent-discard@6000 192、QUEEN_BOSS@6000 152、THE_KIN_BOSS@6000 174、KNOWLEDGE_DEMON_BOSS@6000 212、THE_INSATIABLE_BOSS@6000 234；`mismatched_roots=0`、无 `left_only`/`right_only`），覆盖 `solverMetrics` 非时间/内存字段、选中路线每个动作、根 `ContinuationStamp` 与 `catalogFingerprint`。
- 契约 `TRANSFORMATION-POOL-CACHE` Passed：缓存序列与上游逐实例同序、跨 `Fork` 不可变共享、可变池/外来约束/外来池被拒绝、规范无色池被正确服务、缓存路径与原生路径产出同一张牌且 `CombatCardSelection` 五字段 RNG 状态与完整预测延续状态一致、父模拟与实机根未被改动。初版契约曾因断言无色池必须被拒绝而失败，查明为契约自身错误（无色池是合法回退池），实现无缺陷。
- 未验证：可见 Steam 性能未测，上述倍数只是无头数据；峰值内存成因未取证；收益倍数场景相关（0.99–3.667 倍），不能外推为全局面板。Bash 结构门禁 `tools/verify-refactor-boundaries.sh` 通过（`REFACTOR_BOUNDARIES_OK search_files=114`，退出码 0；增量 1 即本次新增的快照文件）。

## 0.40.1：多策略回合准备选牌修复（2026-09-16）

- 夸克专用发布包完全移除 RitsuLib，不再包含其分发包、解压目录或内部资产 ZIP。统一发布脚本只保留 CombatSolver 最小包内容，并按实际缺口生成不参与 Mod 加载的无压缩填充条目，使文件严格超过 15 MiB；GitHub 最小包和创意工坊内容保持不变。
- 排查日志站 0.40.0 的 12 份 `TurnSetupFailure` 问题包，确认都在多策略路线探索内失败。其中 11 份是在生成跨回合续用戳记时直接从普通战斗根回放，遗漏了已经选择的回合准备牌，因而首个计划动作面对空手牌；另 1 份把准备阶段已经终结的根放入探索队列，再次展开后触发“终结搜索节点不应进入展开阶段”。
- 多策略探索现在与普通 Beam 一样，初始根先按终结状态分流：终结根直接进入完成候选和目标判定，普通根才进入探索队列。跨回合续用回放统一从该路线的准备选牌根开始，完整保留准备选牌后形成的手牌、牌堆和状态；回放异常时同样释放临时模拟器。
- 新增可强制开启多策略搜索的无人测试协议开关和回合准备选牌回归夹具。铁甲战士＋烤手套场景在 5 秒固定预算、DOP2 下通过，保留 `TOASTY_MITTENS` 选牌并完成 3 回合零战损路线。

## 0.40.0：路线精炼、节点预算与多策略搜索（2026-09-16）

- 主界面新增可永久关闭的「多策略路线搜索（实验）」引导横幅，说明追求更优路线的玩家可从「设置 > 性能」手动开启；点击横幅或主动开启功能都会持久化为不再提示。检测到皮皮极速（SpeedX）后的兼容性提醒从综合反馈区拆为独立可点击横幅，点击后同样永久隐藏，不影响计划外重算、异常反馈和新版本提示。
- 多宽度路线精炼改为默认开启；设置迁移版本提升到 244，升级时会把所有旧配置中的精炼开关强制置为开启，避免上一版默认 `false` 被误当作玩家主动关闭。迁移保留性能档、自定义参数、NoGC 与多策略搜索设置；迁移完成后玩家仍可再次关闭。次段成员改由全局剪枝入口显式启用，长期资源、开局通道和终局排序不会因与 Beam 宽度使用相同 limit 而误触发次段取样。
- 四档节点预算统一提高到原来的 5 倍：低档 60,000、中档 120,000、高档 250,000、极高 500,000；时间、Beam 与单节点分支保持原值。自定义节点预算取消 100,000 的配置上限，只保留至少 100 且必须能表示为整数的输入要求。
- 性能页新增默认关闭的「多策略路线搜索（实验）」。开启后在同一请求预算内先探索结构不同的路线，再由当前 Beam 使用余量；结果沿用既有战损、成长、药水与复活排序。无需训练或额外依赖，不改变原动作模拟。
- 新颖性用值类型事实和紧凑事实对记录，同分区父节点只补变化部分；队列上限 2,048 项并及时释放被淘汰模拟图。探索最多半数时间（章节首领四分之一）、5 秒、2,500 节点和总节点四分之一，小预算直接走原搜索。
- 设置、请求冻结、路线缓存、问题包政策与中英界面同步；取消、当前回合接管和已显示路线接管沿用生产边界。仍由 Runtime 管理 GC 与回收续搜。
- 这是质量与成本存在场景差异的可选功能，详细实验、反例及测试见[本轮报告](strategy/bounded-novelty-search-20260916.md)。

## 未发布：录像回放临时费用与充能球恢复（2026-09-15）

- 修复亡灵契约师录像中，临时生成牌的本回合费用和星能费用没有随第一回合状态恢复，导致严格对账把实际 0 费牌重建为基础费用并中止导入的问题。录像导入现在恢复有序的本地费用修正及其清除时机，也恢复临时星能费用；基础费用定义仍与当前游戏严格核对。
- 修复故障机器人录像只替换充能球模型队列、没有同步重建原生球位节点的问题。导入期间在黑屏内同时重建槽位和球节点，使原生“双重释放”等激发动画引用同一个球对象；首个动作不再异常，后续路线也不会因此产生状态失配。
- 修复录像导入调用球位淡出清理后立即布局新球位，导致原生布局取消清理动画、旧球节点脱离管理列表却继续留在画面的问题。黑屏恢复现在即时移除球位容器中的全部旧节点，再建立唯一一套槽位；推球和激发后不会残留重叠的孤儿 UI。
- 录像导入失败现在始终把最初异常写入日志；返回主菜单清理失败另记为清理异常，方便区分真正的导入原因。

## 0.39.0：多宽度路线精炼与 RitsuLib 0.6.0 适配（2026-09-15）

- 修复录像包解压后立即校验同一文件时，独占写句柄尚未释放导致 Windows 报“文件正被另一进程使用”的问题。现在每个协议文件完整写入并关闭句柄后再读取校验；冲突来自同一游戏进程内部，不是额外的游戏进程。
- 录像包的原生状态文件改为不受辅助 Mod 存档补丁影响的规范文本，并移除重载时必然重建的选牌/奖励网络序号；新包继续严格校验，已上传的旧二进制包由已经通过的完整 `replay-state` 对账兼容，避免 BaseLib 等辅助扩展把旧字节误读为非法集合容量。导入失败后的主菜单清理若也失败，会同时保留并显示最初错误，不再被二次异常覆盖。
- 修复可见录像导入在恢复第一回合牌堆时只清除模型、保留旧手牌节点的问题。导入现在先按原生手牌生命周期移除旧节点，再从已严格对账的恢复牌堆按顺序重建手牌节点；接纳预计算路线前明确核对界面与模型引用顺序，避免同一批手牌在画面中重复出现。
- 录像导入改走原生继续游戏的音乐停止、角色转场音效、黑屏淡出和战斗淡入流程，避免主菜单音乐与 Boss 音乐叠播。临时录像战斗结束后，原生终端奖励页的箭头或前进按钮统一调用游戏自带的跑局结束返回入口，经黑屏淡出后回到主菜单，不再进入正常跑图流程。
- 录像库增加服务端收藏状态和独立收藏栏。收藏项不会被同一开战状态的新上传路线替换，也不参与每个“求解器版本＋职业＋Boss”分组的自动清理；普通录像的分组上限从 100 提升为 2000。私用录像 Mod 可直接收藏或取消收藏，并把收藏与普通录像分栏显示。
- 修复录像恢复在原生发牌后调用“移出战斗”可见路径，导致整手牌播放烧毁动画再重新飞回的问题。现在先即时移除旧手牌节点，再静默替换牌堆模型，并在同一帧把恢复手牌放到最终扇形位置；恢复过程不再产生第二轮移牌动画。抽牌、弃牌和消耗堆的按钮计数同时从恢复后的权威牌堆一次同步，避免牌堆内有牌但按钮仍显示 0。
- 合入 PR #94 的可选 Beam 宽度组合实验入口。功能默认关闭；开启后以当前预设为基线，并只在明确的早耗尽和剩余预算条件下尝试比例宽度。此前按实际体验删除的固定 60 Beam 失败恢复保持删除，不随本次合并恢复。
- 性能设置新增“多宽度路线精炼（实验）”。玩家开启后，求解器先运行当前预设的基线搜索；只有基线较快完成、仍有改进空间且节点、时间和内存余量足够时，才依次尝试 `2/3` 与 `3/2` 的 Beam 宽度并选取更优完整路线。首条路线照常显示，后续阶段标明“正在精炼路线”；设置、路线缓存身份与问题包同步记录该开关。
- 适配 RitsuLib 0.6.0 的多程序集版本包：构建改为导入框架提供的兼容程序集与共享程序集引用，无头快照冻结完整版本包，清单最低依赖同步提升到 0.6.0；统一发布脚本的夸克打包前置也改为严格要求完整的 `STS2 RitsuLib 0.6.0.zip`，不回退旧版。RitsuLib 已接管 BaseLib 目标类型的登记与弱缓存，CombatSolver 删除对旧私有查询闭包的重复补丁；该补丁在 0.6.0 中找不到目标并中断初始化，连带造成瞬间出牌补丁和原生弃牌观察补丁未应用。现在初始化可完整应用全部补丁，瞬间模式下生存者的原生弃牌选择能够完成并收束出牌动作。
- 合入 PR #96 的选牌执行续接、生成池与派生工作复用，以及路线行控件复用。搜索预算、动作顺序、路线政策和未知语义的完整回放边界保持不变；详细实现与原 PR 验证记录保留在后续未发布章节。

## 未发布：多宽度路线精炼扩展成员类型（2026-09-16）

- 组合成员从"一个宽度"扩成"宽度 + 排序方式"（`BeamWidthPortfolioMemberSpec`：宽度、是否次段、是否只用基础分）。次段成员的宽度与基线相同，`SolverSearchProfile.SecondRankBand` 置位后，`RankBest` 只在全局剪枝（`limit` 等于 Beam 宽度）里把分数序前 W 位挪到队尾再截断，普通席位因此落在第 W+1 至 2W 位；必保通道置换、边界多样化和药水配额仍按纯分数序的候选池进行。基础分成员的宽度也与基线相同，`SolverSearchProfile.BaseScoreOnly` 置位后 `BeamRankScore` 只返回 `node.Score`，不加九项附加分；终局排序与路线比较不变。默认成员列表变为 `[基线, 基线×2/3, 基线×3/2, 次段 基线, 基础分 基线]`；无人测试显式给出宽度列表时只有宽度成员。开关仍默认关闭，两个标志未置位时保留逻辑与排序逐位不变。
- 动机：离线定位的 19 个"更优路线被剪掉"的位置里 12 个是分数截断，被剪掉的候选排名都在该层后 30%，次段成员直接搜这一区间；基础分成员去掉附加分对能量、铺垫等的偏好。120 根离线批次（口径同 PR #94）：两种成员单独替代基线都不是改善（次段 Very High 净 +125，变好 32 变差 23，Medium −27；基础分 Very High +60，29 / 20，Medium −172）；作为组合成员次段 Very High +222（21 / 2）、Medium +113（12 / 2），基础分 Very High +148（23 / 3）、Medium +113（13 / 3）；叠加：Very High 90+200 的 +276 → 加次段 +330 → 再加基础分 +371，Medium 16+36 的 +314 → +380 → +432。试过的四种排序变体只有"做减法"的两种有效；去能量加分的变体单独 +184 / +109，但叠在次段之上只剩 +14 / 0，不进默认列表；改持续效果口径与增加铺垫项的两种无效。另外两种取段方式收益不高于整段或有超时根，未采用。
- 成本：两种成员都不改变单节点的模拟与评分成本，展开数为基线的 0.92 到 1.10 倍（次段）与 0.91 到 1.03 倍（基础分）；生产路径上它们是首轮之后按顺序多跑的搜索，经过同一套门控，不超过配置的时间与节点上限，峰值内存取各成员最大值。基础分成员在 Very High 120 根里有两根（IRONCLAD-BOSS-03、SILENT-ELITE-10）搜索时间超过 600 秒（基线 328 s / 127 s），由剩余预算截断。成员明细与 `solverMetrics.portfolioMembers` 增加 `secondRankBand`、`baseScoreOnly` 字段，`BEAM_WIDTH_PORTFOLIO_MEMBER` 日志同步。
- 验证：PR 分支 DLL 两个标志都不置位时，与 0.39.0 main 在 5 根上 61 项指标、全部动作与根戳记逐字段相同。同一 DLL 上 120 根每 4 根取 1 的 30 根开关对照：次段作为成员 Very High 净 +51（5 / 0，1 根死转活）、Medium +21（4 / 1，1 根死转活）；基础分作为成员 Very High 净 +41（4 / 0，1 根死转活）、Medium +86（9 / 0，1 根死转活）；与研究分支 DLL 在同一子集上的结果方向一致。离线检查 `BEAM_WIDTH_PORTFOLIO_OK checks=73`，Bash 结构门禁 `search_files=105`，Release 构建零警告零错误。未运行可见 Steam 或 Windows 无人测试；生产路径的时间与内存数据仍以 PR #94 的 A/B 为准。

## 未发布：离线搜索宿主（2026-09-16）

- 新增 `tools/OfflineSearchHarness/`：不启动 Godot，在普通 .NET 9 进程里建出一场战斗、推进到玩家第一回合，再调 `CombatRootSnapshot.Capture` 与 `CombatSearchCoordinator.Solve`（或单次 `CombatBeamSolver`）跑一次固定预算搜索。用途是批量测量搜索量与路线，不做正确性验收；用法、口径、绕过表与限制见 [离线搜索宿主](OFFLINE_SEARCH_HARNESS.md)。
- 为此在 `src/` 加了四处入口，都不改搜索、评分、保留与协调器的任何行为，且在宿主不用它们时游戏内路径与改动前一致：
  - `CombatSolver.csproj` 加 `<InternalsVisibleTo Include="OfflineSearchHarness" />`，宿主对模组本体不做公开化。
  - `UnattendedTestRunner.BeginOfflineSession(OfflineSessionOptions)`：把固定预算、预算毫秒、并行度、宽度组合开关按无人测试请求的同一段映射（`ProtocolHost.ConfigureSearchOverrides`）写进协议主机，返回的作用域释放即还原。
  - `UnattendedTestRunner.OfflineScenarioSession`：建一个不挂在 `NGame` 上的 runner，把 `ScenarioBuilder` 的生成场景注入方法与装备注入静态方法原样转出去；宿主因此不再用反射写私有成员或造未初始化实例。
  - `SolverController.DisplayServerNameProvider`：显示服务器名字的取值口，默认仍直接问 Godot，只有离线进程把它换成固定的 `"headless"`。
- `tools/verify-refactor-boundaries.sh` / `.ps1` 的两条边界声明随之改为 `partial`（`ProtocolHost`、`Writer`），没有新增或删除边界。
- 合并到当前 Windows 主线时补齐离线宿主导入多版本 RitsuLib 引用所需的 `0.111.0` 目标，并让运行期解析器同时查找该版本的兼容程序集与共享程序集；宿主与模组工程现在使用同一完整版本包。

## 未发布：路线界面复用与语言通知修复（2026-09-15）

- 搜索进度重复显示同一行时，按完整动作显示值、嵌套选牌/遗物的本地化身份复用现有胶囊；变化行照常重建，Populate重置部署高亮，状态页清空旧快照，回合指标与Runtime采用路线继续更新。
- 修正同一帧切换语言、创建胶囊再切回时的通知遗漏：仍合并为一次刷新，但不因最终语言等于上次语言而跳过中途新建的控件；退出树照常解除订阅。
- 无头合成单行64次不变刷新：原实现约1.5秒、2.46–3.05MB托管分配，交付源码0.139/0.208毫秒、各48字节。该数字只说明局部控件工作消除，不外推FPS或整搜提速。
- `ROUTE-ROW-REUSE`与中英/繁中本地化合同通过；交付Release零警告/错误，两端结构门禁通过。实验性投影洗牌缓存完成原生牌序及两场8份完整ABBA，路线/工作量相同，但整搜收益不足且蟹战分配/峰值增加，已全部撤回。追加源码不改变Search/Engine/Runtime，实验补丁、数据、失败和边界见[正式PR追加记录](performance/performance-pr-20260915.md)。

## 未发布：完整性能优化正式 PR 收尾（2026-09-15）

- 对当前上游三场12份完整ABBA均通过严格oracle；蟹战耗时−15.95%、分配−15.51%、峰值−1.64%，扩展弃牌耗时−5.42%，携药轻场景−2.25%；后两场分配与峰值均下降。仅限本机无头样本。

- 整合上游 `cd66b1d`（0.38.6）的成长目标、复活消耗与格挡药直插；本批通用分配、生成池和选牌三阶段实现保持，未另提高搜索预算或改写上游策略。
- 合并后卡牌、药水、嵌套动作与首回合准备的严格增量合同、原生跨回合连续选择，以及上游成长目标合同通过。修正上游格挡药夹具4血伤害不满足9血门槛的建局条件，原生部署省9血、T2无伤获胜、计划外重算0。
- Linux无人入口补齐既有格挡药直插断言与两端结构门禁清单。正常Release零警告/错误，两端门禁 `search_files=102`；当前上游整批ABBA、历史证据范围与失败记录见[正式 PR 验收](performance/performance-pr-20260915.md)。大份派生JSON标记为生成证据，便于评审源码，原数据完整保留。

## 未发布：选牌续执行批量实施（2026-09-14）

- 第一阶段将手动自身选择暂停点扩展到41张原版单人卡，共用已捕获的请求/spec，避免探寻打击重新生成随机候选；活动CardPlay格挡计数与非牌堆生成候选通过同一Fork context复制。
- 82普通/升级分支、380个选择、10个代表原生结算，以及真实搜索旧路径/DOP/取消/严格增量合同通过。后续回合/嵌套实现及完整阶段证据见[实施记录](performance/choice-continuation-expansion-implementation-20260914.md)，暂不外推此前三牌的性能收益。

- 第二阶段为9种手动选牌药水共用稳定使用前缀，保留四种生成药水probe和使用后钩子顺序；串行/并行展开共用候选入口。全部41个选择、50次生产分支/再次访问、九种原生完整结算及旧路径/DOP/取消/异常/严格增量合同通过。准备复制与嵌套回退物理Fork独立计数，预算和完整动作保持。

- 第三阶段保存回合来源、抽牌/洗牌、自动与重复子出牌及其外层循环进度，恢复可以再次挂起；普通Fork事务断言保持，同一context复制CardPlay/历史、候选、Power来源及共享死亡集合。未知派发/历史/事务继续原完整回放。
- 生产选择层的全来源92、Mayhem14、Cascade10、后续回合35分支，以及真实首回合/动作/EndTurn搜索、DOP/取消/异常/严格增量、原有限预算耗尽等价合同通过。原生EndTurn连续选牌后与完整预测状态逐字段一致；共享尾部改动后的41卡与9药水回归均通过。完整搜索暴露的已离开牌堆能力牌仍在自动出牌列表中问题已修正，并通过最小原生回归。
- 三场12份完整请求的动作、路线和决策质量一致。原蟹战平均155.954→131.016秒，但逻辑工作量不同，分配+0.81%、峰值+2.68%，只报告实际耗时变化；扩展弃牌耗时+2.84%（约0.12秒）、分配−1.83%，携药轻场景耗时基本持平。按用户要求，在未见决策退化或具体bug后停止继续归因，保留全部差异，详见[完整报告](performance/choice-continuation-expansion-implementation-20260914.md)。
- 最终正常Release、原生39动作完整部署通过：T1无伤获胜、计划外重算0。最终DLL和manifest已更新本地Mod；不提升版本、发包或推送。不外推可见Steam、Windows或未命中药水检查点的性能收益。

## 未发布：自身弃牌续执行正式接入（2026-09-14）

- 后续按用户要求研究其他选牌来源的成批推广。现有公共入口可复用，但格挡、生成候选、回合循环及嵌套执行需要不同状态合同；完整矩阵及扩展顺序见[可行性研究](performance/choice-continuation-expansion-20260914.md)。本批未扩大三牌生产资格。

- 在独立原型后按用户授权接入投掷匕首、杂技、早有准备的手动自身弃牌检查点。串行与并行搜索复用已完成的抽牌/伤害；后续嵌套选择完整回放。保持完整动作、原预算和候选顺序，复杂或第三方事务继续原路径。
- Engine独占显式执行帧与唯一结算尾部；Prediction独占种子/Fork锁；Search的同父选择链或frontier负责持有直到全部作业排空。新增尝试、捕获、复用与额外回退Fork计数，普通Fork断言不放宽。
- 三张牌的完整原生差分、全部选择及兄弟隔离、真实搜索旧路径/DOP对照、取消/异常排空和严格增量搜索均通过。三场共12个最终性能样本完整动作/路线一致；独立弃牌获胜场景平均耗时-3.44%、分配-8.63%、峰值RSS-5.78%，原生部署T1无伤、计划外重算0。原蟹战未命中且基线漂移较大，GC暂停均值增加，不能声称普遍提速；全部数据和限制见[正式整搜报告](performance/choice-continuation-search-20260914.md)。不提升版本、不发包。

## 未发布：选牌暂停与恢复窄原型（2026-09-14）

- 独立实验实现投掷匕首、杂技和早有准备共用的自身弃牌暂停点，优先覆盖杂技／早有准备普通与升级版本。保存明确执行位置与历史身份，从检查点 Fork 独立子分支，复用已完成的抽牌/伤害；选择与出牌尾部继续调用原语义。复杂事务拒绝，后续再次选牌回退完整重放。
- 杂技 9/10 个选择、早有准备 8/36 个选择或组合全部与完整模拟重放对账，含真实洗牌、历史/九条 RNG、兄弟修改与 DOP2；五个普通/升级计时版本分别通过原生完整结算对照。投掷匕首另覆盖取消、异常、释放和拒绝边界。
- 修正旧路径误执行原型诊断的对照污染后，同一实验 DLL 的 8 分支单元耗时：杂技普通/升级减少 34.21%/33.92%，早有准备减少 29.11%/30.16%，投掷匕首减少 45.50%。旧性能数据明确作废并保留；全部样本、分配与受控保留堆口径见[报告](performance/choice-continuation-prototype-20260914.md)。
- 代码与可复跑 builder/runner 仅在 [ChoiceContinuationPrototype](../tools/ChoiceContinuationPrototype/README.md)，正常构建与默认搜索未接入，不改正式搜索预算、策略或第三方登记。整搜收益、真实搜索命中率与峰值未验证；不提升版本、不发包。

## 未发布：蟹战完整搜索延迟继续优化（2026-09-14）

- 完整CPU和回放来源探针发现抽牌洗牌阶段缺失前缀；增加抽牌准备完成、尚未抽牌的安全续接点。只保存已消费的抽牌张数及worker实际观察到的来源提示，并核对分支中对应能力仍有效；完整事务、选择时序、历史、RNG与原抽牌后前缀保持。
- 扩展已核对的原版角色生成候选池，消除回合开始能力反复筛选；可变/自定义池保留原调用次数和惰性过滤，仍逐分支生成独占卡牌。没有降低搜索预算、评分、保路、药水审计或决策质量。
- 同配置完整蟹战本轮平均176.243→154.439秒（-12.37%），峰值27.524→23.574GB；全部300000展开、2414475转移与完整动作/路线严格相同。轻场景反向交错复核及专用短场景的增量峰值反例均保留，逐场口径、整批最初基线和GC暂停见[报告](performance/crab-latency-20260914.md)。
- 最终生成池、洗牌/变牌/延迟抽牌、既有前缀、串并行与取消/失败排空合同通过。两个无明确整搜收益的历史索引原型撤回；高成本执行器留待审核。数字仅限Linux无头，未证明可见卡顿或Windows收益；不提升版本、不发包。

## 未发布：通用分配与重复工作优化（2026-09-14）

- 从上游 `b1674f8`（0.38.2）继续开发；已合入的 PR #90/#92 不重复移植。保留原搜索预算、动作顺序、保路、评分及药水审计。
- 整合研究分支的抽牌后选择前缀学习与九条 RNG 惰性物化。前缀只在当前 lane 实际观察到稳定点后的有效选择后启用，仍属同一父节点；未使用的 RNG 共享完整不可变状态，已有可变实例在 Fork 当时捕获，保留调用方旧引用的隔离。
- 卡牌首次进场检查迁入 wrapper：Fork 继承、Clone 重查，根身份集合只读共享，污染清除/层数变化仍逐次检查。精确冻结跑局监听前缀省去逐项重映射，其他监听及 Power 保持原映射和失效。
- 长期资源值相同的完整候选池省去无消费者的祖先排名暂存；非均匀池保持最高值群组、原排序及排名恢复。原内存预测和回收安全系数保持不变。
- 生成器允许显式 `fixedSearchBudget:false` 测量正常完整请求；NoGC断言可以显式允许已建立后的正常回退。新增[性能研究工具](../tools/PerformanceBenchmarks/README.md)，保存真实进程峰值、完整动作/政策及默认拒绝未知质量字段漂移的对照。
- 语义合同及短预算固定工作量通过；最终候选十个开局24次完整请求中22次严格oracle相同，亡灵契约师女王两次仅总转移少1、动作/路线一致，未作为严格同工作量提速。储君女王平均耗时−27.97%、峰值−17.00%；蟹战四次/版本耗时−18.05%、峰值−9.57%。轻场景与疑点交错复核未见稳定逐场退化，全部样本、反例和取舍见[本批报告](performance/general-allocation-20260914.md)。数字限Linux无头，不外推可见/Windows或整场部署；不改变GC配置、不提升版本或发包，可恢复选牌执行器等高成本方案保留待审。
- 蟹战/静默女王的累计GC暂停均值分别上升48.73%/45.26%，尽管完整计算耗时下降；不宣称GC暂停或实机卡顿改善。

## 当前维护边界

- 版本、分支、性能、发布和验证描述只对其注明的批次负责，不自动升级为当前事实。
- 新的工程规则、职责迁移、版本基线和发布证据分别写入 `source/AGENTS.md`、`ARCHITECTURE.md`、`TEST_MATRIX.md` 和对应专题归档。
- 新增批次应给出验证入口与已知缺口；未重跑的旧结果不能代替当前构建或运行证据。

完整旧版本可由 Git history 恢复；当前历史入口：[文档历史索引](history/README.md)
