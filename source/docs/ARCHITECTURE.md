# CombatSolver 当前架构与职责地图

> 本文是维护者和 coding agent 的当前职责入口；完整历史架构与逐批审计见 [架构历史归档](history/architecture/ARCHITECTURE-2026-09-19.md)。玩家功能说明见根目录 `README.md`，职责迁移时同步更新 Windows/Linux 结构门禁。

## 当前精简分支的局部合同

普通 Power 的 Target 保留原生 null；显式定向入口继续保存传入目标。临时力量复用上游施加入口，首次回调仍先于计数加入，封顶后按修正请求量触发数量回调。Weak/Vulnerable/Frail 的跳过首次持续时间扣减只由各自 Power 保存，影响状态指纹与 ContinuationStamp；其他能力的无效 Skip 元数据不参与该等价性判断。未新增战斗后端或状态存储副本。

## 1. 运行链

### 单一搜索预算与兼容边界

遗物计数策略由 `RelicCounterCatalog` 声明已核对的跨战斗计数，Runtime 过滤总/单项开关与当前持有对象，冻结到 `SearchPolicySnapshot.RelicTargets`。`SimulatedCombatState.RelicCounters` 只投影既有分支状态，`RelicCounterPolicy` 生成范围达标掩码和一次性 HP 额度。快照的 `StrategicHpCredit` 汇总成长与计数额度，终局/中间排序、用药审计及保路共享；真实战损早停仍须同时满足成长、药水、偷窃和已启用的遗物目标。`SolverRelicStrategyPanel` 拥有 UI 输入，Overlay 只接线，Controller 使续用失效并按原自动计算偏好重算。设置导出、归档恢复与磁盘路线键携带同一策略，旧包默认关闭。见 [完整计数清单](relic-counters.md)。

`RelicCounterEvaluation.CounterValues` 按内置遗物顺序保存结束计数的四位数字槽，随结果快照和续用按值保留；`SolverStrategyOutcomeText` 在主线程投影已卡/未达标及成长次数，Overlay 只读取摘要文本。计数显示不新增搜索目标或评分维度。

遗物目标包含优先级，达标优先值用于原 HP 轴之后的路线比较，掩码仍负责 Pareto 和早停。MeatOnTheBone 使用一个半血布尔目标；完整获胜且用户启用时，StateEvaluation 仅补入 HealFor 与 MonotoneHealFor 的差值，沿首领战略价值折算，不在模拟器重复治疗。

开局后续动作探针通过 `ApplyFixedPrefix(seed, prefix)` 构造真实父链，保留前置资源/药水/准备动作的动作数与状态；不得用已经回放前缀的快照伪装成 action_count=0 的根。

`SolverSettings` 将四档或自定义配置解析为一个 `Profile`，主线程冻结到 `SearchPolicySnapshot`。`CombatSearchCoordinator` 的主搜索、药水审计与恢复使用同一套预算维度；`FixedBudget` 只限制无胜利后的预算扩展，测试/API 可显式覆盖时间。Search 不再包含 Short/Deep 配置、枚举、检查点或分段累计统计；两端结构门禁禁止这些符号回流。`SearchRequestWorkTotals` 按请求累计唯一 elapsed 和工作计数。

旧设置的 deep 字段仅在反序列化边界迁移至 search 字段，保存只写新字段；旧归档读取 deepProfile，新的回放政策覆盖文件使用 profile/fixedBudget。旧无人请求的 forceShortSearchOnly 与短/深时间字段在 ProtocolHost 入口转成固定预算；已退休的阶段断言参数不再接受。UI 设置只渲染单列预算。

`CombatDiagnosticJournal` 仍按战斗保留详细诊断，额外向有界进程日志复制 GC/分配预算/主线程帧摘要，供跨战斗关联。高频显示与节点日志不复制。`SearchGcPolicy` 分别记录收集完成后的堆状态和重启 NoGC 后的状态，诊断采样不改变收集策略。

`SmartLayerMemoryForecast` 只决定有证据能改善容量的可选层间回收：完整层超过新区域容量、缺少预测或当前区域新分配低于 max(64 MiB, 区域限额/4)时沿用每批准入。`SearchGcPolicy` 的自动回收请求已确认完成的后台收集；搜索中和搜索后保持同一路径，手动回收继续强制压缩。最新实机 trace 已证明按碎片比例自动压缩会造成数秒停顿，因此碎片比例不再决定自动压缩。算法层不调用 GC。

`NodePoolSignalLifetimePatch` 补齐原版 `NodePool<T>` 递归信号清理的包装所有权：返回的 typed array 通过底层 Array 释放；字典、Variant、从原生转换得到的新 StringName 在作用域退出时释放。节点与 Callable 的目标不属于此作用域，保留原解绑条件；原版 Free 的对象池账本和 OnFreedToPool 保持原调用链。NCard 与 NGridCardHolder 的共享泛型方法分别由真实方法合同覆盖。

可选的 `src/Diagnostics/PerformanceRecording.cs` 是主线程标量采样和状态提示入口，由 Dispatcher 安装；`PerformanceSession.cs` 拥有进程级有界队列、后台文件写入及 OS/GC 采样；`PerformanceLifecycle.cs` 仅计量跑局/房间异步生命周期。节点重建复用同一进程写入器，游戏对象只以弱引用追踪。`tools/watch-performance.ps1` 在独立进程采集 EventPipe 和用户明确触发的 Heap dump；配置文件存在时才启用。诊断不修改搜索政策、GC 模式或第三方行为，详情见 [全程性能录制](performance/long-session-recording.md)。

包装登记探针只捕获 Godot 两个进程级线程安全弱登记容器，后台读取 Count，不遍历目标。watcher 用一个采集器交替运行短 GCHandle 窗口与普通段；GC 关联栈持续保留。补丁清单在同次采集内只解析一次 PatchMethod，避免重复程序集查找。采集完成与解析完整性是不同状态。

```text
Entry / turn hooks
  -> SolverController（主线程会话与请求）
  -> CombatRootSnapshot.Capture（主线程稳定根）
  -> CombatSearchCoordinator（后台主搜索与反事实审计）
  -> CombatBeamSolver（分支搜索）
  -> SolverResult
  -> SolverOverlaySnapshot.Capture（主线程 UI 投影）
  -> Overlay renderer / 原版部署入口
```

版本相关的原生反射签名和补丁目标参数形状集中在 `src/Compatibility/`；当前回合准备入口由
`Sts2TurnSetupCompatibility` 负责，卡牌结果 Hook 由 `Sts2CardHookCompatibility`
负责，`AfterBlockBroken` 参数形状由 `Sts2HookCompatibility` 负责，搜索节点不经过该边界。仍需保留原生
Harmony 参数形状的条件编译位于回合补丁入口；这是当前兼容边界，搜索节点不消费这些原生签名。

搜索 worker 接收 `CombatRootSnapshot`、`SearchPolicySnapshot`、诊断 sink、帧压力信号和取消令牌。它不读取全局设置、控制器、UI 或无人测试状态。

当前多人适配仍处于 MP-0 只读探针阶段。`SolverSessionCapabilities` 是 Runtime 的唯一能力合同：网络多人默认进入 `MultiplayerProbe`，搜索、部署、回合准备接管、选择驱动、药水、Full Auto、Instant、跨回合复用和 Showcase 均关闭；`MultiplayerAdvisor` 与 `MultiplayerSafeExecute` 只声明后续阶段的显式能力，不会根据玩家数或网络类型自动启用。`MultiplayerClientProbe` 只在主线程读取本地玩家与敌方公开状态，`MultiplayerWorldTracker` 只维护观察 fingerprint、world version 和稳定等待窗口，不拥有网络、不修改 live state、不发送动作。

成长策略由 `GrowthBudgets` 随请求冻结，每次实际收益按对应来源取得 HP 额度，中间保路和终局排序沿用同一份额度；成长侧栏只编辑原有额度和忽略收益开关。`CardMechanismFacts` 提供小刀数量、攻击命中与消耗抽牌的纯值估计，`StrategicEffectModel` 消费分支状态；StateEvaluation 的首攻击估值只在原版致命消费者存在或外部战略登记表非空时构建，外部既有字段上下文保持；当前没有奖励／商店评分模块。

普通搜索在 Runtime 同时等待根回收屏障、原生动作队列及当前动作完成后捕获根；队列因等待玩家选择暂时无可执行动作时，当前动作的完成任务仍约束捕获。任何异步等待恢复后都重新进入请求校验，沿用请求身份和战斗生命周期取消；专用回合准备选牌入口先行处理。

同一战斗回合已有计划、活动搜索、部署会话或已完成的部署时，迟到的 AutoTurnStart 在 RequestSearch 入口直接完成。搜索与部署会话分别冻结战斗身份和起始回合；开始部署清空 LatestResult 后由部署会话延续归属，完成后由 LastSolverDeployedTurn 保留。手动重算及下一回合请求继续原流程。

`ICombatPredictionEffectSink.ApplyPowerFromSource` 将原版显式 cardSource 传入分支 Power 施加作用域，null 明确代表能力/遗物自身来源；完成后恢复外层来源。Envenom/Concoct 的附毒使用此入口，UnsettlingLamp 继续只响应卡牌直接施加。作用域存于分支，活动期间禁止 Fork。

## 2. Runtime

| 文件 | 职责 | 不负责 |
|---|---|---|
| `src/Runtime/Entry.cs` | Mod 初始化、战斗与回合生命周期入口、无人请求循环启动 | 补丁注册清单、搜索策略和战斗语义 |
| `src/Runtime/PatchRegistration.cs` | 创建、注册并应用 RitsuLib 必需补丁；保留版本条件与注册顺序 | Mod 生命周期、搜索策略和战斗语义 |
| `src/Runtime/TestingBridge/*` | 生产运行时所需的最小战前协议 DTO、路径/JSON 选项与空闲活动桥；不拥有测试协议 | 测试请求预期字段、fixture、断言、协议 host 和测试 tracker |
| `src/Runtime/CombatSolverLog.cs` / `CombatDiagnosticJournal.cs` | 独立日志入口；生产线程入队不可变消息，复用后台事件文件；战斗切换摘要化、搜索日志绑定所属战斗、提交前缀冻结 | Godot 全局日志收集、搜索候选判定、后台读取 live 状态 |
| `src/Diagnostics/Telemetry/OnlinePresence.cs` | 主线程在线标量采样、共享持久安装标识和证书固定的 HTTPS 客户端；无头和多人隔离 | 搜索策略、完整路线上传、服务端历史存储 |
| `src/Diagnostics/Telemetry/RunStatistics.cs` / `src/Diagnostics/Telemetry/RunStatisticsStore.cs` | 主线程跑局/战斗/设置/实际操作标量事件；独立有界队列，后台持久化、原生结算恢复与幂等补传；不可变提交时战绩快照 | 搜索状态键、模拟、游戏存档修改、历史求解器参与推断 |
| `src/Runtime/SolverController.cs` | 主线程高层搜索/续用/部署/全自动编排入口、共享状态与 facade | Beam 内部算法和 UI 布局、搜索 worker 生命周期细节 |
| `src/Runtime/SolverSessionCapabilities.cs` | 集中声明单人、多人只读 Probe、多人 Advisor 与多人 Safe Execute 的能力边界；当前只返回单人或 Probe | 实机多人证据、网络协议、队友规划和动作分类器 |
| `src/Runtime/MultiplayerClientProbe.cs` | 主线程只读记录网络多人客户端的本地可见状态与公共敌人状态，生成观察 fingerprint | 搜索、部署、RNG/CombatState 修改、网络包和队友隐藏状态 |
| `src/Runtime/MultiplayerWorldTracker.cs` | 维护多人观察的 `WorldVersion`、dirty 标志和 debounce 稳定边界 | 网络事件订阅、路线修复、搜索调度和 live 状态读取 |
| `src/Runtime/SolverController.SearchLifecycle.cs` | 搜索请求、root barrier 延迟/取消、worker 回调、结果发布、搜索引用释放与 CTS 生命周期 | 部署动作顺序、Beam 内部算法和 UI 布局 |
| `src/Runtime/SolverController.Deployment.cs` | 全自动部署、动作入队与原生选择驱动、部署间隔、取消/完成及部署引用释放 | 搜索策略、搜索 worker 生命周期和 UI 布局 |
| `src/Runtime/SolverController.Continuation.cs` | 跨回合续用校验、路线采用、回合准备续接预览、重算原因/状态差异审计与手动分歧记录 | 搜索 worker 调度、部署动作顺序和 Beam 内部算法 |
| `src/Runtime/SolverControllerSessions.cs` | combat/search/deployment 三类会话的状态与取消所有权 | 跨会话全局静态字段堆积 |
| `src/Runtime/CombatRootSnapshot.cs` | 主线程捕获完整预测根，比较捕获前后 live 状态，并向 worker 提供 Fork 根 | worker 惰性读取 live 战斗 |
| `src/Runtime/ContinuationStamp.cs` | 跨回合 live/predicted 状态文本、首个差异与完整差异；九条战斗 RNG 使用计数器与四段内部状态共同核对 | Beam 状态去重 |
| `src/Runtime/SolvedRouteCache.cs` | 主线程捕获路线记录键；后台按完整根与策略读写本地路线副本，独立于战斗会话和 SL 入口；Forecast 使用新根对象 | 搜索策略、原生存档修改、保留旧战斗对象 |
| `src/Runtime/DynamicVarCloneMetadataPatches.cs` | 模拟域精确复制 BaseLib 提示/升级及 Ritsu 提示元数据，只为已有值建立弱表项；保留 live 行为 | 通用 SpireField 工厂替换、丢弃升级值、清空全局弱表 |
| `src/Runtime/BaseLibCloneConcurrencyPatch.cs` | BaseLib 克隆扩展存在时，保护原版 `MutableClone` 及未经独立性核对的预测克隆；已核对的普通原版卡牌与默认内部初始化 Power 由 `NativeModelCloneConcurrency` 放行 | 整段搜索串行化、BaseLib 业务语义与候选政策 |
| `src/Runtime/PowerDynamicVarWarmup.cs` | 主线程根捕获时物化规范 Power 与当前战斗 Power 的显示变量 | 搜索评分、Power 语义与 worker 本地化 |
| `src/Runtime/PowerDynamicVarMaterializationGuardPatch.cs` | 搜索模拟惰性创建 Power 显示变量时立即报告根捕获缺失 | Power 语义、显示内容与搜索阶段串行化 |
| `src/Runtime/PowerAmountComparisonPatch.cs` | 将原生 `GetTypeForAmount` 中两处精确匹配的同枚举装箱比较改为整数比较；保留虚 getter、decimal 分支和调用顺序，未知 IL 原样保留 | Power 状态缓存、跳过类型 getter 或改变显示类型规则 |
| `src/Runtime/SearchGcPolicy.cs` | 管理玩家显式开关的进程级 GC 模式：开启时按原样预算建立战斗级 NoGC、执行搜索内安全检查点与引用释放后的压力回收；稳定关闭时使用 CLR 常规分代 GC 且不新增自动补账压力，从开启切换时仍结清此前义务；模式切换和手动释放与活动搜索计数共用安全边界 | Beam 剪枝、候选评分、模拟语义与同步阻塞 UI |
| `src/Runtime/SearchGcPolicy.Recovery.cs` | 在已排空的提交边界评估可恢复 NoGC 回退；拥有完成 Gen2/冷却/次数上限、物理余量、scope 代次与恢复后区域上限 | 强制回收、等待搜索退出、搜索预算或候选策略 |
| `src/Runtime/SearchGcLifecycleMetrics.cs` | 记录显式回收与 NoGC 启停/丢失；在 Runtime 准入 Gate 内冻结 scope 起止，区分独占搜索与共享进程窗口；暂停最大值仅为观测值 | 线程级 CLR 事件归因与 trace 最大值 |
| `src/Runtime/ProcessWorkingSetTrimmer.cs` | Windows 手动释放在托管堆压缩后修剪当前游戏进程工作集 | GC 生命周期、搜索调度与自动触发 |
| `src/Runtime/SystemMemoryReleaseService.cs` | 等待当前进程回收完成，再通过 UAC 启动短命辅助程序清空系统工作集与待机列表 | 自动触发、修改页列表清理与搜索策略 |
| `src/Runtime/SearchMemoryPressureSignal.cs` | 将 Runtime 的进程分配边界、回收入口、已排空边界的恢复探针和低系统余量下的保守并行标记注入搜索；不让 Search 直接操作 GC 模式 | 设置读取与搜索评分 |
| `src/Runtime/SolverControllerSessions.cs` | 除会话状态外，向 UI 提供当前进程占用与活动搜索分配检查点的只读快照 | UI 样式与搜索内存政策 |
| `src/Runtime/SolverSettings.cs` | 持久化性能、执行、搜索并行度、NoGC 开关与独立预算、逐槽药水策略和搜索结束通知设置，并在主线程捕获不可变搜索 snapshot | 搜索期读取全局设置 |
| `src/Runtime/PlayerTurnSetupPatches.cs` | 准备阶段稳定根搜索与既有选择重放；原生会话独占生命周期，每次搜索独立取消并排空，页面等待后原子确定唯一 worker 所有者；结果发布结束接管标志，手动提交淘汰旧根；后续回合无既有选择时捕获准备根；进入 Play 后交给 continuation 核对 | 普通 Play 阶段搜索与动作部署 |
| `src/Runtime/NativeChoiceRuntime.cs` | 观测原生选择 Task 完成及页面序号，按卡牌语义状态匹配计划实例；搜索期间保留手动输入，实际驱动期间持有页面锁，清除尚未提交的手动勾选后选择计划实例 | 选择分支枚举和战斗结算 |
| `src/Diagnostics/Telemetry/ClientUpdateNotice.cs` | 解析现有心跳响应、严格比较三段版本、发布线程安全纯值提醒；OnlinePresence 在主线程通知 Overlay 刷新 | 网络请求调度、安装更新或战斗操作 |
| `src/Diagnostics/BugReports/CombatBugReportExporter.cs` | 主线程冻结当前/最近战斗的实机取证状态；单消费者后台 FIFO 按检查点顺序整理并一次序列化为 UTF-8 字节，导出任务作为队列屏障等待此前记录完成 | 后台读取 live 战斗、通用 replay/native-state 导入 |
| `src/Diagnostics/BugReports/CombatBugReportDescription.cs` | 汇总本场结构化异常、重算和战损信号，提供诊断文字与标签 | 网络字段拼装、搜索决策 |
| `src/Diagnostics/BugReports/CombatBugReportMetadata.cs` | 主线程冻结战斗、角色及已观察怪物的稳定 ID 和显示名称；序列化 report.json v2 的身份、分类及预测战损比较 | 网络请求、搜索策略、后台读取 live 状态 |
| `src/Diagnostics/BugReports/CombatBugReportUploader.cs` | 通过不继承游戏进程代理的专用客户端直连接收服务；校验问题包与文本上限，以 multipart 流式上传并传播取消，限制服务端响应，并以反馈编号和实收字节数确认完整接收 | 问题包内容生成、隐私脱敏、UI 单实例与确认流程 |
| `src/Replay/CombatShowcaseCollector.cs` | 在严格合格的首个 Boss 搜索根冻结值材料，完整胜利后生成五文件录像包，并由在线统计同意状态控制待上传队列；逐阶段记录未收录原因 | 搜索策略、后台读取 live 状态、监控后台 |
| `src/Replay/CombatShowcaseModEligibility.cs` | 依据 Mod 的玩法声明筛除新角色、新机制和数值修改，并集中登记效果已冻结进根或只在指定复现流程生效的建局工具 | 按当前加载数量设置白名单、搜索兼容性判定 |
| `src/Replay/CombatShowcaseRuntime.cs` | 校验录像协议与文件摘要，从主菜单建立不保存跑局、恢复精确战斗根，并把预计算结果交给 Controller | 远端目录 UI、重新搜索、正式存档与统计写入 |
| `src/Runtime/SearchCompletionNotifier.cs` | 搜索成功、失败、停止或过期后按设置决定是否通知；Windows 使用原生通知和系统提示音，先核对前台进程并在非 Windows/headless 环境停止 | 搜索生命周期、跨平台伪通知和自定义声音播放 |

`SolverCombatSession` 持有本场路线、续用和重算状态；`SolverSearchSession` 持有 generation、取消、进度和帧观测；`SolverDeploymentSession` 持有部署取消。旧回调只能写回创建它的 search session。

`src/Api/CombatShowcaseApi.cs` 是私用录像 Mod 的公开入口，只暴露协议兼容信息和按本地包路径进入临时对局的异步调用。API 不直接操作 Controller 或 CombatManager；Runtime 完成建局与恢复。录像会话使用既有精确 continuation 续接和部署入口，任何失配都停止会话，禁止调用重算。

`SearchGcPolicy` 将活动搜索期间收到的后台回收请求保存在独立的 deferred 完成链中，所有搜索退出后才提升为实际后台回收。搜索内内存检查点只等待自己能够完成的回收，不能等待以该搜索退出为前提的任务；手动工作集释放继续等待搜索后的回收链。已覆盖的取消及 GC 转换后注入失败路径会协调 CLR 实际模式与内部所有权，并落定对应完成链、释放等待屏障；这些断言不穷举 CLR 转换前失败、OOM 或日志系统异常。搜索账本的存活与运行时 GC 模式互不混用。

搜索内检查点在 Gate 外直接等待非压缩后台 Gen2 primitive，不加入上述 deferred 链。primitive 先观察最新已完成 Gen2 的 index 与新 LOH 弱哨兵，仅在上一轮已完成却未覆盖哨兵时再次请求；不按定时器盲重发。全部异步等待不捕获调用方上下文。已发出的回收不能随搜索取消而放弃：确认完成后取消才落到默认 GC，超时或确认异常先显式阻塞排空，不能提前重建 NoGC。回收开始前的手动 GC 有独立完成信号，回收确认成功但搜索取消/超时不使它误报失败；开始后的手动请求与新的引用释放义务继续等待后续安全回收。日志分开记录请求模式、实际完成类型/index、CLR Concurrent 标志及阻塞超时兜底，不承诺每次都采用并发 GC 或没有暂停。
### 2.1 战前预测 API 隔离边界

NoGC 因内存不足或意外收集退出后，`Recovery` 仅在 coordinator 的 `EnsureMemoryForNextCommit` 已排空边界尝试恢复。退出后的检查点若已确认完成 Gen2、且尚未在该堆上尝试失败的预留，可直接复用这份完成证据；已经失败的预留或首次准入失败则等待新的已完成 Gen2，观察间隔至少两秒。每 scope 最多三次预留尝试，后续尝试保持退避，不因另一轮退出而清零。当前物理余量的一半作为恢复预留上限，且已知下一次不可分割工作必须能放入。恢复上限延续到本 scope 的后续区域重建，配置值仍为原始上限。恢复不主动收集、不加入 deferred 链；新 scope、退出 NoGC 和 Dispose 使旧探针代次失效。显式关闭、不支持的平台/区域尺寸、不可分割提交主动回退、取消和收集确认超时不自动恢复。Search 仍只消费信号，原并行增减策略、接纳顺序和工作预算不变。

PR #43 集成修正：Mod 使用独立文件复制，游戏程序继续使用硬链接；运行目录按主进程 PID 隔离。普通退出会移除大型游戏与 Mod 副本，保留会话诊断材料。启动快照缺失时 API 显式失败。当前账号目录由游戏路径 API 解析，并映射到禁用 Steam 的 worker 账号目录。求解设置按值冻结、进入状态令牌与 worker 签名，配置改变时重建 worker 并写入捕获值；整体期限从排队前开始，显式 Stop 会取消活动请求。

`src/Api` 是供伴生 Mod 使用的公开战前边界。API v6 只暴露不可变请求选项、目标坐标、已确定的非战斗路径步骤、假设样本选项、规划快照入口、枚举、结果，以及 worker 状态和生命周期入口，不暴露 `SolverController`、live `CombatState` 或搜索内部类型。

- `PreCombatLiveStateSnapshot` 只能在主线程捕获当前单人跑局。它用 `RunManager.ToSave` 获取完整 `SerializableRun`、记录精确加载的 Mod 集合和游戏/用户目录，并生成包含规范化存档与当前房间身份的 SHA-256 状态令牌。Mod 源路径优先解析到主进程初始化时建立的独立文件副本，使内存中的已加载版本成为 worker 的权威来源。
- `PreCombatRunSerialization` 清除墙钟、平台和地图涂鸦等非语义字段；显式写出游戏序列化器会省略、但反序列化默认成 `true` 的 `can_modify:false`；只移除恢复后会从空对象变成缺失值的事件历史 `variables:{}`，保留非空变量。这样子进程恢复后的全量快照能够逐字节核对。
- `PreCombatForecastApi` 负责参数验证、相同状态确定请求的去重/缓存和返回前主线程复核。确定预测要求目标地图列坐标，避免远端战斗沿用当前位置派生怪物 RNG；可选路径步骤必须逐层连续且只能是已确定的篝火、宝箱或商店等非战斗房间，未决事件和中间战斗会被拒绝。活动跑局、战斗状态或令牌发生变化时只返回 `LiveStateChanged`。
- `SimulateAsync` 是与确定预测分开的显式假设入口。它只接受当前幕原生普通、精英或 Boss 遭遇，以当前状态和下一可用地图行建立战斗，不进入确定预测缓存；调用方提供的样本种子只在隔离进程完成精确快照恢复后替换遭遇局部 RNG 和九条战斗相关 RNG。`UpFront`、奖励、地图与主进程 RNG 不被推进。
- `SimulatePlanningAsync` 是规划状态专用的显式假设入口。worker 从调用方提供的独立 `SerializableRun` 恢复牌组、生命、药水和地图规划状态；live 跑局只用于捕获环境并在返回前做令牌复核。它允许 `Unknown` 与原生事件节点进入战斗，并在主线程按目标坐标确定第二首领；规划存档的幕、种子、角色和玩家身份不匹配时显式失败。
- 调用方可读取 worker PID、忙闲、工作集、私有内存、峰值工作集、静音标记与空闲期限，也可显式停止或重启/预热。默认空闲期限为两分钟；API 可在 worker 待命时把期限重新设为任意合法毫秒值并重新计时，或以 `null` 取消空闲关闭。单个确定请求或一个调用方组织的样本批次可以在 reusable 屏障后选择立即关闭；生命周期选择不改变结果缓存键。
- 默认调用仍共享相同请求；显式可见的手动面板可要求独占取消和强制重算。独占取消会等待其精确拥有的 worker 进程结束后才完成，不能留下后台计算。
- `PreCombatForecastWorker` 串行拥有一个 Windows 子进程会话。主进程初始化期间先在游戏目录下带所有权标记的 `.combatsolver-precombat/startup-mods` 中钉住本次会话选中的 Mod 文件，后续 worker 从这些固定文件镜像实际加载的全部 Mod；工坊目录在运行中被 Steam 替换不会改变 worker 的版本。worker 另行硬链接游戏文件、复制必要配置，并使用独立的 `APPDATA` / `LOCALAPPDATA`、关闭 Steam 和 NoGC。隔离设置中的主音量、BGM、音效和环境音均强制为零并回读验证。
- 相同游戏根、用户根和 Mod 集合的连续请求会在同一子进程中依次执行。父进程只有在收到匹配 runId 的 result，并等到子进程返回主菜单、后台活动归零及 matching ready 屏障后才允许复用；达到调用方选择的空闲期限、显式释放、失败、超时、取消或主进程退出都会关闭拥有的进程。“一直维持”仍会在显式停止、失败或主进程退出时清理，不会留下脱离所有权的进程。
- 子进程由 `ScenarioBuilder` 直接恢复完整跑局，加载资源和地图后再次规范化序列化并核对精确哈希；只有通过后才按顺序补记已确定的中间非战斗地图历史，应用可选入战 HP，并用目标坐标、房间和节点类型进入遭遇。确定预测由此保持目标 `TotalFloor`、地图坐标、怪物局部种子、正常开战 Hook、首回合初始化和搜索流程一致。假设样本则在哈希核对之后、怪物生成之前注入独立样本 RNG。中间房间的购买、奖励、锻造和其他玩家状态变化不会被擅自执行，必须由调用方标为条件场景。

`src/Api` 禁止直接调用 `SolverController.RequestSearch`、`CombatManager.SetUpCombat` 或 `RunManager.EnterRoomDebug`。这些静态边界由 Windows/Linux 两份 `verify-refactor-boundaries` 脚本共同检查。隔离 worker 内通过 `COMBATSOLVER_PRECOMBAT_WORKER=1` 关闭 API，避免加载伴生 Mod 后递归创建 worker。

`RitsuEmptyCapabilityFastPathPatches` 的标签入口只在模拟隔离域且已证明 capability 集为空时返回原 `IEnumerable<CardTag>`，不枚举、不复制、不缓存标签值；已有空集合和精确类型默认来源代次沿用公共判定。非空贡献者、晚注册默认来源及 live 调用仍执行框架管线。

RitsuLib 0.6.0 自身拥有 BaseLib 目标类型的外部登记查询、按程序集弱键缓存和动态程序集旁路。CombatSolver 不再修补该桥的私有查询闭包或重复维护缺失证据；目标类型语义继续通过 RitsuLib 的公开能力入口读取。项目构建导入 RitsuLib 随包提供的多程序集引用表，Windows/Linux 无头快照复制同一完整版本包，避免编译期与运行期落在不同兼容分支。

玩家死亡被确认后，`CombatPredictionSimulator.HandlePlayerDeath` 先调用 `SimulatedCombatState.RemovePowersAfterDeath`，再清理球和宠物。敌人能力仍由原领域死亡清扫处理；玩家不能依赖仅遍历敌人的后续清扫。

`CombatPredictionSimulator.CardTargeting` 对君王之剑和小刀完整读取分支能力：能力存在时选择全体，不存在时选择单体。两侧都不能回退到可能读取实机 owner 的动态 TargetType；普通卡牌保持原生目标元数据入口。

## 3. Search

开发中的反馈修复：`GrowthOpportunityPolicy` 在主线程从当前可用的物理牌实例冻结逐来源目标。能力牌和消耗牌的基础次数都是每个尚可打实例一次；遗传算法、巨镰与黏糊强化额外要求 `DeckVersion`，固定 `GetEnchantedReplayCount` 逐次加入目标。单一致命来源按敌人数和实体数取可证明上限；多个致命来源竞争、动态重放、复制、消耗回收或第三方缺少目标计算器时写入不可证明原因。第三方计算器只收到不可变 `GrowthOpportunityCardSnapshot`，不能读取实机对象；负次数直接拒绝策略捕获。`SearchPolicySnapshot.GrowthTargetSatisfied` 比较整个收益向量，任一来源不可证明都禁止成长早停。早停还要求实际用药不超出用户必要数量。偷窃分项沿既有 SimulatedCombatState 计数投影为 SimulationSnapshot → SolverSnapshot → OverlaySnapshot，只读 UI 不重新读取真实战斗。Runtime 在选牌部署失配时暂停并交还手动选择，只有退出场景才取消原生选择；缺失战斗通知的面板恢复由 MonitorCombatPresence 在稳定回合负责。
`SearchPolicySnapshot.CanStopAtHpTarget` 统一默认开启的战损目标早停与实际成长目标。主线程冻结 `GrowthOpportunityTargets`，额度本身不代表持有对应牌；目标向量和不可证明原因进入路线缓存与问题包。Phases 在已准入候选提交时检查完整胜利、全部有界成长目标、遗物、偷窃和强制用药要求，命中后排空当前父节点/并行批次，释放后续工作并从达标候选收尾；Coordinator 在补充搜索结果边界沿用同一开关与阈值。“不考虑局外收益”从统一入口移除成长目标。
`GrowthCostPolicy` 管理至亮之焰单场累计最大生命消耗的准入；成本属于 SimulatedCombatState 的独立分支值，从主线程原生出牌历史捕获，经 Fork 复制并进入指纹/续用文本。`ResolveRoundChoiceBranches` 与 `ResolveTurnSetupChoices` 在产出候选前统一拒绝超额分支，实际模拟仍执行原有效果。禁忌魔典的收益计数在已有 CardPowerOnPlaySupport 中记入 GrowthValues，允许额度由成长策略设置决定。

`CombatSearchCoordinator.FailureRecovery` 在请求级完成主搜索与药水审计后，管理无完整胜利的有限追加搜索。它扩大搜索配置、保留请求剩余时间并比较已有质量；交接结果优先返回，每轮内存观测独立起算。四档内置节点预算由 `SolverSettings` / `SolverSearchProfile` 声明，依次为 60,000 / 120,000 / 250,000 / 500,000；Custom 保留显式设置，节点预算只要求至少 100，不设额外配置上限。设置迁移 244 只强制旧配置开启多宽度路线精炼，不重置性能与其他开关。

根创建时，`PredictionModPatchAudit` 在 Prediction 层检查已有卡牌 OnPlay 的第三方 Harmony 补丁；每根按类型去重并读取当前补丁表。`AdaptedCardOnPlayMirrors` 只为完整精确组合提供标准 registry 镜像，选择表归 `PredictionModHookSubscriberCapture`，随 `SimulatedCombatState` Fork 共享。OnPlay facade 命中后直接返回，禁止再执行 vanilla/spec。Runtime 的 live continuation 读取当前配置，预测 continuation 和指纹只读根标记；既有采用／续用／部署检查拒绝配置失配。worker 不得读取 Harmony 表。启用登记后，根未审计的新卡牌类型明确失败；其他方法和未登记状态机不在完整审计范围。接口见[OnPlay 补丁适配](third-party-onplay-patches.md)。

`BuildAcceptedEndTurnNodes` 是回合层/软时间预算收尾及普通串行回合尾的共同入口，复用 raw EndTurn 批次生成、跨回合剪枝与循环出口准入。全部直接选择分支在转置准入前结算临时观测；批次持有未转交快照，迭代器提前结束或生成失败时统一释放。

`StateEvaluation.BuildProjectedDeathPrevention` 每次按分支药水槽和遗物原序读取瓶中精灵、蜥蜴尾巴的可用状态，不缓存跨快照的可变结果。意图预测携带孤注一掷的一次性致死状态：玩家实际承受正数攻击伤害后先消费该状态并置为死亡，再按原版顺序尝试保命；全额格挡不触发。Engine 的 `HookMirrors.ModifyHpLost` 返回只读修正者集合，空结果共享空数组，非空 List 独占；后续通知先取得原监听表，空集合只跳过通知遍历。回调顺序、成员身份与重复成员只调用一次的规则保持。

### 3.1 请求级编排

- `SearchPolicySnapshot.cs`：主线程捕获的不可变搜索设置、逐槽药水策略，以及第一/二幕与最终 Boss 各自的血量取舍；后台不读取 UI 或玩家设置。
- `SearchDiagnosticsSink.cs`：搜索日志和可选纯值路径观察出口。观察默认关闭，先按状态键过滤，命中后才复制完整动作/选择路径与政策标签；另可显式筛选外层 Prune 池，记录完整输入、真实 RankBest 的原排名/必保/路由/选中索引、当时的战术估值标量及最终仲裁集合。RankBest 内部同步借用列表，立即转成值副本；不向注入方暴露节点、模拟器或闭包，不重算估值或选择器，也不参与候选裁决。注入方负责并发和输出容量。
- `SearchFramePressureSignal.cs`：Runtime 向 worker 提供的帧压力信号；以最近 `31` 个非搜索帧中位数建立基线，压力阈值为 `max(33 ms, baseline × 1.5)`，无显示服务的 headless 请求旁路帧恢复等待。
- `SearchRequestWorkTotals.cs`：一次请求内所有正常、失败和取消 solver 的工作区间均精确记账一次，包括取消前已发生的展开、转移、选牌、耗时、分配和 GC；Smart 有限药水层之间由 coordinator 主动执行的内存整理也单独计入耗时、分配和 GC，但不伪装成额外 solver。请求总值不是完整 coordinator 外层墙钟或进程峰值，也不承担结果质量排序。
- `CombatSearchCoordinator.cs`：一次请求的搜索编排；Smart 先搜索无药基线，再根据可用药水、无药战损和药水价值门槛确定最多进入的“恰好 `N` 瓶”层。按瓶数递增搜索，同层药水共同竞争；第一层完整获胜且满足救命、节省生命或保全被盗资源条件时立即采用并停止增加药量。达到设置的可接受战损阈值也可提前结束请求，不保证遍历全部药水层或取得所有药量中的全局最优。进入下一梯度前回收上一层搜索图并重建 NoGC 区域；截止时保留已完成且符合政策的选择。跨 solver 只发布符合政策的严格改善完整路线，并透传当前 solver 已完成回合的候选。玩家可采用已显示路线或只执行当前回合。Disabled/RequireAtLeastOne 保持各自政策；实际运行的各层共享请求级时间余量并合并总指标。
- `CombatBeamSolver.BlockPotionInsertion.cs`：Smart 无药主搜索选出完整胜利后，针对首个预计掉血至少 `PotionMinimumHpSaved` 的回合，把可用且未保护的格挡药插在结束回合或强制交回合动作之前。修改后的动作链必须由模拟器逐动作精确重放并重建逐回合标注及 continuation；只有实际省血达到门槛、仍获胜且不增加保命资源消耗时才替换结果。该路径不进入 Beam、转置或药水候选展开，成功后 Coordinator 直接结束请求。
- `CombatPlan.cs`：Runtime 消费的计划、结果和续用数据。结果不得保留历史 Simulator 对象图。
- `SearchReplayEvidence.cs`：最终选中路线已有父链标量与同次遗物标注回放的逐动作对账，记录首个 HP/格挡/能量/星能/手牌数差异；仅差异时生成完整回放状态文本，最终失败时保存双侧完整状态。普通候选不增加状态转储；异常路径记录失败候选前缀和尝试动作。只通过 diagnostics sink 输出不可变文字。

`ActionRelicTriggerRecorder` 仅存在于最终路线回放，附带 Damage/Heal 的来源、请求/修正数值和 HP 前后值；普通 Beam 分支保持 null，不分配取证列表。直接字段赋值等绕过 Damage/Heal 的变更尚无来源事件，不能把这份记录宣称为所有语义写点的完整追踪。

`BeamWidthPortfolio.cs` 是一个与 Beam 算法无关的组合器：按顺序在同一个根上跑若干宽度或中途排序不同的成员，共享一份节点预算（首个成员拿全额，其后各成员的上限是扣掉前面实际展开数后的余量，扣光即停），撞节点上限又没到终局的成员不参与比较，其余按调用方传入的既有比较规则整条取最优，同分保留先出现的基线成员。它不含比较规则、状态键或终局排序；展开数、转移数和终止原因都由调用方按各自既有口径给出。`SolverSettings.UseBeamWidthPortfolio` 默认开启，由 Runtime 冻结进 `SearchPolicySnapshot`；关闭时只运行基线成员。做法与数据来源见[宽度组合](strategy/beam-width-portfolio.md)。

`BeamWidthPortfolioGate.cs` 是精炼成员的准入判断，只做算术与比较，不看搜索状态：基线必须已经把自己这一宽度搜干净（`BoundaryReason == None`）、不是已证明最优的零战损胜利、耗时不超过时间预算的四分之一，且共享节点余量、剩余时间、`SearchMemoryPressureSignal.RemainingBytes` 都装得下「基线实测 × 成员宽度 ÷ 基线宽度 × 3/2」的估算，才启动下一位成员；否则该成员不运行、不花预算，只留一行原因。成员顺序执行不并行，精炼成员的软时间预算收紧到本轮剩余部分。`BeamWidthPortfolioTelemetry.cs` 是请求级诊断，记首条路线发布时刻、逐成员开销与各成员结束后的托管堆峰值，挂在 `SolverResult.PortfolioTelemetry` 上供测试写出；组合关闭时同样记录，那时是单成员一行。基线成员一完成就走协调器已有的 interim 回调发布给界面（中途路线本来就由 `SolverProgress` 承载），精炼不影响玩家看到第一条路线的时刻。Search 仍然不读设置：开关与成员宽度由运行时写进 `SearchPolicySnapshot`。

周期候选在最多 32 步的窗口内比较重复动作、控制形状及伤害发生相位，避免把较长周期中的安静阶段当成整个循环。每周期伤害数值可以变化：动作、形状和伤害相位重复且实际刷新敌人耐久低点时，可取得伤害进展证据；精确转移增量是否一致仍单独记录，不把增长伤害伪装成相同增量。已证明刷新逐敌人历史最低耐久的路线可使用独立进展通道：每个 region 每层至多一个代表，最多保留该周期余下的 31 个安静动作，且只由实际保留节点的一个直接后代消费。只有新的最低耐久能续期；普通停滞、试探和顺序选择预算不因此重置。进展准入在最终仲裁后结算，并解除已经完成目标的旧出口探针；所有动作仍逐步模拟并受请求节点与时间限制。

主 incumbent 只能由满足硬政策、且没有消耗或预计消耗保命资源的完整胜利建立。无主动用药入口要求实际生效政策为 `Disabled` 或 `Smart`、最少用药数为0、候选显式用药数为0；若启用逐槽指令，还必须实际满足全部强制使用要求。正数精确药水层保留原条件：最少与最多药量相等、有已审计无药基线、未启用需另证的逐槽强制指令，且完整胜利严格改善基线主质量。未完成路线、死亡路线或仅满足中间评分的候选不能建界。

完整胜利先按保命资源消耗次数排序，再按统一战略战损计价：累计掉血、最终最大生命缺口、路线治疗、无条件战后遗物回血和保命资源消耗。瓶中精灵与蜥蜴尾巴的复活回复不算路线治疗，最终 Boss 同样保留消耗代价。未完成分支的乐观下界允许当前缺血全部恢复，保留已经发生的保命消耗代价；中间最大生命缺口可能恢复，不进入下界。搜索中的路线展示、主结果剪枝、保留和最终排序共用该口径；消耗保命资源的路线不触发战损早停。诊断日志以 `source=no_explicit_potion` 或 `source=exact_potion_layer` 区分建界来源。

Smart 层间使用 `SmartLayerMemoryForecast` 的同窗分配和转移高水位估算下一层容量；预测超出余量、样本不完整或区域丢失时回收并重建 NoGC。回收仍遵循原有药水层准入及停止条件。

普通 Beam 保持原有评分、动作数、`OffensiveProgressValue` 初始排序及必保候选构造。在必保候选置换之后、药水配额处理之前，定位原排序中最后一个实际存活的普通候选，仅对跨越该截线且 `BeamRankScore` 与动作数都精确相等的块做有限多样性保留：同一 `PotionCount` 内按进展值分组，值从高到低轮流取代表。组内仅无既有保留路由签名的候选按当前回合和完整转置标签隔离，再以零费可执行牌数、可达手牌价值、手牌数稳定排序，写回各组原位置；带签名节点的原组内位置不动。签名存在性直接复用 `RetainedRoutingChoice`，包括其既有跨回合例外，不重新定义时效或依赖观察器。必保候选、各标签和该块各药量已有席数、其他评分块、总容量和工作预算不变；单值组、单席组、完整终局优先模式及含获胜候选的块旁路。这避免同分截线被单一进展值占满，不使用卡牌或遭遇身份，也不保证有限宽搜索完备。

### 3.2 分支状态与预测领域

- `CombatSearchCoordinator` 拥有请求级预算、路线比较、药水审计和恢复编排；`CombatBeamSolver` 只拥有分支搜索与候选剪枝。
- `CombatRootSnapshot` 由主线程捕获并冻结，worker 只消费根快照、`SearchPolicySnapshot`、诊断 sink 和取消令牌，不读取 Controller、UI、全局设置或 live 战斗对象。
- `SimulatedCombatState` / `CombatState` 拥有各自分支状态；`Prediction` 领域只通过既有 effect sink、mirror 和纯值模型提供预测，不建立第二套战斗后端或跨分支可变缓存。
- `SolverResult` 是搜索到 UI 的边界；Overlay 先捕获不可变 `SolverOverlaySnapshot`，renderer 不反向读取结果模型、计划对象或 `ModelDb`。

## 4. 模拟引擎、UI 与部署边界

- 内嵌模拟引擎负责可 Fork 的状态、卡牌/遗物/Power mirror 和确定性 RNG；原生兼容反射与 Harmony 参数形状集中在 `src/Compatibility/`，不泄漏到 Search。
- `SolverController` 及其 SearchLifecycle、Deployment、Continuation partial 拥有主线程会话、取消、部署和跨回合续用；`SolverCombatSession`、`SolverSearchSession`、`SolverDeploymentSession` 分别持有对应状态。
- `SolverOverlaySnapshot` 是 UI 的单向输入。面板拥有输入与设置提交，部署仍由 Runtime 驱动；控件不得持有或重新读取 worker/live 状态。

## 5. Unattended 测试边界

- `src/Testing/` 拥有完整请求协议、fixture、断言和结果写入；`src/Runtime/TestingBridge/` 只保留生产侧必须的窄 DTO 与活动桥接，不把测试状态带回 Runtime。
- `ProtocolHost` 负责请求循环和协议校验，`ScenarioBuilder` 负责建局/恢复材料，`Executor` 负责搜索与部署，`Assertions` 负责断言，`Writer` 负责公共结果和证据原子写入。
- OfflineSearchHarness、CheckpointTool 和 headless runner 可以复用协议入口，但不拥有生产正确性或游戏内语义。

## 6. 工具与变更门禁

- 当前工具分类见 [`tools/README.md`](../tools/README.md)。平台 runner、CompatibilitySmoke、CoverageCatalog、目标版本检查和结构门禁属于当前入口；实验/研究目录不能未经引用审计变成生产依赖。
- 纯职责移动至少运行 Release 编译和当前平台结构门禁；改变语义、搜索或显示行为时，再按影响面选择严格差分、完整 headless、CoverageCatalog 或可见 Steam。
- 旧架构记录、性能报告和问题包只提供历史证据；与当前源码、`source/AGENTS.md` 或本页冲突时，以当前入口为准。

完整原始架构：[架构历史归档](history/architecture/ARCHITECTURE-2026-09-19.md)
