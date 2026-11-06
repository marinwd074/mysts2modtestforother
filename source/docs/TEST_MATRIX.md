# CombatSolver 当前测试矩阵

> 只保留当前能力、验证入口和未验证边界。历史批次与旧数字从 Git history / 定向 evidence 查。

## 静态 / 构建

| 层 | 入口 | 证明范围 |
|---|---|---|
| Target | `tools/verify-target-version.ps1` | 0.107.1 / RitsuLib / compatibility symbol |
| Architecture | `tools/verify-refactor-boundaries.ps1` | 依赖和职责边界 |
| Repository | `tools/verify-repository-hygiene.ps1` | Git 跟踪文件、Markdown 本地链接、项目版本日志与 LFS 边界 |
| L1 contracts | `tools/run-contract-tests.ps1` | Search / Prediction / Multiplayer 纯合同 |
| 多人事件交错 | `dotnet run --project tools/MultiplayerInterleavingChecks -c Release` | 直接编译生产观察状态和执行授权类；10,493 个确定性顺序，失败输出 `--replay <id>`，不依赖游戏 DLL |
| 请求生命周期观测 | `dotnet run --project tools/SearchRequestObservationChecks -c Release` | L1 默认运行；直接编译生产观测器，16 项纯时序/任务合同，覆盖延迟、取消前未启动、并行 CTS 收尾和单次封存；不运行 Controller/UI |
| 多人候选刷新 | `dotnet run --project tools/U0U1PinnedHarness -c Release -- multiplayer-refresh --out <ignored-directory>` | 两个原生角色模型、受控 HP/回合变化；376 项检查/54 次独立重放，含完整当前/备选路线新根重评、连续刷新、失败隔离、评价窗口拒绝、提前胜利、选择集合隔离、预算/取消账本、R1 缓存清理、请求观测开关对照。跨回合传递新增 52 项/11 次，含两个原生搜索续接点重放、合成历史成本裁切、共同 600ms、失败后 R1/DOP=1/4 节点上限及续用后再刷新；无原生 EndTurn/Controller/原生队友动作/网络/UI |
| Release | `dotnet build CombatSolver.csproj -c Release` | 当前本机真实依赖下编译 |
| 不启动游戏的组合验证 | `tools/run-offline-validation.ps1 -GameDir <安装目录>` | 外部版本校验、上述静态检查/Release、L1、portfolio 与 17 个 pinned 模式；逐步日志、DLL 摘要及跳过子项 |

快速 Linux main push / PR 运行 Target、Architecture、Repository；定向 Windows 回归和手动 pinned 矩阵按工作流各自触发规则执行。CI 只能证明其实际执行的层级；没有日志/steps 的 runner 失败不能记作源码 FAIL。

组合验证只运行 .NET 合同及带 Godot 服务替代层的进程内模拟，不启动真实游戏。快照基础设施自测使用改名的 `cmd.exe` 夹具；无符号链接权限时明确跳过 reparse 子项，其余项仍可验证。该入口不能证明原生 UI/输入时序或真实 Host/Client 并发。

多人交错合同直接测试主线程纯状态边界，未覆盖完整 Controller 或传输层。2026-11-05 的隔离副本变异检查分别去掉过期版本保护、将 debounce 缩短为 199ms、放行重复观察和放行取消后 AcceptAction；四个副本均触发可重放断言失败。完整 35 组 L1、Release 与 `upstream-search` 17 请求/17 重放通过；符号链接权限子项仍明确跳过。

`upstream-search` 同时核对显式药水成本快照：空记录、普通主动消耗、自动消耗、高价主动+自动混合、同一槽位连续消耗，以及 Fork 后父 ledger/快照隔离。自动消耗由测试直接输入 ledger，不是原生救命药水时序证据。成本快照优化前后 17 请求的完整动作、质量、工作量及优化计数相同；JSON 对照只排除 elapsed，末态在各构建内部独立重放验证。

`U0U1PinnedHarness entropic-cost --out <ignored-directory>` 核对 pinned 原生 `PotionFactory` 序列与生成来源，覆盖普通补槽重置、自动消耗、连锁混沌、龙涎香、Fork/状态键、Smart 准入、完整搜索及独立重放。跨回合通过真实模型重放和生产 `BuildRouteAnnotations` / `CaptureUses` 生成账本，再对合成结果调用生产续接合同，核对过去回合成本不再扣除；并不冒充实际 UI 续接。`PotionStrategyChecks` 的 24 项覆盖同槽同 ID 再生不能替代原始强制瓶、自动消费不能完成显式指令，以及全禁用和仅强制策略。两个入口均不启动游戏。

混沌专项 41 项检查通过，包含完整 Coordinator 的强制基线/Smart 分层与独立重放；例中原始混沌成本 9，生成火焰药水成本 0。龙涎香不在 pinned 0.107.1 原生模型库，溢价规则只由纯策略合同覆盖，不能报告为原生效果 PASS。2026-11-05 组合入口 24 步、35 组 L1、82 项 portfolio、17 个 pinned 模式通过（符号链接权限子项仍跳过）；在组合验证后又复跑包含 Coordinator 的 41 项专项。与先前构建比较，16 个非混沌常规请求质量值相同，部分等质路线/工作量变化；第 17 个共享见证请求工作量相同，所有选中末态在各构建内部独立重放，不把此结果称为普遍提速。

`U0U1PinnedHarness rng-restore` 对照旧 RNG 还原的计数器、状态、后续随机值及父状态隔离；完整请求对照复用 `request-hydration-benchmark-off`，高计数器压力夹具为 `multi-hit-high-counter`。不替代实机搜索或退出取证验证。

`U0U1PinnedHarness turn-based-energy` 对照 PaelsFlesh / Bread 原生最大能量：144 次根/未来/live 回合及熔化查询、1280 次 DOP=4 查询、4 次完整 EndTurn 重放，核对全分支状态/RNG及真实回合隔离。`request-hydration` 另覆盖异常成员 Faulted、释放与重复 Dispose 的单次贡献。

`U0U1PinnedHarness continuation-replay` 另覆盖已物化 incumbent 的显式采用：Coordinator 选择/worker 最终结果保留采用范围与屏幕身份，普通完成不获得采用标记；连续两次 RNG 新根重放核对质量、不同路线身份及 live 隔离。当前标记修复的 Host/Client 复测仍 UNVERIFIED。

`U0U1PinnedHarness model-id-cache` 核对原版类型、注册期模组临时 ID、冻结后 ID、重复通知和空参数；`PreCombatRequestChecks` 另核对 worker 不得启动嵌套预报。两者是 pinned / 合同证据，不证明实际 worker 进程或 Host/Client 成功。

`U0U1PinnedHarness card-choice-identity` 覆盖 11 组费用有效期/隐藏层/顺序/星费、原生清理和 Fork 隔离；另以 40 个唯一/重复牌选牌场景核对实体 occurrence 并输出有序结果 SHA-256 与局部 allocation probe。优化前后 1,647 个分支顺序一致；该 probe 不代表全战搜索速度。

`U0U1PinnedHarness upstream-port --out <ignored-directory>` 覆盖 12 组 pinned 0.107.1 合同：32 个 DOP=4 COW 分支及游戏克隆身份、DynamicVar 字段/公开回退、原生星费清理、9 组路由剩余牌序、12 个 Seeker RNG 状态、批量变换、效果嵌套/Fork 拒绝/释放、同数量集合写入、手牌上限指纹/续用及父状态隔离、多项差分上限、三选一原生字段形状、6 档格挡溢出与普通原生倍率。所有检查后 live continuation 不变；不证明原生 UI 时序、完整原生效果/执行检查点差分或 Host/Client。`completion-scope` 同时核对不同候选版本不得接管，以及种子必须复制外部动作列表。

`U0U1PinnedHarness upstream-search --out <ignored-directory>` 对四个 pinned 人工夹具做 ABBA，外加一次共享完整胜利见证搜索，共 17 个请求与 17 次独立完整末态重放；拒绝时间截断，核对质量不退化和 live 隔离。覆盖有限回复、未知根/分支来源回退、第三方原生补丁拒绝、见证根身份/资源目标隔离、实际共享界与 Smart 剪枝命中。原开局 99/660 工作不变，100HP/无遗物为 520/3634 → 258/1758，两瓶火药为 671/9071 → 277/3901，未知 Feed 为 575/8528 不变（expanded/transitions）。这些不是自然实机快照；增量验证开启，耗时不作为普遍性能证据。另以 `BeamWidthPortfolioChecks/run.py` 82 项核对机会成本、多人原质量、饱和估算和真实回收检查点准入，`PotionStrategyChecks` 及 `MultiplayerLocalCrossTurnChecks` 86 项保留策略边界。均不代表原生执行、UI 或真实 Host/Client PASS。

`U0U1PinnedHarness memory-display` 核对物理容量、缺失采样、回收压力与显示文案，可用 `DOTNET_gcServer=0/1` 分别验证两种 CLR 模式；`CombatSolver.GcPolicyChecks manual-release` 使用真实 CLR 核对搜索退出屏障、重复请求合并、存活数据和空闲堆页归还。`recovery` 覆盖 40 次恢复、饱和退避、结构性退出与预算限制；`recovery-lifecycle` 需要真实恢复余量，不足 512 MB 时失败不计为 PASS。

`tools/multiplayer-lab/test-joint-continuation-validator.ps1` 覆盖 plain/JSON、reason 解析及错误原因拒绝；RouteReplay 检查同日志/请求的新根与完整账本、评估范围、新授权及原生部署。TargetDeath 另检查 roster strict subset、旧 seed 截断、恢复与存活目标部署；搜索代次和路线代次独立匹配。反例包含错请求/回合、旧授权/路线、混合成员、无质量证明、动作顺序、未清授权、替换敌人、死亡目标、预算变化及缺失证据；只有真实 Host/Client journal 的对应模式通过才构成实机证据。入口见 [多人 RUNBOOK](multiplayer/RUNBOOK.md#joint-forecast-continuation-smoke)。

`request-hydration-benchmark-off --measure` 另核对 Snapshot 全调用与子阶段调用数、不重叠阶段上界、结果内部子阶段和 worker 汇总；固定输入包含 shared-audit / draw-repeat / multi-hit，三敌覆盖 DOP=1/4。诊断开关对照验证完整根/路线/最终状态/质量/工作一致，不作为新增默认门禁或提速证明。

`U0U1PinnedHarness root-history` 核对根历史预聚合与原扫描、预测事件叠加、未知玩家/非玩家回退、Fork 隔离及新根更新。完整请求对照沿用 `request-hydration-benchmark-off`；`history-repeat` 人工记录 2000 次抽牌但保持棋盘不变，只作长历史压力输入。JSON 含实际加载 DLL SHA256；请求计时不包含根捕获，不替代真实两玩家验证，不新增默认门禁。

`U0U1PinnedHarness projected-shuffle` 对照原生 StableShuffle 和未缓存 CardValue：450 次值查询、288 个完整投影、32 个 DOP=4 分支，核对顺序、完整卡牌键、逐位置加权取整、四字 RNG/Counter 及原 RNG 隔离，覆盖重复牌、空列表、分数值、修改/升级/Clone/COW、冷并发查询、父/真实状态隔离和可外部修改模型的禁用合同。仅复用原生 Preview 纯值；不替代第三方模型或实机执行验证，不新增默认门禁。

`tools/StrategicKeywordChecks/run.py` 比较 134932 个完整上下文，含 Corruption 已确定技能消耗时的按需读取及小刀固有关键词；`--native` 生成完整关键词集合 oracle 并显式编译进既有 harness，比较 462 个上下文/抽牌时机，覆盖 Hex 全局 Hook、移除/可写卡牌、NoDraw 顺序、Fork 隔离及 DOP=4。oracle 共享标量公式，不替代旧算法标量合同。同一入口的 `strategic-energy` 另核对 180 次原生费用、792 个上下文、48 个并发分支及查询前后 Power/history 指纹、真实 Owner 变化隔离，覆盖免费/消耗 Power、局部费用、X/负费用和牌堆移动；原生牌堆临时挂载仅限无头串行 oracle。所有原生上下文同时对照未缓存卡牌纯值，含预热后 BaseValue 分数修改，核对 Ceil、最低值和聚合字段。原生完整请求使用人工 `strategic-repeat`（4 Power + 6 张抽牌堆卡牌），覆盖 DOP=1/4；Benchmark 以完整动作 JSON 值核对 Choice/目标，结果记录 `fullRoute`。不替代实机 Hook/部署验证，不新增默认门禁。

## 单人

- 主线：稳定基线。
- 0.107.1 后版本差异候选已完成主要卡牌审计。
- Axebot `AXEBOTS_NORMAL`：用户实机确认旧 search setup 崩溃已解决。
- 怪物 static-member guard：固定 DLL 元数据门禁。
- Monster target fanout：63 个可确定 move 已支持；Knowledge Demon 多人远端 Choice 继续 fail closed。

## Multiplayer

| 能力 | 状态 |
|---|---|
| MP-0 Core / lifecycle | PASS |
| MP-1 Advisor 受控 Smoke | PASS |
| MP-2A 单动作 Safe Execute | PASS |
| MP-2B 连续动作 + 远端干扰中止 | PASS |
| MP-2C bounded N-action | PASS |
| Reactive Carry / Safe EndTurn | PASS |
| Local cross-turn T1→T2→T3 | PASS |
| Safe Auto 连续 3 本地回合 | PASS |
| Carry Ranking R1 | PASS |
| Carry Ranking R2 decisive | UNVERIFIED |
| S1 斩杀窗口公开伤害 → 新根重算 → 新授权部署 | 定向实机审计通过，见当前交接；不等同于 MP2B normal / Reactive Carry B |
| S1 显式采用后的重复 RNG 失效恢复 | UNVERIFIED |
| MultiplayerOnly runtime boundary | UNVERIFIED |
| Tag Team 原生双 Client 语义 | UNVERIFIED |
| TAG_TEAM Safe Execute whitelist | NOT ENABLED |
| 本地 Potion / 计划内 Choice | Safe Execute 已实现；完整 current-build Host/Client 场景仍待复验，不能记作全面 PASS |
| Replay 语义 / teammate-target / Multiplayer Instant | FAIL-CLOSED |

真实 multiplayer PASS 必须来自 Host/Client journal + 对应 validator；合成 validator self-test 不能替代实机。

## 当前多人测试工具

- 旧 CombatSolver Console Fixture 已删除。
- test-only `TheBookOfAges / GM Console` 保留完整 UI / GameActions / 网络同步实现，不进入正式 CombatSolver DLL。
- 当前先验证“所有端同构建 + 最小状态修改”的同步稳定性，再用它构造 MultiplayerOnly 场景。

## 证据读取原则

只在核验具体结论时读取对应本地问题包、运行日志或 Git history。不要为了普通开发把历史 evidence、performance 或旧报告批量加载进上下文。
