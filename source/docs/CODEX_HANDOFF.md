# Codex 当前交接

> 只记录当前生产状态、当前风险和下一任务。历史阶段过程、旧问题包结论和已关闭实验从 Git history 恢复，不在本文件维护流水账。

## 基线

- CombatSolver `0.40.2`
- STS2 / RitsuLib pinned `0.107.1`
- .NET 9 / Godot 4.5.1 / `STS2_01071`
- 单人与多人共享主搜索/模拟核心。
- 生产多人默认使用 **local-single-core**：本地单人质量排序 + 多人真实状态/WorldVersion + 本地玩家执行权限。Team Objective、teammate forecast、Scenario/Robust 等实验多人预测由 `UseMultiplayerPrediction` 单独开启，默认不作为生产质量层。
- MultiplayerOnly 牌保留真实牌堆占位与抽牌距离，但不进入主动搜索/自动执行。
- 多人 Safe Execute 只部署本地玩家动作；队友观察不会获得部署权限。

## 当前主目标：Rolling Horizon Reuse

主计划：[`Rolling_Horizon_Reuse_Architecture.md`](Rolling_Horizon_Reuse_Architecture.md)

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

新增待实施任务：[多人战前预计算与 Boss 通关优先计划](CombatSolver_GPT_Architecture_Plan.md#11-多人战前预计算与-boss-通关优先2026-09-30)。仅完成静态可行性审计；先统一 Boss 质量排序，再验证多人离线恢复，未开放能力或修改相关源码。Showcase 已取消。

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

Release、结构门禁、`request-hydration`（含并发统计、未首验惰性校验和默认关闭诊断）通过；DOP=4 药水审计固定输入也保持完整结果/工作一致。尚未证明净提速，因此继续默认 false，停止扩容和推广，不进入本功能的实机启用验证。当前统计只支持定位成本，不证明真实 Host/Client 或普遍性能。下一候选应先寻找确有昂贵重复转移的自然输入；否则按 [加速计划 §12](CombatSolver_GPT_Architecture_Plan.md#12-多人搜索加速2026-10-01) 转向其他已测热点。

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

三请求均有系统内存压力 GC 回退，工作量不同，不能与上一构建作为净收益 A/B；没有执行路线，实机净收益/部署正确性仍 UNVERIFIED。手动重算 SnapshotEvaluation 累计 9.298s，其中持续效果 3.172s、Fingerprint 2.958s（CombatFingerprint 0.509s）。下一步继续定位 `StrategicEffectContext.Build` 内能耗/关键词查询的剩余成本，先证明具体热点和完整请求净收益；预算、排序和缓存默认保持不变。

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
