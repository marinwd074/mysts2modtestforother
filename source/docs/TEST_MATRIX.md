# CombatSolver 当前测试矩阵

> 只保留当前能力、验证入口和未验证边界。历史批次与旧数字从 Git history / 定向 evidence 查。

## 静态 / 构建

| 层 | 入口 | 证明范围 |
|---|---|---|
| Target | `tools/verify-target-version.ps1` | 0.107.1 / RitsuLib / compatibility symbol |
| Architecture | `tools/verify-refactor-boundaries.ps1` / `.sh` | 依赖和职责边界 |
| Architecture rule contracts | `python3 tools/verify-refactor-boundaries-tests.py` | 当前 Linux 门禁正例 + 八项负向变异；五组规则跨脚本静态等价，不代替 PowerShell 执行 |
| Repository | `tools/verify-repository-hygiene.ps1` | Git 跟踪文件、Markdown 本地链接、项目版本日志与 LFS 边界 |
| L1 contracts | `tools/run-contract-tests.ps1` | Search / Prediction / Multiplayer 纯合同 |
| Release | `dotnet build CombatSolver.csproj -c Release` | 当前本机真实依赖下编译 |

快速 Linux main push / PR 运行 Target、Architecture、Repository；定向 Windows 回归和手动 pinned 矩阵按工作流各自触发规则执行。CI 只能证明其实际执行的层级；没有日志/steps 的 runner 失败不能记作源码 FAIL。

`U0U1PinnedHarness rng-restore` 对照旧 RNG 还原的计数器、状态、后续随机值及父状态隔离；完整请求对照复用 `request-hydration-benchmark-off`，高计数器压力夹具为 `multi-hit-high-counter`。不替代实机搜索或退出取证验证。

`U0U1PinnedHarness turn-based-energy` 对照 PaelsFlesh / Bread 原生最大能量：144 次根/未来/live 回合及熔化查询、1280 次 DOP=4 查询、4 次完整 EndTurn 重放，核对全分支状态/RNG及真实回合隔离。`request-hydration` 另覆盖异常成员 Faulted、释放与重复 Dispose 的单次贡献。

`SearchInteractionChecks` 是无游戏 .NET 9 合同：直接链接生产候选交互 module 与进度 DTO，覆盖发布/批准/显示/采用/后台更新/完成、清理发布不重置显示/接管、无 ordinary result 的显式 seed 取消、版本与动作分别失配、TurnSetup 原有无 seed 接管、锁外单次物化、只读列表、清理与停止结果 stamp。外部搜索/游戏 DTO 使用最小替身，不宣称验证完整 PlanAction 嵌套选择、搜索质量或真实部署。`U0U1PinnedHarness completion-scope` 保留真实类型下的 scope/前台排序/显示合同；正例 seed 与预览同版本，并独立拒绝同动作不同版本、同版本不同动作的反例。

`continuation-replay` 另用受控 GC/native held tasks 驱动真实 Controller Defer/Resume/dispatcher，核对 replay 引用、reason/deploy 的两轮往返、queued 取消/替换/epoch 拒绝、noncancel fault exact-owner 清理；实际 owner/epoch/CombatManager-state guard 保留，仅记录最终 UI entry 参数。它不是实际 GC/native-action 触发或 RequestSearch UI/live-root 的运行证明。

`U0U1PinnedHarness continuation-replay` 另覆盖已物化 incumbent 的显式采用：Coordinator 选择/worker 最终结果保留采用范围与屏幕身份，普通完成不获得采用标记；连续两次 RNG 新根重放核对质量、不同路线身份及 live 隔离。当前标记修复的 Host/Client 复测仍 UNVERIFIED。

`completion-scope` 另覆盖真实 `SolverSearchSession`：factory/注册失败、取消前启动、worker 完成而 callback 仍排队、同步 callback、实际 Controller 的 stale/抛异常完成与重入 Cancel/Drain；核对四个 owner 各一次 scheduled/completed/CTS 计数。测试临时设置并恢复固定游戏 main-thread ID，未启动 UI；Release 的 tracker 桥接为 inert，活动无人 activity 分支只静态核对。

`U0U1PinnedHarness completion-scope` 另链接真实 `SolverDeploymentSession` / classifier，检查计划快照、Safe 容量、EndTurn-only、forecast/不支持边界、启动/拒绝竞争、同步完成、启动异常及 fault drain；调用实际 Controller 完成入口核对拒绝时一次性释放 CTS 和旧会话隔离，并在同步 factory 前缀重入 CancelDeployment/Drain，核对 Task 附着与真实操作完成前不会释放。它不启动真实 UI 或原生部署。

`PreCombatWorkerSessionChecks` 链接生产 session、公开 contracts 与 protocol/JSON 源码，使用真实临时协议文件和受控进程 adapter；另以两个真实测试子进程核对只停止精确 owner。覆盖 result/ready/cleanup 顺序、旧 requestId、Held、取消、格式错误、进程退出、停止超时与失败后保留所有权，以及停止确认期间状态查询不阻塞。`PreCombatRequestChecks` 覆盖公开完成适配不以取消或 live drift 覆盖退出未确认失败。它们不启动真实游戏，也不证明 Windows 镜像/Steam/worker 完整流程通过。

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
| Potion / Choice / Replay / teammate-target / Multiplayer Instant | FAIL-CLOSED |

真实 multiplayer PASS 必须来自 Host/Client journal + 对应 validator；合成 validator self-test 不能替代实机。

## 当前多人测试工具

- 旧 CombatSolver Console Fixture 已删除。
- test-only `TheBookOfAges / GM Console` 保留完整 UI / GameActions / 网络同步实现，不进入正式 CombatSolver DLL。
- 当前先验证“所有端同构建 + 最小状态修改”的同步稳定性，再用它构造 MultiplayerOnly 场景。

## 证据读取原则

只在核验具体结论时读取对应本地问题包、运行日志或 Git history。不要为了普通开发把历史 evidence、performance 或旧报告批量加载进上下文。
