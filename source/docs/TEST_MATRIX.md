# CombatSolver 当前测试矩阵

> 本文只保留当前测试入口、最近批次、覆盖范围、重跑方式和已知边界；退役快照由 [文档历史索引](history/README.md) 说明并可从 Git 历史恢复。当前规则与职责以 [`source/AGENTS.md`](../AGENTS.md) 和 [`ARCHITECTURE.md`](ARCHITECTURE.md) 为准。

## Multiplayer Lab（当前）

- Multiplayer Lab snapshot sync：`test-headless-runtime.ps1 -MultiplayerSnapshot` 通过并已接入 `run-contract-tests.ps1`，持续覆盖持久 base 保留、HostVanilla 对 CombatSolver 构建变化不重建、CombatSolver/RitsuLib managed overlay 原子替换与残留删除、底座变化/底座篡改全量修复、`-ForceRebuild`、live fail-closed、schema 2 split marker、ownership 清理和 `prepare-instances` 分项输出；无管理员符号链接权限的环境会跳过 reparse fixture，运行时拒绝逻辑仍由静态边界和既有 profile 安全合同覆盖。
- MP-0 Core：`PASS`；MP-0 Hardening lifecycle：`PASS`（Host 退出并重建房间后 Client 重新加入、Ready、再次进入战斗）；当前受控 MP-0 矩阵为 `PASS`，Host 重建后的证据见 Phase 0 JSON。
- MP-1 Advisor：`SMOKE_PASS`（受控范围；静态合同/Release 已通过，fresh `-bbfix` 真实复验 `SEARCH_COMPLETE=5`、`SEARCH_STALE=1`、`FAIL_CLOSED=0`、`SEARCH_FAILURE=0`，并有原生完成通知与路线回放），默认仍是 Probe，只有 `COMBATSOLVER_MULTIPLAYER_MODE=advisor` 显式 opt-in；MP-2 Safe Execute 显式一动作 Smoke `PASS`，默认仍是 Probe。
- MP-1 Advisor Stability 追加受控轮（2026-09-20）：Host 重建房间后 Client 重新加入的无药水战斗记录 `SEARCH_START=4`、`SEARCH_COMPLETE=3`、`FAIL_CLOSED=0`、`SEARCH_FAILURE=0`，Probe `130/130` 全部只读且无动作入队/自定义网络包；随后非空远端药水复验记录 `FAIL_CLOSED=7`、`SEARCH_SETUP_FAILURE=7`，消耗药水后再完成 2 次搜索，整体稳定性仍为 `PARTIAL`。证据见 `multiplayer/evidence/mp1-advisor-potion-2026-09-20.json`。
- Advisor root/phase 合同：`MultiplayerRootCaptureChecks` 15 项通过；精确 `BurningBlood` 可省略，未知远端遗物继续 fail closed，远端私有遗物清单仍不可访问，EndTurn/side-turn/block scaling 边界及单人 EndTurn 保持均有窄合同。
- MP-2A 合同基线：`MultiplayerSafeExecuteChecks` 固定非 PlayCard、缺卡、本地所有权、自动 EndTurn、Replay、选择、多人专属卡、缺失/队友目标、正式精确 token 与 Lab-only capability token/evidence/owned-instance 三重门禁；历史正式 Host/Client 一动作 Smoke `PASS`。
- MP-2B 历史合同：`MultiplayerSafeExecuteChecks` 原 39 项通过，覆盖两动作上限、`Authorized → Executing → AwaitingWorldUpdate → Revalidating` 会话、WorldVersion 冲突/单调接受、旧授权中止、预期本地变化、远端/未知变化、动作不匹配和不稳定世界的 fail-closed 判定。
- MP-2C bounded N-action 合同：基线为 40 项通过，覆盖六动作有限上限、5-action session 迁移、连续安全前缀、首个不安全动作硬边界、超过 ceiling、完成/中止 session 不可重用等检查；Release 构建与真实正常/干扰 Smoke 均已通过。
- Reactive Carry 合同：`MultiplayerSafeExecuteChecks` 当前 53 项通过，新增 Safe EndTurn 单次授权、EndTurn 前边界复核、旧 session/route/generation/authorization 失效、Fresh Probe/Search、WorldVersion 单调和 reactive replan 场景；`test-reactive-carry-validator.ps1` 合成自测通过。
- Multiplayer Carry Ranking v1 合同：新增 `MultiplayerCarryRankingChecks` 8 项通过，覆盖无远端 neutral、等风险保持 baseline、明确公开威胁移除、低远端有效生命风险、Unknown 目标 neutral、远端私有不可见和 WorldVersion/public fingerprint 变化重建上下文；纯 evaluator 不读取 Runtime/UI/live state。
- Multiplayer Carry Ranking v1 当前实现：公开上下文只在显式 Advisor/Safe Execute 的 main-thread root capture 中建立，最终排序只在既有本地安全/资源/敌方生命键之后作 tie-break；单人和默认 Probe 不启用，R1/R2 尚未作为实机证据收口。
- Multiplayer Local Cross-Turn 当前源码合同：`MultiplayerLocalCrossTurnChecks` 10 项通过，覆盖策略边界、T1→T2、T1→T2→T3 本地投影、拒绝队友动作、精确 continuation、远端公开变化/WorldVersion 拒绝、目标移除身份变化、当前回合执行授权边界和 T3 可出牌平局选择。Release 构建 0 errors（保留 2 条既有 `CS9113`）。
- Multiplayer Continuation Validation 修复（`94c6497`）：T2 continuation 校验前执行 fresh Probe；严格 WorldVersion `>` 门禁不变；拒绝路径覆盖 cached turn 缺失、本地状态差异、WorldVersion 未推进及远端公开差异，并保证 local exact + multiplayer reject 正常 fresh replan、不因空 diff 越界。Release 构建 0 errors，10 项 Local Cross-Turn 合同通过。
- Multiplayer Local Cross-Turn 实机：T3 Fix Client 在 `X7TJJK1T4W` / `NIBBITS_WEAK` 中通过 T3 空推荐复测；generation 3 route 保留当前回合 `DEFEND_IRONCLAD` + 两张 `STRIKE_IRONCLAD`，request 3 原生执行并进入 EndTurn。既有 T3→T4 精确复用、新授权和 mismatch→Fresh Search 均无回归；无 `SEARCH_SETUP_FAILURE`/越界异常。摘要见 `multiplayer/evidence/local-cross-turn-t3-fix-smoke-2026-09-20.json`。
- Multiplayer Local Cross-Turn X2 实机：三人 `F23XG9KSJD` / `FUZZY_WURM_CRAWLER_WEAK` 中 Client 1001 主动打击公开敌人，诊断观察到 `E0.hp 180→171`；旧 continuation 因 `local_state_mismatch` 被拒绝，随后 `fresh_probe=true`、`fresh_capture=true`、generation 2 Fresh Search 生成新 route 和新 authorization，旧 future action 未入队。摘要见 `multiplayer/evidence/local-cross-turn-x2-smoke-2026-09-20.json`。
- Multiplayer Local Cross-Turn T3 tie-break 与 EndTurn 边界：T3 可执行牌优先的运行复测 `PASS`；真正 EndTurn-only 的允许性由 `MultiplayerLocalCrossTurnChecks 10/10 PASS` 保持。专用 `MP_LOCAL_XTURN_CONTINUATION_MISSING` 运行夹具本轮未触发，登记为 `UNVERIFIED`，不能替代为普通 mismatch 证据。
- MP-2B 真实 Smoke：2026-09-20 Host/Client 正常两动作运行返回 `MULTIPLAYER_MP-2B_PASS`；两动作之间远端干扰运行返回 `MULTIPLAYER_MP-2B_REMOTE_ABORT_PASS`，没有第二个原生动作并完成新搜索。摘要见 `multiplayer/evidence/mp2b-smoke-2026-09-20.json`。
- MP-2A 证据验证器：`test-mp2a-validator.ps1` 在 CI 中只验证 parser 的 PASS/FAIL/UNVERIFIED 判定；真实运行使用 `validate-mp2a-results.ps1`，必须看到 Safe Execute capability、恰好一个原生 `PlayCardAction`、无药水/自动 EndTurn、动作后 WorldVersion 失效和新搜索，才能报告 Smoke PASS。
- MP-2B/2C 证据验证器：`validate-mp2b-results.ps1` 已通过 `-MinActions` / `-MaxActions` 泛化为 bounded N-action 校验器；默认参数仍兼容历史两动作运行。当前正常合成用例 6 个、远端干扰合成用例 6 个均通过，其中包含 5-action 正常与两张牌后中止；真实 MP-2C 运行要求同一 request ID 的连续原生 `PlayCardAction`、每动作重验证、WorldVersion 前进、无 forbidden action、`end_turn=false` 和 fresh search，干扰则要求不存在下一 action。
- MP-2C 实机：正常运行由一次点击自动完成 3 张牌，远端干扰运行完成 2 张后中止且不捕获下一张原生动作；两次均由对应验证器 PASS。摘要见 `multiplayer/evidence/mp2c-smoke-2026-09-20.json`。
- Reactive Carry 实机：Smoke A/B/C 均 PASS。A 在 request 1 完成安全牌序列和 native EndTurn 后进入下一回合 fresh search；B 在 request 2 的 EndTurn 与 fresh search 之间观察到队友公开变化；C 以 requests 1/2/3 连续完成 local turns 1/2/3，无 remote abort、旧授权复用或自定义网络路径。机器摘要见 `multiplayer/evidence/reactive-carry-smoke-2026-09-20.json`。
- MP-2A 日志落盘门禁：`DiagnosticLogTests` 已纳入 L1 合同套件，覆盖 journal 正常退出时排空后台写队列；Multiplayer Lab 默认优雅停止 owned 进程，`-Mode Force` 仅用于显式清理且不能作为完整 journal 证据。
- Advisor 复验修正：side-turn relic 只读取已捕获参与者；原生多人 block scaling 仅在 enemy/powered-block 路径计算；EndTurn replay 只处理 `RootCapturedPlayers`，未知远端 turn 仍 fail closed。
- Advisor Smoke 只读合同：首轮 Probe `51/51` 条为 `readOnly=true`，`actionsEnqueued=0`、`customNetworkPacketSent=0`；生命周期复验追加 Probe `257/257` 只读、无动作入队/自定义网络包。路线动作仅为模拟回放，未启用 Safe Execute、自动 EndTurn、药水或选择。
- post-MP1 固定工作量单人 spot 对照：当前源码 3 个独立样本均为 `expanded=3528`、`transitions=10156`，路线/结果 identity 与历史 Batch 7 baseline 一致，Gen2 与 >50/100 ms 帧均为 0；耗时/分配仅作非交错对照，不宣称稳定加速。证据见 [`runtime-evidence/20260920-post-mp1-performance`](../../runtime-evidence/20260920-post-mp1-performance/)。
- Advisor Smoke 机器可读摘要：[mp1-advisor-smoke-2026-09-19.json](multiplayer/evidence/mp1-advisor-smoke-2026-09-19.json)；完整运行日志仍保留在本地 `.local/`。
- 机器事实与证据索引：[phase0-matrix-2026-09-19.json](multiplayer/evidence/phase0-matrix-2026-09-19.json)；运行器：[tools/multiplayer-lab/](../tools/multiplayer-lab/)。
- 定向复跑：`validate-phase0-results.ps1 -Phase MP-0A` / `-Phase All`；双 Client 公开状态使用 `compare-probe-public-state.ps1`。这些入口不会绕过真实生命周期或只读证据门禁。
- 生命周期边界：游戏要求 Host 退出并重新创建房间后 Client 才能重新加入；重连后的 Advisor 若需要未捕获远端私有药水库存会 `FAIL_CLOSED`，该结果不计作搜索通过。

## 2026-09-18：三份新问题包共因回归修复

- 代码覆盖 `FUEL` 原生“先加能量、再抽牌”顺序，以及 v0.107.1 `SlowPower` 的 `SlowAmount` 生命周期；对应问题包为 `AEONGLASS_BOSS`、`SOUL_NEXUS_ELITE`、`BYGONE_EFFIGY_ELITE`。
- L0/L2：Release 与 CompatibilitySmoke 构建通过（0 errors，2 条既有 `CS9113`）；`verify-refactor-boundaries.ps1` 输出 `REFACTOR_BOUNDARIES_OK search_files=119`；`verify-target-version.ps1` 输出 `TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- L3：0.107.1 私有 headless 游戏进程的 `FIRST_TURN` CompatibilitySmoke 通过：`PASS: native 0.107.1 first-turn search; actions=20; incremental verification enabled`；日志确认 63/63 CombatSolver 补丁应用成功。脱敏证据见[`runtime-evidence/20260918-issue-fix-compat-smoke`](../../runtime-evidence/20260918-issue-fix-compat-smoke/)。
- 限制：生产构建按设计排除 `src/Testing`，因此通用 `run-unattended-test.ps1` 与 `SLOW-TURN-RESET-FORK` 不作为本批次通过依据；三份问题包的完整游戏内回放仍待用户用输出 DLL 实测。

## 2026-09-18：架构优化 Batch 17——文档事实瘦身与构建产物清理

- 文档门禁：`source/AGENTS.md` 不再把已完成批次写成当前工作项；`ARCHITECTURE.md` 的 Runtime 所有权与 `PatchRegistration.cs` 对齐；开发笔记和测试矩阵标明历史证据边界，重复 Batch 8 标题已删除。
- 验证：Release 构建 0 errors（2 条既有 `CS9113`）、`REFACTOR_BOUNDARIES_OK search_files=122`、`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`、`git diff --check` 通过。详见 [Batch 17 报告](performance/documentation-fact-slimming-20260918.md)。
- 清理：删除 14 个工具 `bin/obj` 目录，约 50.5 MB，可由下次构建恢复；保留游戏本体、活动运行数据、`.godot`/`.local` 和历史报告引用的性能 JSON。最终 DLL 已输出供用户可见游戏测试。

## 2026-09-18：架构优化 Batch 12——BeamRetentionPolicy Cycle partial

- 结构：循环 startup/exit portfolio、风险桶代表、探测族比较、票据租约和有界保留选择移至 `CombatBeamSolver.BeamRetentionPolicy.Cycle.cs`；`Retention.cs` 保留调用/共享桥接/最终票据结算，`CyclePlanning.cs` 保留周期生命周期与预算。
- 验证：Release 与 CompatibilitySmoke 构建 0 errors（2 条既有 `CS9113`），结构门禁与目标版本门禁结果记录于 [Batch 12 报告](performance/beam-retention-policy-cycle-split-20260918.md)。本批次不重复上一批已超时的私有运行；最终 DLL 由用户在可见游戏中测试。

## 2026-09-18：架构优化 Batch 13——AfterBlockBroken Hook 兼容边界

- 结构：`AfterBlockBroken` 的版本相关原生参数列表移至 `src/Compatibility/Sts2HookCompatibility.cs`；Hook 镜像不再直接包含该版本条件编译。
- 验证：Release 与 CompatibilitySmoke 构建、结构门禁和目标版本门禁结果记录于 [Batch 13 报告](performance/after-block-broken-hook-compatibility-20260918.md)；最终 DLL 由用户在可见游戏中测试。

## 2026-09-18：架构优化 Batch 14——回合准备补丁目标参数兼容边界

- 结构：`SetupPlayerTurn` 与 `RunAutoPrePlayPhase` 的目标参数数组和 `CombatTurnState` 解析移至 `src/Compatibility/Sts2TurnSetupCompatibility.cs`；Harmony Prefix 的原生参数条件编译仍保留在补丁入口。
- 验证：Release 与 CompatibilitySmoke 构建、结构门禁和目标版本门禁结果记录于 [Batch 14 报告](performance/turn-setup-target-compatibility-20260918.md)；最终 DLL 由用户在可见游戏中测试。

## 2026-09-18：架构优化 Batch 15——GitHub 架构边界门禁

- CI：`compatibility.yml` 的 `static-consistency` Job 新增 `Verify architecture boundaries`，执行 `source/tools/verify-refactor-boundaries.ps1`。
- 本地等价验证：结构门禁通过；本批次未改变生产源码或测试输入，Release DLL 重新构建并输出供用户实测。详见 [Batch 15 报告](performance/architecture-boundary-ci-gate-20260918.md)。

## 2026-09-18：架构优化 Batch 16——Entry 补丁注册职责拆分

- 结构：`Entry.Initialize` 只调用 `PatchRegistration.ApplyRequiredPatches`；完整 patcher 注册清单及版本条件继续保留在 `src/Runtime/PatchRegistration.cs`。
- 验证：Release 构建、结构门禁、目标版本门禁和最终 DLL 记录于 [Batch 16 报告](performance/entry-patch-registration-split-20260918.md)；本批次为纯职责移动，不重复行为 smoke。

## 2026-09-18：架构优化 Batch 11——BeamRetentionPolicy CrossTurn partial

- 结构：跨回合 retention 选择图移至 `CombatBeamSolver.BeamRetentionPolicy.CrossTurn.cs`；`Retention.cs` 保留剪枝协调调用，`CrossTurnPlanning.cs` 保留证据传播与 stand-pat 语义状态附着。未改变候选顺序、风险分带、探测预算或搜索算法。
- 验证：Release 与 CompatibilitySmoke 构建 0 errors（2 条既有 `CS9113`），结构门禁与目标版本门禁结果记录于 [Batch 11 报告](performance/beam-retention-policy-cross-turn-split-20260918.md)。本批次不重复启动上一批已超时的私有运行；最终 DLL 由用户在可见游戏中测试。

## 2026-09-18：架构优化 Batch 10——BeamRetentionPolicy Mutation partial

- 结构：完整有序变异职责移至 `CombatBeamSolver.BeamRetentionPolicy.Mutation.cs`；主文件保留共享协调器/通用排名，`CombatBeamSolver.OrderedMutationRetention.cs` 保留账本与最终提交边界。未改变算法、候选顺序或搜索预算。
- 纯移动核对：类型 152 行、首段 1,753 行、后续 Mutation 方法图 2,634 行逐字等价；Release + CompatibilitySmoke 编译 0 errors（2 条既有 `CS9113`），结构门禁 `REFACTOR_BOUNDARIES_OK search_files=120`，目标版本门禁 `0.107.1/0.107.1/STS2_01071`。
- 兼容运行：本批次额外私有 `FIRST_TURN` 尝试超时（180 秒），不记为通过；没有据此推导行为回归。此前 issue-fix 的 0.107.1 首回合 smoke 通过结果继续作为启动/加载基线，最终 DLL 由用户在可见游戏中测试。
- 汇总见[Batch 10 报告](performance/beam-retention-policy-mutation-split-20260918.md)。

## 2026-09-18：架构优化 Batch 9——BeamRetentionPolicy Potion partial

- 结构：最终政策资格记录/比较、药水配额、药水谱系分组和 `UsesPotion` 拆至 `CombatBeamSolver.BeamRetentionPolicy.Potion.cs`，仍为同一嵌套 `BeamRetentionPolicy` partial，未改变搜索算法或候选顺序。
- 验证：Potion 专属源码逐段等价、Release 与 CompatibilitySmoke 构建均 0 errors（2 条既有 `CS9113`）、结构门禁 `REFACTOR_BOUNDARIES_OK search_files=119`。本轮无新增运行时结论，生产 DLL 留给用户进行可见 Steam 实机测试。汇总见[Batch 9 报告](performance/beam-retention-policy-potion-split-20260918.md)。

## 2026-09-18：架构优化 Batch 8——BeamRetentionPolicy Choice partial

- 结构：路由／回合开始选择辅助逻辑拆至 `CombatBeamSolver.BeamRetentionPolicy.Choice.cs`，仍为嵌套 `BeamRetentionPolicy` partial；未引入接口、服务或策略替换，候选顺序和搜索算法不变。
- 校验：移动块逐段等价（290 + 171 行）、Release + CompatibilitySmoke 构建 0 errors、结构门禁 `REFACTOR_BOUNDARIES_OK search_files=118`。
- 固定 0.107.1 游戏进程 smoke：`expanded=3528`、`transitions=10156`，与 Batch 7 candidate-03 的路线和结果身份均 identical；`gen2=0`、>50/100 ms 帧为 0。专用 smoke 不写常规 result，外层启动器收尾提示不作为 unattended 通过/失败判定。原始 JSON 见[`runtime-evidence/20260918-batch8-beam-choice-split`](../../runtime-evidence/20260918-batch8-beam-choice-split/)，汇总见[Batch 8 报告](performance/beam-retention-policy-choice-split-20260918.md)。

## 2026-09-18：架构优化 Batch 7——Hook 监听索引候选

- 代码范围：`MirroredHookListenerLayout` 的按 mask 惰性索引与 `HookListenerEnumerable` 的分支快照索引遍历；不改变监听顺序、重复成员、PendingChoice 停止或搜索策略。
- 固定 0.107.1 headless A-B-B-A-A-B 六次：baseline `3169.203 ms / 370,614,600 B`，candidate `3145.065 ms / 370,677,333 B`；`expanded=3528`、`transitions=10156`、route/result identity 全部一致，GC Gen2 与 >50/100 ms 帧均为 0。耗时下降约 0.762%，分配变化约 +0.017%，仅作为持平范围记录；未测可见 Steam 性能。
- `COMPAT1071_FIRST_TURN` 候选产物通过：`PASS: native 0.107.1 first-turn search; actions=20; incremental verification enabled`。专用 smoke 不写常规 result 文件，外层启动器的退出提示不记为 unattended 请求通过。原始 JSON 见[`runtime-evidence/20260918-batch7-hook-index`](../../runtime-evidence/20260918-batch7-hook-index/)，汇总见[性能报告](performance/compat1071-hook-index-20260918.md)。

## 2026-09-18：架构优化 Batch 6——搜索性能基线

- 新增 `COMPAT1071_PERFORMANCE_BASELINE`：在 0.107.1 游戏进程内固定 IRONCLAD/NIBBIT、`COMPAT1071` seed、生产 profile、beam、DOP1 和 5000 ms 固定预算，只记录 expanded、transitions、耗时、worker 分配、bytes/transition、GC、主线程帧间隔以及完整路线身份；不改变搜索算法、评分、Beam 或质量策略。
- 该专用模式写出自己的 JSON，不写常规 unattended `result.json`；启动器的收尾提示不作为 unattended 通过/失败判定。正式数值与环境限制见 [Batch 6 baseline 报告](performance/compat1071-search-baseline-20260918.md)。

## 2026-09-18：架构优化 Batch 5——完整战斗生命周期 smoke

- 新增 `COMPAT1071_FULL_BATTLE`：从 0.107.1 原生回合设置选牌开始，经过全自动部署和实际卡牌动作，确认 `CombatEnded` 后等待引用屏障；断言 `IsSearching`、`IsDeploying`、`FullAutoEnabled`、自动搜索暂停、`PlayerTurnSetupCoordinator` 活动会话以及 GC/No-GC 活跃状态均已清理。
- 验证结果：专用 smoke 写出 `setup_turn=2; selected=True; deployed=True; next_turn=2; route_reuse=False; combat_in_progress=false; cleanup=search,deployment,turn_setup,gc; lifecycle=2>1; gc_ends=1; gc_losses=0`；外层启动器未生成常规 result，按既有口径不记为完整 unattended 请求通过。

## 2026-09-18：架构优化 Batch 2——测试协议边界

- 生产边界：`src/Testing/**/*.cs` 保持从 `CombatSolver.csproj` 排除；新增 Runtime 最小桥后，生产 API 不再引用完整 `UnattendedTestRequest` / `UnattendedTestResult` / `UnattendedTestFiles`。
- 验证：Release + CompatibilitySmoke 编译 0 errors（保留既有 2 条 `CS9113` 警告）；contract tests `5/5`、PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=117`、目标版本门禁 `0.107.1/0.107.1/STS2_01071`。
- 代表性 smoke：FIRST_TURN 在游戏进程中写出 native 0.107.1 首回合搜索通过、20 个动作并启用增量核验；启动器因该专用模式不写常规 result 文件报告 `exit_code=0` 收尾异常，因此不记为完整 unattended 请求通过。

## 2026-09-18：架构优化 Batch 3——Runtime 目录整理

- 物理边界：问题报告移至 `src/Diagnostics/BugReports`，统计/在线通知移至 `src/Diagnostics/Telemetry`，Replay/Showcase 移至 `src/Replay`；namespace、逻辑和主程序集保持不变。
- 验证：主程序集 Release + CompatibilitySmoke 构建 0 errors（2 条既有 `CS9113` 警告），PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=117`；FIRST_TURN smoke 写出 20 动作、增量核验开启的 native 0.107.1 通过结果。独立工具项目因缺少 `project.assets.json` 未完成编译，未记为通过。

## 2026-09-18：架构优化 Batch 4——SolverController SearchLifecycle

- 结构变更：`SolverController.SearchLifecycle.cs` 承担搜索请求、worker 回调/结果发布、root barrier 延迟/取消和搜索引用释放；`SolverController.cs` 保留高层协调与共享状态，未改变 namespace、签名或执行顺序。
- 验证：Release + CompatibilitySmoke 构建 0 errors（2 条既有 `CS9113` 警告）、结构门禁 `REFACTOR_BOUNDARIES_OK search_files=117`；FIRST_TURN smoke 以 20 个动作和增量核验开启写出 native 0.107.1 通过结果。外层脚本未生成常规 result，未记为完整 unattended 通过。

## 2026-09-18：架构优化 Batch 4——SolverController Deployment

- 结构变更：`SolverController.Deployment.cs` 承担全自动部署、动作/选择执行、延迟、取消和部署引用释放；未改变 namespace、签名、动作顺序或搜索策略。
- 验证：Release + CompatibilitySmoke 构建 0 errors（2 条既有 `CS9113` 警告）、结构门禁 `REFACTOR_BOUNDARIES_OK search_files=117`；FULLAUTO smoke 验证回合准备选牌、部署、进入下一回合、路线复用及战斗仍在进行。外层脚本未生成常规 result，未记为完整 unattended 通过。

## 2026-09-18：架构优化 Batch 4——SolverController Continuation

- 结构变更：`SolverController.Continuation.cs` 承担跨回合续用校验、路线采用、回合准备续接预览和重算/手动分歧审计；未改变 namespace、签名、异步顺序或部署动作。
- 验证：Release + CompatibilitySmoke 构建 0 errors（2 条既有 `CS9113` 警告）、结构门禁 `REFACTOR_BOUNDARIES_OK search_files=117`；FULLAUTO + route reuse smoke 验证选牌、部署、下一回合、路线复用和战斗生命周期。外层脚本未生成常规 result，未记为完整 unattended 通过。

## 0.40.2：v0.107.1 问题包回归修复（2026-09-18）

- 问题包归因：CubeX `9bd8ad30`、Mawler `91f2bf97`、The Kin `03fc1f75`/`5023e5ca`/`c45c1331` 的 `ROCKET_PUNCH` 费用差异共享同一镜像根因；Phrog `c12596f0` 的部署漂移、Ruby Raiders `012a00da`/`6ea89c43` 的 Juggling Hook、Slimes `728a43c5` 的 Regen Hook 使用已有的窄化／版本条件修复。
- 代码验证：`RocketPunch` 镜像改为 `EnergyCost.SetUntilPlayed(0)`；活动源 Release 编译成功（0 errors，1 条既有 `CS9113` 警告）。
- `FIX-ROCKET-PUNCH-CUBEX` / runId `4aed021648054af0b6d3963c069b7004`：headless 使用 v0.107.1/RitsuLib 0.6.2/CombatSolver 0.40.2 启动成功，60/60 补丁应用成功；从问题包 `start` 执行 `DeploySolver` 在 120 秒内超时且未写结果，因此不记为 Passed。证据：[launcher-result.json](../../runtime-evidence/2026-09-18-rocket-punch-cubex-9bd8ad30-clean/launcher-result.json)。

## 0.40.2：多策略路线搜索默认关闭与大战损引导（2026-09-17）

- 设置与 UI 合同已更新：新安装默认关闭多策略路线搜索；245→246 迁移只推进版本，完整保留玩家已有的开启／关闭状态与永久隐藏横幅选择。多宽度路线精炼仍默认开启且没有独立横幅。
- 两条玩家引导统一以预计损失至少 8 HP 为「大战损」门槛：7 HP 及以下不显示，8 HP 起显示。多策略横幅还要求功能关闭，点击可永久隐藏；主动开启功能同样不再提示。
- `NOVELTY-PORTFOLIO-SETTINGS` / `e53b50614756461481b31d5902f5f01b` Passed，22.47 秒：验证新安装默认关闭、246 迁移分别保留玩家已有的开启和关闭状态、设置往返、性能页控件、请求冻结，以及多策略与性能预设两条引导共同采用 7／8 HP 边界。
- `UI-LOCALIZATION` / `484adb3b62f34561b54ef4a4609dbde0` Passed，25.67 秒：eng/zhs/zht 共 426 项目录，「大战损」引导中英文文本、功能关闭/开启、7／8 HP 边界、点击永久隐藏与 SpeedX 引导合同通过。Release 编译 0 警告、0 错误；PowerShell 结构门禁 `search_files=114` 通过；未启动可见 Steam。

## 0.40.2：请求级搜索进度（2026-09-17）

- 控制器 UI 合同已更新：同一请求从主搜索切到后续搜索时，即使当前子搜索节点数重置，进度仍按 10 秒请求预算从 5% 推进到 6%；累计世界线与候选路线展示保持原口径。合同另断言超过软时间预算后仍固定显示 95%，避免排空与最终复核被显示为已经完成。
- 本轮只执行 Release 编译与结构门禁；按用户要求未运行无人战斗或可见 Steam 测试，以上合同改动已编译但未在游戏进程中执行。

## 0.40.2：变形池根快照缓存（2026-09-17）

- `TRANSFORMATION-POOL-CACHE` / `c8c552fc5f52400b849c1a77a77fefce` Passed，23.89 秒：断言缓存序列与上游 `GetUnlockedCards` 逐实例同序、跨 `Fork` 不可变共享、可变池被拒绝、外来约束被拒绝、外来池被拒绝、规范无色池（Quest/Event/Ancient/Token 回退）被正确服务且同序、缓存路径与原生路径产出同一张牌且 `CombatCardSelection` 五字段 RNG 状态与完整预测延续状态一致、父模拟与实机根未被改动（`comparisons=4`）。使用隔离无头实例并在完成后退出，未启动可见 Steam。
- 该契约初版在 `Transformation pool accepted a changed pool or constraint.` 失败。排查为**契约自身错误**：它断言无色池必须被拒绝，但无色池是合法回退池、本就应被服务；实现无缺陷。已改为具名的正/负断言并复跑通过。不把这次失败记作实现缺陷，也不把修正前的运行记作通过。
- 等价性：`tools/OfflineSearchHarness/compare_results.py` 对基线 `41f9478` 与候选产物逐字段比较，**7 个根全部一致**：crab@2000 170 字段、KAISER_CRAB_BOSS@6000 242、silent-discard@6000 192、QUEEN_BOSS@6000 152、THE_KIN_BOSS@6000 174、KNOWLEDGE_DEMON_BOSS@6000 212、THE_INSATIABLE_BOSS@6000 234，全部 `mismatched_roots=0`、无 `left_only`/`right_only`，覆盖 `solverMetrics`（排除时间/内存/GC）、`route` 每个动作、根 `ContinuationStamp` 与 `catalogFingerprint`。
- 固定工作量 A/B：同根、`VeryHigh`、beam 48、`--dop 1`、顺序 ABBA。KAISER_CRAB_BOSS @2000 节点 18.78 秒 → 9.07 秒（2.072 倍）。**压力场景**（沿用 crab 生成场景规格只换遭遇与幕索引，预算标定到基线 ≥20 秒）：KNOWLEDGE_DEMON_BOSS 54.11→14.76 秒（3.667 倍）、THE_KIN_BOSS 41.78→14.43 秒（2.895 倍）、KAISER_CRAB_BOSS 37.32→14.86 秒（2.511 倍）、THE_INSATIABLE_BOSS 23.63→10.79 秒（2.191 倍）；**基线 >20 秒的 4 个根加速比 2.191–3.667 倍**。不走变形路径的提前穷尽根为 1.041 倍（silent-discard）、1.426 倍（QUEEN_BOSS）；**对照组**把同批 Boss 遭遇改用默认薄牌组后三者全部提前穷尽、加速比 0.984 / 1.015 / 0.990 倍（收益为零，略低于 1.0 属 1–3 秒量级噪声，不记作退化）。Release 构建 0 警告、0 错误。
- 内存：每节点总分配 2.06 MB → 1.04 MB；但峰值工作集约 385 MB → 约 405 MB、峰值托管堆约 157 MB → 约 179 MB，**未改善**。峰值成因未取证，不作为通过项。
- **并行度 8** 复测（12000 节点、同根、顺序 ABBA）：厚牌组 2.583 / 2.398 / 2.178 / 1.865 / 1.696 倍（KAISER_CRAB_BOSS / KNOWLEDGE_DEMON_BOSS / THE_KIN_BOSS / QUEEN_BOSS / THE_INSATIABLE_BOSS），薄牌组对照组 1.000 / 0.989 / 0.983 倍。并行度不改变结论。
- **DOP 8 的字段级等价性不可用**：`compare_results.py` 报 `DIFFERENT`，但差异仅 `roundReplayPrefixCaptures` / `executionChoiceReuses` 两个调度计数器，`route` / `rootState` / `catalog` 全为 0 处；且**基线自比**在 DOP 8 下同样在这一个计数器上不同（A1 vs A2 7808 vs 7794），证明是并行调度非确定性而非语义差异。不把 DOP 8 的 `DIFFERENT` 记作实现缺陷，也不把它记作通过；字段级等价性以 DOP 1 的 7 根全一致为准。
- 结构门禁：Bash `tools/verify-refactor-boundaries.sh` 通过，`REFACTOR_BOUNDARIES_OK search_files=114`、退出码 0（增量 1 即本次新增的 `src/Search/RootCombatTransformationPoolSnapshot.cs`）。Release 构建 0 警告、0 错误。
- 未执行：可见 Steam 性能未测，上述倍数只是无头数据，不外推为实机收益。详见[性能报告](performance/transform-pool-root-snapshot-20260917.md)。

## 0.40.1：多策略回合准备选牌修复（2026-09-16）

- 夸克打包结构合同通过：只用现有 `CombatSolver-0.40.0.zip` 调用独立打包函数，临时产物为 15,728,765 字节，保留 5 个 CombatSolver 根条目并仅新增一个 13,663,763 字节的无压缩 `QUARK_UPLOAD_PADDING.bin`；RitsuLib 条目与嵌套 ZIP 均为 0，文件严格超过 15 MiB。未执行上传、网盘移动或正式发布。
- 日志站基线：0.40.0 共取得 12 份 `TurnSetupFailure` 问题包，覆盖烤手套、能力牌及多个职业/遭遇；12 份异常栈均进入 `RunNoveltyPortfolioPass -> CombatBeamSolver.RunNoveltyOpen`。11 份在 `BuildContinuations -> Replay` 因未回放准备选牌而找不到首张手牌，1 份由终结准备根进入 `Expand`。服务端筛选结果是玩家主动提交的问题包，不作为总体发生率统计。
- `NOVELTY-TURN-SETUP-CHOICE-0400` / `26117906a7a4464c83cc1a9a10ac803f` Passed：显式强制多策略路线搜索、固定 5 秒预算、DOP2，真实烤手套准备选牌被路线保留；最终搜索 1,578 个节点、10,669 次转移，3 回合零战损获胜，没有准备阶段失败。Release 构建 0 警告、0 错误。
- 两个全新无头实例在建局时停在原生 `There's another modal already open`，均到 120 秒后由启动器停止，未进入搜索且不计为回归失败或通过；改用此前已完成初始化的隔离实例后，同一请求正常通过。

```powershell
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId NOVELTY-TURN-SETUP-CHOICE-0400 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -Seed NOVELTY-TURN-SETUP-CHOICE-0400 -RelicsJson '[{"relicId":"TOASTY_MITTENS","addWithoutObtainedEffects":true}]' -FixedSearchBudget -SearchBudgetOverrideMilliseconds 5000 -SearchMaxDegreeOfParallelismForTest 2 -UseNoveltyPortfolioForTest -PerformancePresetForTest Low -ExpectedInitialSetupChoiceCountAtLeast 1 -ExpectedInitialSetupChoiceSourceId TOASTY_MITTENS -StopAfterInitialSetupAssertion -TimeoutSeconds 120 -ExitOnComplete
```

## 0.40.0：有界新颖性组合与设置迁移（2026-09-16）

- 引导横幅回归：`UI-LOCALIZATION` / `56c829a25d294a95bed3959f98322c7f` Passed，eng/zhs/zht 共 426 项目录，验证多策略与皮皮极速横幅的当前语言文案、点击永久隐藏和设置往返；`NOVELTY-PORTFOLIO-SETTINGS` / `1bb50f409cd843e797a2b16669219da4` Passed，验证精炼默认开启、多策略默认关闭、旧设置缺失横幅字段时采用显示默认值、两类横幅关闭选择持久化及搜索请求冻结。Release 构建 0 警告、0 错误；两项均使用隔离无头实例并在完成后退出，未启动可见 Steam，因此不把无头结果写成真实排版验收。
- 节点预算与强制精炼迁移后，离线预设合同通过：四档节点预算为 60,000 / 120,000 / 250,000 / 500,000，时间与 Beam 保持原值；自定义 1,000,001 节点的迁移断言已编译，迁移 243→244 强制开启精炼并保留 Custom、多策略、NoGC 与内存值，244 后再次关闭保持关闭。无胜利追加搜索原策略和 8 项请求合同通过，`BEAM_WIDTH_PORTFOLIO_OK checks=73`、PowerShell 结构门禁（`search_files=113`）及 Release 构建通过，构建 0 警告、0 错误。独占与并行无头模式各尝试一次 `NOVELTY-PORTFOLIO-SETTINGS`，均在 120 秒内未取得宿主资源，测试未启动且未停止现有实例，因此游戏内设置断言未记为通过。
- 合并 PR #102/#103 后的本轮审计：修正次段成员误触发长期资源 `RankBest` 的作用域，并把多宽度路线精炼改为默认开启。`BEAM_WIDTH_PORTFOLIO_OK checks=73`、新颖性 21+6+12,000+7+9 项离线合同、PowerShell 结构门禁（`search_files=113`）和 Release 构建均通过，构建 0 警告、0 错误。`NOVELTY-PORTFOLIO-SETTINGS` 已更新默认值断言并成功编译；本轮执行时独占无头槽持续被其他实例占用，120 秒准入超时，测试未启动，未记为通过，也未停止现有实例。
- 新增默认关闭的多策略开关，当前上游 `7f806de`、游戏 0.111.0、RitsuLib 0.6.2。14 个固定根的 28 份完整 Smart 请求全部运行成功，两边均 12 个完整胜利；其中 3 根投影战损下降，其他根战损相同。每份样本独立进程、交替 AB/BA，核对五份输入/原生开局 JSON，使用请求 `total_*` 指标。具体成本、反例与未完成胜利见[报告](strategy/bounded-novelty-search-20260916.md)。
- 离线合同：21 项参考调度 + 6 项祖先配额 + 7 项有界队列 + 9 项共享预算；12,000 个混合状态与参考新颖性完全一致。两个原生结构门禁均通过，`search_files=113`；最终 Release 11.48 秒、0 警告/错误。
- `GENERATED-NOVELTY-SEARCH` 加 `control-checks.flag` / `2d59455a85d34d82b28540efe4b4a12b` Passed：实际 DOP2 接管当前回合、逐动作接管已显示路线、取消向外传播、工作只计一次及 live/shadow 根不变。
- `UI-LOCALIZATION` / `49c873258c7f4a32a68311311f5078a7` Passed，eng/zhs/zht、424 项目录；`NOVELTY-PORTFOLIO-SETTINGS` / `60e2e21739604b578dbaa6c2e197a9d5` Passed，默认关闭、持久化、性能页控件与请求冻结。
- `NOVELTY-HP-TARGET-STOP` / `07da6f2abdd0486f947f02fe1e4c922a` Passed：真实前置探索、目标战损、固定重放成长、致命成长、强制一药/保留备用药及至少一药；`ROUTE-CACHE-RECORD-V0111` / `0f890408d5784ecca93e939f84f079ae` Passed，新增策略隔离缓存身份并保留恢复/手动重算语义。
- 综合 `CONTROLLER-SESSIONS-527` / `51d1ea6438c646bca26081bc9f5c9a89` 在窗口缩放/尺寸持久化断言失败（`configured=True, persistence=False`），尚未到新增设置断言。完整综合场景未通过，新增设置改用上述独立同源合同验证；不把失败归因为新搜索或记成通过。
- 双组合开关 / `1bb9e9c368824ce892b3efef1bef228b` Passed：30秒请求中实际运行3个Beam宽度，探索加全部Beam成员11,443节点≤24,000主搜索上限；上游药水审计仍按每层节点预算及请求截止时间执行。
- 原生两端 ScenarioId 参数通用；[复跑方式](../tools/BfwsResearchChecks/README.md) 同时说明 PowerShell/Bash 协议与 Linux 独立进程包装器。5 个新根的 10 份对照完整获胜且终局策略摘要相同，但多数成本更高；另有两个场景8份独立ABBA。三场原生部署与首次预测的战损/药水一致且计划外重算0，包含DOP2与真实1GB NoGC预算4次回收续搜；runId和全部代价见报告。没有可见 Steam、FPS 或 Windows 实机性能结论。

## 录像回放临时费用与充能球恢复（2026-09-15，未发布）

- 亡灵契约师/女王原包修复前 `71f63f33ad1d4e65bb52e66ba1cc50e8` 在严格导入时失败：手牌第 8 张 `SPUR` 记录为带 `EndOfTurn, WhenPlayed` 清除时机的 0 费，导入后为基础 1 费。修复后同一原包 `SHOWCASE-BUNDLE-IMPORT-V0111` / `d65e84b66d3f40319cc9495822aaa7f0` Passed，23.78 秒；8 张模型手牌与界面节点一致，牌堆计数一致，录像路线接纳且本地搜索 0 次。
- 故障机器人/女王原包的实机日志在首张 `DUALCAST` 进入 `NOrbManager.EvokeOrbAnim` 时抛出“Sequence contains no matching element”，随后路线在第 19 步因首张牌未完成而失配。修复后同一原包 `SHOWCASE-DEFECT-DUALCAST-0390` / `e0969979ed5d4373aef7a96cf615e44d` Passed，20.11 秒；球队列 1 个模型与 3 个原生可见槽位引用一致，首张双重释放正常结算，完整预计算路线第一回合无伤击杀，计划外重算 0，并经原生终端按钮返回主菜单。
- 最新实机日志 `combat-09f187ae444d4c538f6ba63efb85f308.jsonl` 显示同一机器人路线 38 步完整结束、四次 `DUALCAST` 均完成且状态失配为 0，确认新增反馈属于可见节点生命周期。补充容器检查后，同一原包基线 `SHOWCASE-BUNDLE-IMPORT-V0111` / `170ad140151c43769d9cce3c2b1ab7bc` 明确失败：管理列表外仍有旧 `NOrb` 留在容器。即时清理后 `SHOWCASE-DEFECT-DUALCAST-ORPHAN-UI` / `474124f84c1c4e2fafbf04f5c08275a1` Passed，35.67 秒；容器节点与管理列表一一对应，完整 38 步路线第一回合无伤击杀，四次双重释放及中间推球完成，计划外重算 0。
- Release 构建通过，0 警告、0 错误。两项均使用 Windows 隔离无头实例和玩家本次实际下载的原始 `ShowcaseBundleV1`；未启动可见 Steam，未执行 Bash 门禁。

## 0.39.0：多宽度路线精炼与 RitsuLib 0.6.0 适配（2026-09-15）

- 使用本次实际下载的 `ShowcaseBundleV1` 在 Windows 上验证五个协议文件全部解压，其中四个负载文件均在关闭写句柄后通过大小与 SHA-256 校验，未再出现共享冲突。
- `SHOWCASE-BUNDLE-IMPORT-V0111` 先复现旧包原生状态仅有选牌/奖励网络序号差异；在完整 22 Mod 栈下进一步复现 BaseLib 把旧二进制误读为非法字典容量。最终同一份静默猎手/永世沙漏旧包在基础 Mod 栈和当前完整 22 Mod 栈均通过整包入口，进入第 1 回合并接纳 18 个预计算动作，本地搜索 0 次；新规范原生状态的格式标识、自身匹配和单字节损坏拒绝合同同时通过。完整栈 runId `b356adc2efaf4979af145903d9feca85`，48.20 秒；最终源码基础栈 runId `ba22e3212df0424f87c949685dd9b3cc`，22.27 秒。
- `SHOWCASE-HAND-VISUAL-RESTORE` / `fdf489b0f12b491a878d52711d04cfc3` Passed，57.74 秒。使用玩家刚回放的静默猎手/永世沙漏原包恢复第一回合，模型手牌 9 张、界面手牌节点 9 张且引用顺序一致；随后本地搜索 0 次，沿包内路线第一回合击杀 Boss。修复前截图中的 18 张来自旧 9 个手牌节点未释放后与恢复手牌重叠，不是录像包记录了 18 张模型手牌。
- `SHOWCASE-NATIVE-TERMINAL-RETURN` / `4ada3b8429d9447b9a4231e514d062b6` Passed，64.75 秒。同一静默猎手/永世沙漏原包由预计算路线第一回合击杀后，测试触发实际原生终端奖励页的 `ProceedButton.Released`，确认跑局已清理、主菜单已加载、原生转场完成且终端覆盖入口已移除；本地搜索仍为 0。音乐切换复用原生继续游戏入口的 `StopMusic` 与角色转场流程，未做可听音频验收。
- `SHOWCASE-PILE-VISUAL-RESTORE` / `dda17ba3d52947cab53699441bfd77b4` Passed，83.56 秒。使用玩家本次实际下载的最新静默猎手/永世沙漏包恢复：手牌模型 7 张、可见 holder 7 个；抽牌堆模型与按钮均为 4，弃牌堆与消耗堆模型/按钮均为 0。随后沿包内路线第一回合无伤击杀，本地搜索 0 次，并通过原生终端按钮返回主菜单。恢复源码已移除可见 `RemoveFromCombat` 路径并将新 holder 同帧放到最终位置；无头测试不构成肉眼动画验收。
- 日志后台 Python 3.12 全部 65 项测试通过，其中录像库合同覆盖收藏读写、独立筛选、收藏阻止同根替换，以及收藏不占分组自动清理额度；生产默认普通录像上限为每组 2000。退出后既有 `test_reports_v2` 临时 SQLite 句柄出现一次 Windows 清理告警，不影响测试退出码和断言结果。CombatShowcaseRecorder 1.0.2 与 CombatSolver Release 构建均为 0 警告、0 错误；Windows PowerShell 结构门禁通过（`search_files=91`）。
- 本轮只运行隔离无头恢复和 Release 构建，不使用 Computer Use、不启动可见 Steam、不执行 Bash 门禁。测试结束后已停止隔离游戏进程。
- 合入 PR #94/#96 后，Windows Release 构建通过，0 警告、0 错误；PowerShell 结构门禁通过（`search_files=105`）；Beam 宽度组合离线合同通过（`BEAM_WIDTH_PORTFOLIO_OK checks=59`），其中包含默认关闭、门控、预算和最终质量仲裁。
- `ROUTE-ROW-REUSE` / `91fe2c42bc3c44d6bdec080606ac9967` Passed，23.97 秒：覆盖同值复用、显示字段变化、选择/击杀/顺序、空路线、失败重试、部署状态和语言往返。
- `CARD-CONTINUATION-EXPANDED-SEARCH` / `56398494c8b24ff9abe44bef784d7a0c` Passed，8.95 秒：实际穿过扩展后的卡牌选牌续执行和搜索边界。两项均在 Windows 隔离无头实例执行；完成后实例已停止，本次没有运行 Bash 门禁或可见 Steam 测试。
- RitsuLib 0.6.0 失败基线 `UI-LOCALIZATION` / `1a41b720610146afb894f3c9a25c5c18` 在 120 秒上限退出；日志直接定位为旧 `RitsuBaseLibTargetTypeLookupPatch` 找不到已经被框架改写的 `Assembly -> Type` 私有闭包，CombatSolver 初始化在应用自身补丁前中断。删除重复适配后，同一完整 0.6.0 分包启动并运行 `UI-LOCALIZATION` / `8d5c7bcdac8d438396cc12fb4d3459a4` Passed，28.53 秒，eng/zhs/zht 与 420 项目录通过。
- `NATIVE-HAND-CHOICE-REPLAY` / `fd5b50d371c041129e4c0e82d266a858` Passed，29.82 秒：RitsuLib 0.6.0 下两组燃烧契约选择、失配后人工恢复保持；新增生存者在 `Instant` 模式打出、选择防御弃牌、退出原生手牌选择并完成动作的直接合同。
- `BEAM-PORTFOLIO-SETTINGS-0387` / `0064d9a37309472db95ba9c1fd7fe353` Passed，29.35 秒：开关默认关闭，设置往返、性能页控件与搜索请求冻结一致；首回合原生部署完成。组合器与门控离线检查 `BEAM_WIDTH_PORTFOLIO_OK checks=59`，Windows PowerShell 结构门禁通过（`search_files=105`）。没有运行 Bash 门禁或可见 Steam 测试。

## 多宽度路线精炼扩展成员类型（2026-09-16，未发布）

- 组合器与门控离线检查 `python3 tools/BeamWidthPortfolioChecks/run.py`：`BEAM_WIDTH_PORTFOLIO_OK checks=73`，新增默认成员含且仅含一个次段成员和一个基础分成员、基线成员是普通宽度成员、两种成员的 Profile 各只多一个标志、显式宽度列表不追加、`MoveLeadingBandToTail` 四种情形。Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=105`，Release 构建 0 警告、0 错误。
- 一致性：本分支 DLL 在两个标志都未置位时，与 0.39.0 main（`7f806de`）的 DLL 在同一离线宿主、同一 5 根生成场景（Very High、固定节点预算、DOP 1）上 61 项 `solverMetrics`、全部动作与根戳记逐字段相同。
- 开关对照：同一 DLL、120 根生成场景每 4 根取 1 的 30 根，基线（两个标志都关）与次段开、基础分开各跑一次。次段作为组合成员：Very High 净 +51 HP 当量（变好 5、变差 0，1 根死转活），Medium 净 +21（4 / 1，1 根死转活）。基础分作为组合成员：Very High 净 +41（4 / 0，1 根死转活），Medium 净 +86（9 / 0，1 根死转活）。次段两组与基础分 Medium 组 30 根全部有效；基础分 Very High 组有一根（IRONCLAD-ELITE-04）撞 600 秒时间保险，该根在基线下同样撞保险。
- 本轮只运行离线宿主与离线检查，没有可见 Steam、Windows 无人测试或生产路径计时。

## 离线搜索宿主（2026-09-16，未发布）

- macOS Release 构建：`CombatSolver.csproj` 与 `tools/OfflineSearchHarness/OfflineSearchHarness.csproj` 均 0 警告、0 错误。
- Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=105`；Beam 宽度组合离线检查 `BEAM_WIDTH_PORTFOLIO_OK checks=59`。两条 `partial` 边界声明已同步到 `.sh` 与 `.ps1`。
- 新宿主对旧研究版宿主逐字段一致（同一份 0.39.0 DLL、同一批生成场景请求、`VeryHigh`/beam 135/nodes 100000/分支 72-42-54、`--dop 1`、`--budget-ms 600000`、`searchMode=Evaluate`）：BASE5 五根比 466 个字段，30 根子集比 2719 个字段，全部相同，没有单边多出来的根。比较口径见 `tools/OfflineSearchHarness/compare_results.py`（`solverMetrics` 排除时间/内存/GC 字段、选中路线逐动作、根 `ContinuationStamp`、目录指纹）。
- `--search-mode Coordinator --use-portfolio` 三根（`High` 预设、60 秒预算）全部 Passed，`solverMetrics.portfolioMembers` 各 3 个成员，宽度 `[90, 60, 135]`，即默认的 `[W, 2W/3, 3W/2]`；开关关闭时只有 1 个成员。
- 本轮只在 macOS 上跑离线宿主与 Bash 门禁，没有启动游戏、没有跑无人测试、没有 Windows 验证。
- 合并到当前主线后的 Windows 首次验证发现宿主工程缺少多版本 RitsuLib 的 `0.111.0` 引用目标，补齐后编译通过；首次运行随后发现解析器只查旧单目录，无法加载 `STS2-RitsuLib.Runtime`，已改为同时解析版本兼容目录与共享程序集目录。宿主原默认遭遇 `JAW_WORM` 在当前目录不存在，已改用项目现有的 `FUZZY_WURM_CRAWLER_WEAK`。最终运行结果记录在本次合并提交。
- Windows 合并验证：CombatSolver Release 与 OfflineSearchHarness Release 均 0 警告、0 错误；PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=113`。离线宿主默认场景最小烟测通过，推进到玩家第一回合并完成 Evaluate 搜索：182 展开、507 转移、预计战损 4，未启动 Godot 或可见 Steam。

## 路线界面复用与派生计算实验（2026-09-15，未发布）

- 交付场景 `ROUTE-ROW-REUSE`（证据文件为未纳入仓库的本地产物），runId `83d3d63b552f4393b8ffc03e8fba9060` Passed（25.374秒）：实际Godot控件身份、同值新数组、全部显示/本地化字段变化、选牌/击杀/顺序、空路线、状态页、构建失败后重试、部署索引/高亮、语言往返和订阅清理。原生双端ScenarioId入口，IRONCLAD、FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒、显式EvidenceDirectory；不启动搜索。
- `UI-LOCALIZATION` / `483a2e7173044a26a730997020c911b3` Passed（6.812秒）：eng/zhs/zht、415条目录、保留/恢复路线卡名、升级/嵌套选牌、序列化和无计划外重算。卡牌投影、名称与语言通知源码与交付源码相同；行缓存成功后发布的边界由交付合同另行覆盖。首次复用进程运行暴露同帧语言通知遗漏，失败与修正后证据同时保留。
- 交付Release 11.10秒、0警告/错误；Bash/PowerShell结构门禁均为 `search_files=102`。搜索层没有追加差异，投影洗牌缓存及专用缓存合同已从生产源树撤回。
- 已撤回实验的两场8份完整请求、原生120份牌序对照及严格增量结果仍记录于[正式PR追加报告](performance/performance-pr-20260915.md)和[结构化证据](performance/derived-work-reuse-20260915.json)；不把这些实验数字称为交付搜索提速。不启动可见Steam，未验证FPS或可见帧时间。

## 0.38.6 上游合并后的性能 PR 验证（2026-09-15，未发布）

- 对当前上游三场12份完整ABBA均通过严格oracle；蟹战耗时−15.95%、分配−15.51%、峰值−1.64%，扩展弃牌耗时−5.42%，携药轻场景−2.25%；后两场分配与峰值均下降。仅限本机无头样本。

- 正常Release 14.56秒、0警告/错误；Bash与PowerShell结构门禁通过，`search_files=102`。比较器源码未修改，复用此前16项通过证据。
- 四组严格增量：卡牌扩展 `ae00c0b92e1b485b83c04c65328d5441`、药水 `a8a8a40b79b548659aab223a2220caf4`、动作/EndTurn嵌套 `5feed9b8a4ad481096bd03980af551d0`、首回合准备 `358e83a8965841db9f40e7c5d2562ef8`，均Passed。
- 原生跨回合连续选择 `98135e17d0c84660b9bc5958547180af` Passed，35分支与完整状态对账；上游成长/药水早停 `3a599fbe370848258b538fa12fde2e7a` Passed。
- 格挡药夹具首次 `7f79df25a74f486aa7388e33a20fcc28` Failed：本场只掉4血，未达到断言所需9血。仅补敌方5层力量并将上限设为120秒；修正后 `36e20d691de1424e9b5e7e14196623ce` Passed，实际省9血、T2无伤获胜、零计划外重算。Linux启动器支持与PowerShell相同的 `expected-initial-deterministic-block-potion-inserted` 三态断言。
- 全部合同上限120秒，实际部署Instant/0秒；当前上游完整请求ABBA、基线Testing支持补齐及所有失败见[正式 PR 验收](performance/performance-pr-20260915.md)。无可见Steam或Windows帧时间结论。

## 选牌续执行批量实施（2026-09-14，未发布）

- `CARD-CONTINUATION-EXPANDED` / `15ed72aac4504a0f8c56133251fe1c2c` Passed：NECROBINDER、41来源82普通/升级分支；80种选择共380候选、2种无选择；完整状态/历史/RNG/身份、兄弟与DOP2、10种代表原生结算。
- `CARD-CONTINUATION-CONTRACT` / `18085ba4d6b044b58a0aef69bb4605b9` Passed：扩展后的原三牌边界、洗牌及原生合同。
- `CARD-CONTINUATION-EXPANDED-SEARCH` / `5e70bf69886741e6abdc0dd97bfce87c`，`CARD-CONTINUATION-EXPANDED-INCREMENTAL` / `151a8484b2094687a8e28780453c855b` Passed：SILENT、实际搜索前缀逐分支及完整结果对照、取消/错误排空、严格增量。
- `POTION-CONTINUATION-CONTRACT` / `bcf7b7f30d5c4b0691c08c6707a3060f` Passed：SILENT、九种药水41个选择、50次生产分支/再次访问、九种原生完整结算；状态/历史/RNG、消耗、BeltBuckle/ReptileTrinket、兄弟修改及DOP2。
- `POTION-CONTINUATION-SEARCH` / `856b8d5db4604d5fa9bb27e2070d1174` 与 `POTION-CONTINUATION-INCREMENTAL` / `0d002f4702184b2da1e106666d60b131` Passed：旧路径/DOP1/DOP2完整结果，真实嵌套回退、同父并发取消/错误排空、严格增量。
- 均用两端已有ScenarioId协议、FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒、根合同后停止；实际执行Linux无头。回合/嵌套、最终完整测量及原生部署见下；详见[阶段记录](performance/choice-continuation-expansion-implementation-20260914.md)。

### 第三阶段与共享尾部回归

以下场景仍使用同一双端ScenarioId协议，SILENT（全41卡合同使用NECROBINDER）、FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒、根合同后停止；原生跨回合额外使用Instant/0秒。

| 场景 | 直接证据及范围 |
| --- | --- |
| `DRAW-EXECUTION-CONTINUATION` / `NESTED-DRAW-EXECUTION-CONTINUATION` | `fbdea0b824774494bc02e962669015d9` / `9deb4e09364545fab0195a71ef203273` Passed；部分抽牌、洗牌、再次捕获、全状态/历史/RNG、DOP2及原生 |
| `TURN-AFTER-EXECUTION-CONTINUATION` / `TURN-NESTED-EXECUTION-CONTINUATION` | `076aece95fa041bfad376621e623cef4` / `74e26c01154f473eaa443b322f08596e` Passed；六来源及消耗/弃牌后的深层抽牌 |
| `CARD-DECISIONS-EXECUTION-CONTINUATION` | `7e6063ff8bae4fa4af0d6e8d10094c17` Passed；重复子出牌、历史别名与原生。其他Before/Havoc/Cascade/Repeat独立完成项所在请求整体Failed，按[实施记录](performance/choice-continuation-expansion-implementation-20260914.md)的部分请求范围引用 |
| `EXECUTION-CHOICE-SEARCH-CONTRACT` | `e23c68bf438b429c90a50c00ba435724` Passed；全来源92、Mayhem14、Cascade10、后续回合35分支，全部状态/历史/洗牌/一次transition与父/live隔离 |
| `EXECUTION-CHOICE-SEARCH` / `EXECUTION-CHOICE-INCREMENTAL` | `e00560d9502d4faaaf1a6ccdbcbd59c1` / `71cd4c23b66e499e9ae05aeb626f81b5` Passed；完整Solve旧路径/DOP1/2、同父取消/异常排空及严格增量 |
| `EXECUTION-CHOICE-SETUP-SEARCH` / `EXECUTION-CHOICE-SETUP-INCREMENTAL` | `3b5c082e119e4414ae1187f82c45db30` / `fe41c0e260a84ccfa2ec281013b21a4c` Passed；首回合三来源真实Solve及严格增量 |
| `EXECUTION-CHOICE-SETUP-BUDGET` | `402c6f1cc87444d99332248b15bb892c` Passed；九层压力在两模式均到达相同有限预算边界，不以扩大预算获得完成根 |
| `CARD-REMOVED-PREFIX-EXECUTION-CONTINUATION` | `3b6ca0a3f7154dc2b99128822c6d9fc6` Passed；Cascade先打出并移除能力牌，再两次选牌；原完整回放/历史/DOP/原生一致，覆盖完整蟹战暴露的非牌堆列表成员 |
| `HAND-DRAW-SHUFFLE-CHOICE-REPLAY` | `c81dafb9cd84472db5e78a3bbb8f5b1b` Passed；关闭新执行续跑，保留旧稳定前缀的完整状态、DOP/取消/异常验证 |
| `EXECUTION-CHOICE-ROUND-NATIVE` | `47f1e0e03065438abb271475d31aca5c` Passed；真实EndTurn进入第二回合、连续原生选择、完整StateText一致 |
| 最终41卡/9药水与各自严格增量回归 | `b368988cf058462d8f52a1391f4d9e51` / `4866f778cb404aadb7b590f05aa4c503` / `e101b6d9dea14560a71c84d7f7a79800` / `22148a231efe482cacadb8dbe965041a` 均Passed |
| L0 | Release 0警告/错误；Bash/PowerShell结构门禁search_files=101；16项性能比较器检查通过 |

`CHOICE-CONTINUATION-STEP-AUDIT`：`88236e7e4ef748f0bead85422be84c67` Passed，73.689秒；以报告的原蟹战输入改为`mode:Setup`、VeryHigh、DOP2、NoGC关闭、120秒请求运行。内部固定20,000节点、两次主动用药，56,211次执行续接逐步对账完整状态、待选请求/有序候选、历史数量和洗牌；错误接受/拒绝分支均检查，并比较关闭续接的完整搜索结果。它是独立诊断搜索，不等同于协调器的完整三层药水审计，也不计入性能成绩。

最终三场12份正常完整请求均Passed，完整动作/路线和决策质量一致；弃牌/携药轻场景的严格工作量也一致，原蟹战保留工作量差异，按用户要求不继续归因、不标为同工作量提速。全部样本、输入错误和比较结果见[结构化证据](performance/choice-continuation-expansion-implementation-20260914.json)。`CHOICE-EXPANSION-NATIVE-DEPLOY` / `ef6fd35b159a4aee974c826319755371` Passed，60.440秒，39动作原生执行到T1无伤胜利，HP56→56、敌HP0、`UnexpectedReplans:0`；Instant/0秒、120秒上限，使用最终正常Release。

## 自身弃牌续执行正式接入（2026-09-14，未发布）

原生两端无人启动器均可使用以下 `ScenarioId`，固定SILENT / FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒、根合同后停止：

| 场景 | 本轮证据 |
| --- | --- |
| `CARD-CONTINUATION-CONTRACT` | `7bc12837a9de419095d2ed238da8dc1b` Passed；三张牌、杂技/早有准备普通与升级、原生完整结算、全部选择、历史/RNG/洗牌、兄弟/DOP2、取消与错误 |
| `CARD-CONTINUATION-SEARCH` | 最终源码 `c37f7aea45c942d2be284d67c920dd99` Passed；真实选择链、嵌套回退、关闭复用/DOP1/DOP2完整结果、并发取消/异常排空 |
| `CARD-CONTINUATION-INCREMENTAL` | 最终源码 `91e987fc05804e28a75099bf546ae5f7` Passed；严格增量，复用与回退均命中 |
| Release、结构门禁、性能比较器 | 0警告/0错误；Bash/PowerShell `search_files=92`；12项比较器测试通过 |
| 独立完整原生部署 | `5520c13005d24e43ab9f1a3ac92f5f44` Passed；初始39动作计划，原生T1结束、HP56→56、敌HP0、计划外重算0；Instant/0秒，120秒上限 |

完整极高同工作量测量使用正常Search与独占新进程，不带增量开关；原蟹战、静默起始牌组死亡场景与独立弃牌获胜场景，共12个最终样本和9项完整对账通过。原两场保留A1后采最终F1/F2/A2，获胜场景独立ABBA；数据、GC不利变化、部署日志限制和全部runId见[报告](performance/choice-continuation-search-20260914.md)及[JSON](performance/choice-continuation-search-20260914.json)。既有CoverageCatalog分类与外部注册签名未变化，没有全量覆盖门禁或可见Steam测试。其他选牌来源的[扩展研究](performance/choice-continuation-expansion-20260914.md)仅做源码和清单核对，未写为通过语义或性能测试。

## 选牌暂停与恢复窄原型（2026-09-14，独立实验）

基于 `1ef4601` 的实验 Release 构建 0 警告/错误，默认搜索与生产源码未修改。[报告](performance/choice-continuation-prototype-20260914.md)和[结构化证据](performance/choice-continuation-prototype-20260914.json)保存全部原始样本与失败尝试。此前投掷匕首计时受旧路径拒绝诊断污染，性能结论作废；以下三次均使用修正后的同一实验 DLL。

| 验证 | runId / 结果 |
| --- | --- |
| 投掷匕首全部9选择、完整历史/RNG/身份、兄弟与DOP2、取消/异常/释放、升级/历史前缀/真实洗牌、拒绝/嵌套回退、原生完整结算；固定工作量及受控保留堆 | `6c9670133f8242dcb2f29b4089eaa89a` Passed |
| 杂技普通/升级全部9/10选择，抽3/4弃1，真实洗牌、历史/RNG/兄弟/DOP2/嵌套回退，两版分别原生完整结算及固定工作量 | `13cb642b0ac5459c897daf032503c078` Passed |
| 早有准备普通/升级全部8/36选择或组合，抽弃1/1及2/2，真实洗牌、历史/RNG/兄弟/DOP2/嵌套回退，两版分别原生完整结算及固定工作量 | `f4559b26b5484ed5be592c706053c2fb` Passed |

复跑先按[工具说明](../tools/ChoiceContinuationPrototype/README.md)在固定版本的独立 worktree 构建，使用新证据目录；runner 的 `--card dagger|acrobatics|prepared` 选择场景。Linux 无头、SILENT、FUZZY_WURM_CRAWLER_WEAK、敌HP999、关闭NoGC、每请求120秒；专属进程在结束/失败时清理。原生检查等待精确动作完成并核对完整 continuation。未执行默认 Search、完整蟹战、可见 Steam、Windows 或全量发布门禁，不作对应收益结论。

## 蟹战后续延迟优化（2026-09-14，未发布）

沿用3afbdd3的完整VeryHigh输入、DOP16与16GB NoGC，新增专用洗牌短fixture；全部非时序质量字段、开局、政策、动作/路线直接对照。原型、增量峰值反例、整批初始基线、每次数据与复现参数见[报告](performance/crab-latency-20260914.md)及[结构化证据](performance/crab-latency-20260914.json)。

| 验证 | runId / 结果 |
| --- | --- |
| 最终生成池v3：27组有序候选/Power状态/RNG与历史事件类型顺序、兄弟/live隔离；三类可变池一次解锁读取回退 | `e9dc2207bfe34624807e8d95a4b3ea70` Passed |
| 最终抽牌前缀v2：洗牌选择后继续变牌、延迟抽牌只消费一次、来源失效、完整状态/增量/兄弟隔离及洗牌次数/历史条目数、DOP1/2与取消/失败排空 | `edc5c7fac70244168f66a92e092179d5` Passed |
| 最终组合既有即时/抽牌后学习前缀 | `dcdc9b5ef80d4db8821f22ac351fba86`、`0f51aa3c657b45389efe733355815257` Passed |
| 最终完整蟹战A/F/F/A、轻场景A/C/C/A及C/A/A/C、专用短场景C/C及最初基线U/U | 全部严格oracle一致；不把原型速度或诊断时间当最终数字 |
| 最终正常Release、双端结构门禁 | Release 0警告/0错误；Bash与PowerShell均通过，`search_files=90`；结果写入JSON verification |

最小合同均用原生无人启动器、IRONCLAD、FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒上限、建局合同后停止。scenario-id分别为`TURN-START-GENERATION-CACHE`、`HAND-DRAW-SHUFFLE-CHOICE-REPLAY`、`END-TURN-CHOICE-REPLAY`与`ADAPTIVE-END-TURN-CHOICE-REPLAY`。新增生成池与前缀分别在对应源码定版后验证，合并时只重跑共享抽牌段相关既有合同并执行最终原始蟹战交互对照。初次测试编译的不存在GetHandCount调用及perf包装器返回码问题保留在报告，不计作通过；外部注册与CoverageCatalog分类未变，无全量门禁或可见Steam。

## 通用分配与重复工作优化（2026-09-14，未发布）

基线为上游 `b1674f8`，双方使用相同生成器完整预算/NoGC回退测试支持。固定输入、全部开局、政策、动作与路线分别对账；短搜探针数字不作为完整极高性能结论。完整样本、失败、源码阶段和限制见[本批报告](performance/general-allocation-20260914.md)。

| 验证 | runId / 结果 |
| --- | --- |
| 九条RNG原生序列、完整状态、保留引用、父子/兄弟/多代及冷读取不物化 | `c665b52b8d2d4b55876c513477e19a2c` Passed |
| 通用抽牌后前缀发现、完整状态/历史/续用/RNG、DOP1/DOP2工作与动作、并发/取消/失败排空 | `65c8ad5d55414cbdaf4ef5696a81badc` Passed |
| 原 ToolsOfTheTrade 即时前缀及选牌回放合同 | `3d17b2c8b693488e8ca7e78255b7ceaf` Passed |
| 生成器固定/正常预算映射与既有建局合同 | `8ac328b1c2964837816806a03bb3a77c` Passed |
| 根牌/生成牌/Clone/多代Fork首次入场、父子隔离及污染增减 | `b5af79fada6a4797b22016c8d4d979c7` Passed，最终根共享实现 |
| 冻结跑局前缀身份/顺序、可变Power映射、卡牌变异和多代Fork | `bc8eb0fc6c1f43d9bcd98fbb3c259b6d` Passed，最终根共享实现 |
| 长期资源均匀/非均匀池旧实现对照、选中身份/顺序、共享祖先及全部排名恢复 | `152b0f1432a84433bf224c3afda896a7` Passed；保留原List遍历方式的最终候选 `50397d3aba5b4b9ea919f9a0f9bd7649` Passed |
| 最终候选十种完整VeryHigh开局，蟹战/静默女王追加交错复核 | 24次候选请求Passed；22次严格oracle相同，亡灵契约师女王两次总转移少1、动作/路线一致，排除严格同工作量提速；全部runId及差异见[结构化证据](performance/general-allocation-20260914.json) |
| 最终正常Release、比较器单元测试、启动器语法与结构边界 | Release 0警告/0错误；比较器10项通过；Bash/PowerShell启动器语法通过；最终双端结构门禁通过，`search_files=90` |

保留两个夹具失败：`e0d1edede32e4300b67c9478cba1b1ff` 的敌人过早死亡，未覆盖前缀复用；改为敌HP999后覆盖。`94ba920946634fd0b3a1dafeeb37caa2` 缺少既有污染断言所需技能牌；加入DEFEND_IRONCLAD后覆盖。早期入场测试 `5527c75b597b43e59ba966c7e1f1c5a9` 通过，不能替代最终根共享合同。原120秒重场景内环未返回结果，作为超时保存；最终完整请求属于预先确定的独立测量层。

资源保路夹具先保留两次失败：`eea19a6790b349858d7d5294f99a2ba1` 暴露旧反射回放入口漏传两个新增可选参数，已同步真实签名；`cc7e15d2b9e942f2aabd9a3ad6498da0` 对零资源错误调用只接受正增量的领域方法，改为保留零初值后通过。两次均未执行到排名对照，不能算生产候选错误或通过。

复跑最小合同使用原生启动器、`--scenario-id` 对应 `LAZY-RNG-FORK`、`ADAPTIVE-END-TURN-CHOICE-REPLAY`、`END-TURN-CHOICE-REPLAY`、`POWER-AFFLICTION-ENTRY`、`FROZEN-ROOT-LISTENERS`、`LONG-TERM-RESOURCE-STAGING`。选牌/入场/资源合同用IRONCLAD、敌HP999；入场/冻结监听用清空战斗牌堆后加入手牌INFLAME与DEFEND_IRONCLAD。请求上限120秒，关闭NoGC，仅检查建局合同并停止；PowerShell使用对应PascalCase参数。生成器另用解析后的指定样本。Linux无头不证明可见FPS、Windows或完整自动部署。

## 在线监控：离线战绩身份（2026-09-14）

- 最终 `npm test` 22 项通过，Edge headless 浏览器测试 10 项通过。服务接口覆盖战绩先于心跳上报时返回空昵称、离线状态和原始安装 ID，收到心跳后恢复当前昵称和在线状态；昵称包含搜索只从当前在线名单映射安装 ID，无匹配时返回空范围。浏览器测试覆盖离线行显示完整安装 ID、禁止“离线 · 离线玩家”回流，以及玩家昵称筛选参数和已应用标签。默认 Playwright Chromium 首次因本机未安装对应浏览器而未执行页面逻辑，后续均按项目既有 `BROWSER_CHANNEL=msedge` 入口验证。

## 0.38.6 发布范围：格挡药路线直插与录像收录辅助 Mod 判定（2026-09-14）

- 格挡药路线直插：Release 隔离构建 0 警告、0 错误，Windows PowerShell 结构门禁通过（`search_files=91`），CoverageCatalog 取得 3035 项、0 未分析、0 待实现。新增 `BLOCK-POTION-ROUTE-INSERTION` 完整部署场景，断言 Smart 无药路线单回合战损达到 9 后直接插入格挡药、实际省血至少 9、T2 获胜且计划外重算为 0；本轮运行时因已有普通游戏进程占用宿主准入而未执行，不记为通过。

- `REPORT-V2-CONTRACT` / `7c9972982bc4425b929247e149e6c790` Passed，22.84 秒。新增合同证明录像浏览器、统计、QuickSL 等 `affects_gameplay=false` 辅助 Mod，以及统一登记的 Loadout/RNG 复现工具不会阻止收录；声明修改玩法的新角色和数值重制 Mod 仍被拒绝。既有问题包上传、取消和 TLS 合同同时通过。
- 最新实机日志确认 0.38.5 未上传的原因是旧逻辑把 23 个已加载 Mod 与两项 ID 白名单比较，在线统计实际开启，本地没有待上传包，服务端也没有收到请求。本轮不使用 Computer Use，不执行 Bash 门禁。

## 0.38.5 发布范围：Act 3 无伤 Boss 录像对局库（2026-09-14）

- CombatSolver 与私用录像 Mod 的 Release 构建通过，0 警告、0 错误；Windows PowerShell 结构门禁另记最终结果。按用户要求不执行 Bash 门禁，不启动可见 Steam。
- 日志后台 Python 3.12 隔离环境完整 64 项单元测试通过；测试进程退出后既有 `test_reports_v2` 临时 SQLite 句柄出现一次 Windows 清理告警，不影响测试退出码和断言结果。
- 服务端合同覆盖五文件白名单、文件摘要、客户端/路线结束回合一致性、从动作时间线重新计算结束回合及用药数、1/3/多回合、同根质量替换、每组前 100、只读鉴权、后台展示/下载/删除。
- 实机全职业/全原版第三幕 Boss 的精确导入与路线部署尚未运行；因此当前验证不宣称这些组合已逐项实机通过。

## 0.38.4 发布范围（2026-09-14）

- `HP-MODIFIER-COLLECTIONS` / `09f87bce86e74dc1b30f179d71e3dcd6` Passed：192 组 HP 修正集合合同保持；新增孤注一掷意图预测断言，非致命穿透伤害准确转为死亡并预测消耗一次蜥蜴尾巴，全额格挡保持安全，预测前后完整状态不变。
- `DEATH-SAVE-ORDERING-FINAL` / `015992ad11e34b3eacdd34d2f527cbbd` Passed：控制器生命周期与搜索合同通过；纯排序断言证明同为完整胜利时零复活路线压过血量和回合更优的复活路线，而复活胜利仍压过无复活的失败路线。最终战不再免除保命资源成本。
- `FAIRY-AUTOMATIC-RESCUE-DEATH-SAVE-FINAL` / `bb074141070441258c9f13191dabe520` Passed：1 HP 且只有瓶中精灵能存活的既有两回合场景仍自动复活并获胜，用药 1、计划外重算 0，证明新约束没有把万不得已的救命路线禁掉。三项均使用 Windows 隔离 headless；Release 构建 0 警告、0 错误，未启动可见 Steam。

## 0.38.3 发布范围（2026-09-14）

- 问题包弹窗正文回归：`UI-LOCALIZATION` / `dc7da1036cb0461698c9b75f073ffbd0` Passed；尺寸合同增加“滚动容器不参与自然高度时仍取得 320 px 默认高度”的断言，继续覆盖三种视口的总尺寸、拖动边界及 eng/zhs/zht 控件。Release 构建 0 警告、0 错误；未启动可见 Steam 做人工排版验收。
- 夸克打包版：直接调用统一发布脚本中的 `New-QuarkReleaseBundle`，以 `CombatSolver-0.38.2.zip` 为基础加入未解压的 `STS2 RitsuLib 0.5.20.zip`。临时外层包为 22,036,563 字节，严格超过 10 MiB；嵌套条目恰好一项，名称保持不变，条目原始长度 20,161,342 字节与前置文件一致。未执行上传、移动或发布。
- 失败窄搜移除：主搜索和 Smart 精确药水层直接使用原 profile，源码中不再存在 `NARROW_BEAM_RECOVERY`、`RecoverDeferredTurnFrontier` 或同回合落选前沿 fixture；请求级无胜利扩大搜索合同保留。Release 构建 0 警告、0 错误，PowerShell 结构门禁通过（`search_files=90`）；`NoVictoryRecoveryChecks` 最终通过 8 项请求合同及原策略断言，首次运行因检查工具仍引用已删除的旧 `Deep` profile 而未编译，改用当前 `Default` 后通过。按用户要求不执行 Bash 门禁。
- 药水批量预设：四种纯策略转换及设置序列化断言已进入控制器生命周期测试；Release 构建 0 警告、0 错误。完整控制器场景继续到既有 Smart 药水补查断言后失败，该失败不在本项批量预设路径，未记整场通过。
- `Ctrl+F9` 显隐：结构断言覆盖正确组合、错误功能键、键盘连发及隐藏后恢复原可见状态；输入节点独立于覆盖层。可见游戏未运行。
- 问题包弹窗：`UI-LOCALIZATION` / `8fcb860ea114408c8a476d5bdee69334` Passed；纯尺寸合同覆盖 1920×1080、1280×720、960×540，正文为纵向滚动容器且标题/按钮位于其外，中英/简繁控件合同通过；可见排版未运行。
- 原生选牌覆盖等待：`NATIVE-CHOICE-COVERED-WAIT-0383` / `fa7d612ef12e40e3a40f8e552d4f6a6a` Passed；纯状态合同覆盖预期页、其他覆盖层和真实缺失三态，10 秒真实缺失、60 秒遮挡、再 19.999 秒缺失不超时，累计真实缺失到 30 秒才超时；控制器生命周期与零损首回合搜索通过。工具箱下实际打开卡组未运行可见测试。
- 成长机会目标：`SEARCH-HP-TARGET-STOP` / `26431b92993144bd9ac1f7d1a0513ce1` Passed，23.49 秒；覆盖能力牌未打出实体、消耗牌未消耗实体、永久牌组实例、固定重放目标与逐次实际收益、黏糊强化、炼制药水不按空槽裁剪、致命来源竞争、动态重放和消耗回收。零损早停 1 节点、关闭后 4 节点，狩猎兑现后早停、强制/至少一瓶药水合同保持。前两次独立实例因默认实例持有独占租约而在主机准入阶段超时，未执行场景；随后按受管标记复用默认实例并通过。
- 第三方与额度：`GROWTH-POLICY-FREE-FIRST` / `c26a45b17e174cd7a6861188ed35d69f` Passed，21.48 秒；`GROWTH-POLICY-PAID` / `a9314d72f9cf43aaa162431515a90171` Passed，21.51 秒。覆盖旧登记不提供目标时保持完整搜索、可选计算器只读冻结快照、固定 Spiral 附魔重放、负次数与无效返回拒绝，以及零额度拒绝付血、足额额度取得成长、IgnoreLongTermRewards 清零。先行失败 `8d6dcdf7ca5e4c2ea3e01f76d9e2d3e8` 修正旧测试硬编码八个来源，`e203b483afae45918bea2a718a0c7b7a` 修正“成长存在即永不早停”的旧断言，`c8c16d90b32f4cd09258cf2ade57dc4f` 把额度排序与早停测试职责拆开；失败均未记通过。最终 Release 构建 0 警告、0 错误；可见 Steam 未运行。

## 0.38.2 发布范围（2026-09-14）

- 定版范围为 PR #85–#88 的回合末晚期、卡牌引用、精确 OnPlay 补丁组合与能量重置适配，以及 PR #92 的通用战斗测试工具；不改变正式搜索预算或策略。
- 行为证据复用下列合并验证：三组独立合同工具、CoverageCatalog、组合原生两回合续用与生成场景 Windows 无头 Search 均已在当前行为源码上通过。之后只改版本、玩家日志和发布元数据，不重复行为场景。
- 当前没有内置第三方适配声明；任意外部 Mod、完整长局、可见 Steam 和完整发布门禁均不在本次普通发版验证范围。

## PR #92 合并验证（2026-09-14）

- 基于已含 PR #85–#88 的当前 `main` 合并 PR #92；滚动文档冲突保留双方开发、发布与验证记录，生成器仍限定在 Testing/无人测试边界，不改变正式搜索政策。
- 修正审查发现的两项输入失败问题：药水请求先按目标槽数验证，通过后才清空并调整槽位；Bash 启动器要求生成场景路径已经存在，并在获取无头运行时前失败，证据目录仍允许新建。
- `python -m unittest tools/GeneratedCombatScenarios/test_run.py` 2 项通过；PowerShell 与 Bash 结构门禁通过，`search_files=90`；Bash 缺失场景路径负向用例在启动游戏前以退出码 2 拒绝，并保留原始路径；Release 构建 0 警告、0 错误。
- Windows 隔离无头 `GENERATED-SCENARIO-CONTRACT` / `71c38e4846144248955e045b121564cf` Passed：SILENT / BYRDONIS_ELITE 完成原生建局，生成解析、单人池、装备、药水顺序及原生开局合同通过；固定 1000ms Search 取得首个结果，541 展开/3065 转移，TimeLimit，不作胜利结论。
- 药水超槽负向用例 `7dc1e836f1824afb9ba26d33960235bf` 按预期 Failed 于 `inject_run_relics`，明确报告 3 瓶/2 槽。没有运行可见 Steam、完整 Deploy 或发布门禁。

## 通用战斗生成（2026-09-13）

独立于玩家问题包，配置与口径见[工具说明](GENERATED_COMBAT_SCENARIOS.md)，[结构化证据](testing/generated-combat-scenarios-20260913.json)。PR 开发轮次无可见Steam或Windows实机验证。

| 验证 | runId / 结果 |
| --- | --- |
| 随机生成、全部原版角色池、独立随机流、非法输入 | `1a5b75d474084b0c983508d722caa3b4` Passed |
| 单人池排除与显式多人牌拒绝，六种子短搜 | `3e0cc805a1e14770848a7f075344a914` 含扩展合同Passed；种子 `OPTIMIZATION-A10-20260913-0000` 至 `0005` 全部取得首结果，含仅死亡路线，不作胜利结论 |
| 多人专用遗物拒绝与实际单人装备终检 | `5ade44ac866c41fe83875808ccad25d4` Passed；全角色/无色多人牌及MassiveScroll拒绝，实际人数/装备检查，首结果搜索 |
| 全指定重放，完整配置/装备/开局相等 | `977049b4617842ca9d3309e17a7ff7e5` Passed |
| 批量入口首结果搜索，DOP2 | `06d8563020e547edb5d1b6a405593f5a` Passed；801展开/3124转移，TimeLimit，非获胜结论 |
| 指定A10第三幕Boss、无初始装备/进阶之灾、原生药水腰带获取及四槽 | `55c373dc3f5e4dec9a9b7570fc6a10d9` Passed |
| 赌博筹码/工具箱建局选牌及显式重放，完整开局相等 | `467e5da2a032470cbc9ae892c115f1b4` / `c9d4e59f7a4b41f787743bb73d89f80f` Passed |
| A10三瓶药水请求拒绝，不覆盖槽位 | `5fd3aedf9d7f4a14a184492764bcb3b2` 预期Failed，错误明确为3瓶/2槽 |
| 批量入口完整部署通路 | `2ffecdf3d2974dbaa2ae3ce1d7fa9494` Passed，第9回合结束；此先行样本未带重算断言 |
| 最终请求隔离/预算/模式合同与完整部署，Instant/0秒、零重算 | `a2e30360c6144d58a1532a5fad3d6c12` Passed，第10回合获胜，HP42、敌HP0、`UnexpectedReplans:0` |

生成器Release构建零警告/错误；两平台结构门禁通过，PowerShell入口语法通过；批量失败后新进程继续、中断后清理的两项模拟启动器合同通过。不同源码阶段的先行证据保留，不冒充全部由最后一次构建重新运行。随机组合并不保证可胜或已获模拟支持。

Linux代表入口（读取完整显式回归配置，显式要求结束后退出进程）：

```bash
./tools/run-unattended-test.sh --generated-scenario-path tools/GeneratedCombatScenarios/regression-necrobinder-elite.json --evidence-directory .local/generated-regression --scenario-id GENERATED-SCENARIO-CONTRACT --headless-instance generated-regression --timeout-seconds 120 --exit-on-complete --search-max-degree-of-parallelism-for-test 2 --enable-no-gc-region-for-test 0
```

Windows等价入口：

```powershell
./tools/run-unattended-test.ps1 -GeneratedScenarioPath tools/GeneratedCombatScenarios/regression-necrobinder-elite.json -EvidenceDirectory .local/generated-regression -ScenarioId GENERATED-SCENARIO-CONTRACT -HeadlessInstance generated-regression -TimeoutSeconds 120 -ExitOnComplete -SearchMaxDegreeOfParallelismForTest 2 -EnableNoGcRegionForTest 0
```

## PR #85–#88 合并验证（2026-09-14）

- 基于当前 `main`（已含 0.38.1、PR #90 与在线 DAU 提交）依次合并 #85、#86、#87、#88。滚动文档冲突保留全部已发布与开发中记录；适配手册将回合末晚期、OnPlay 组合及尚未开放入口编号为 §2.10–§2.12。
- #85 与 #88 都从旧基线占用监听掩码 bit 55；合并后保留 `AfterSideTurnEndLate=55`，将 `AfterEnergyReset` 置于 bit 56。两项新增 Hook 使镜像注册表总数为 46、`Hooks/` 为 39，并同步修正正文旧计数。
- `TurnPhaseMirrorChecks` 25 项、`ModelPredictionStateChecks` 32 项、卡牌引用 28 项、空登记 3 项及 1,000 次哈希遍历 0 字节分配检查通过；`AdaptedOnPlayChecks` 35 项与空登记 2 项通过。
- PowerShell 结构门禁通过，`search_files=90`。CoverageCatalog `--verify-effective --verify-runtime-evidence` 通过：3035 项，0 未分析、0 缺少有效运行证据；22 项仍明确位于回放视野外。
- 合并产物的 `ADAPTED-ONPLAY-INTEGRATION-REUSE` / `820e50163dbd475da7ba798fb85af901` Passed：真实模拟器组合覆盖双方晚期结算、模型卡牌引用、OnPlay 适配与跨回合续用，精确复用到第 2 回合，计划外重算 0。
- 最终 Release 构建成功，0 警告、0 错误。PR #88 的原版 handler 与旧 switch 已做源码逐项对照，但本轮没有仓库内第三方 `AfterEnergyReset` 专用游戏夹具；没有运行可见 Steam 或完整发布门禁。

## 0.38.2：精确 OnPlay 补丁适配

- 游戏 0.111.0 的 Release 构建通过，零警告、零错误；Bash 结构门禁通过。
- `AdaptedOnPlayChecks`：35 项合同、2 项空登记检查通过。使用 Harmony 2.4.2，覆盖完整组合、来源、重载、类别、顺序、冻结及拒绝规则；游戏实体和模拟器外壳使用替身。命令见[工具说明](../tools/AdaptedOnPlayChecks/README.md)。
- 原生替换（证据文件为未纳入仓库的本地产物）：真实防御 OnPlay 替换执行恰好一次，完整快照、增量回放、Fork 与 T1→T2 对账通过；额外补丁改变 continuation，旧根保持冻结，新根拒绝未登记组合。
- 缓存路线（证据文件为未纳入仓库的本地产物）：补丁变化使控制器执行资格失效，移除补丁后恢复。
- 跨回合续用（证据文件为未纳入仓库的本地产物）：第 2 回合精确续用通过，计划外重算为 0。
- 游戏验证使用回合阶段、卡牌引用和 OnPlay 适配的组合构建；续用场景同时登记模型状态与 OnPlay，独立场景只登记 OnPlay。注册场景须使用独立新进程。
- 执行中热换补丁、完整长局和任意第三方 Mod 未覆盖；未作性能验证。

## 0.38.2：卡牌引用辅助接口

- 游戏 0.111.0 的 Release 构建通过，零警告、零错误；Bash 结构门禁通过。
- `ModelPredictionStateChecks`：卡牌引用合同 28 项、模型状态合同 32 项、空登记合同 3 项通过。覆盖实例身份、父子兄弟隔离、Fork、两侧描述、空值、重复、顺序及失效引用；游戏对象和模拟器外壳使用替身。
- 模型状态集成（证据文件为未纳入仓库的本地产物）：完整模拟器 Fork、Preview COW、子状态变更隔离、引用列表参与指纹和 continuation，以及 T1→T2 原生完整快照对账通过。
- 模型状态续用（证据文件为未纳入仓库的本地产物）：控制器第 2 回合精确续用通过，计划外重算为 0。
- 游戏验证使用回合阶段、卡牌引用和 OnPlay 适配的组合构建。任意外部 Mod 的状态语义未覆盖；未作性能验证。注册场景须使用独立新进程。

## 0.38.2：回合末晚期镜像

- 游戏 0.111.0 的 Release 构建通过，零警告、零错误；Bash 结构门禁通过。
- `TurnPhaseMirrorChecks`：25 项合同、1 项冻结检查及分配回归通过。覆盖精确登记、两侧参数与顺序、空参与者、异常传播、选择暂停、成员快照、COW 和 Disintegration 调用次数。模型与命令使用替身。
- CoverageCatalog 校验通过，新增镜像识别为 `Registered / Exact / EngineMirror`。
- 玩家晚期伤害（证据文件为未纳入仓库的本地产物）：原生差分通过，2 格挡承受 5 点伤害后掉血 3。
- 双方晚期伤害（证据文件为未纳入仓库的本地产物）：原生 T1→T2 完整快照、Fork 与 continuation 对账通过。
- 游戏验证使用回合阶段、卡牌引用和 OnPlay 适配的组合构建。末击、多监听器原生顺序和任意第三方晚期 Hook 未覆盖；未作性能验证。

## 在线 DAU（2026-09-14）

- `node --test tools/OnlinePresence/dau-ui.test.mjs` 通过，验证 Chart.js 风格参数、折叠延迟创建、单点、空值、悬浮状态与实例复用；JavaScript 语法检查通过。

- 按用户澄清把入口移至监控后台后，Docker DAU/启动接入 3 项测试通过，前端两份脚本语法检查通过；日志后台 DAU 提交已回退，保留原有日志功能。未做浏览器视觉验收。

- Docker Node 24 中监控服务 24 项测试通过，包含 DAU 去重、午夜切日、重启、同分钟比较、缺口与零基期、90 天清理、心跳接入和认证。
- Docker Python 3.12 中日志服务 10 项测试通过，包含 DAU 共享快照、未采集/过期、登录、页面入口及既有版本删除/Agent API 回归。前端 JavaScript 语法检查通过；未做浏览器视觉验收。

## 0.38.1 发布验证（2026-09-13）

- 合并提交 `55b4e89` 相对 PR 最终提交 `1ebd323` 仅有工坊简介文档差异，行为源码一致。
- 本轮 Windows Release 编译零警告/错误，PowerShell 结构门禁 `search_files=90`。
- 本轮独立 GC 工具 `recovery` 六项通过；`recovery-lifecycle` 两项通过，`starts=1 restarts=1 forced=0`，覆盖真实 CLR 恢复及取消、退出和释放后的隔离。
- 本轮 Windows `END-TURN-CHOICE-REPLAY` 因已有普通游戏进程，在主机准入阶段超时，未执行场景；记录于 `.local/pr90-release-endturn.txt`。未停止该游戏进程，未尝试第二个同样受阻的场景。
- 复用下一节 PR 最终提交的两项 Linux 场景记录，明确不是本轮 Windows 重跑结果。版本与文档提交后执行最终 Release 构建并生成最小发布包。

## 当前使用约定

- Release、CompatibilitySmoke、结构门禁和无人测试分别证明构建、版本兼容、职责边界和请求级断言；一个层级通过不替代其他层级。
- 当前条目保留 Windows PowerShell 与 Linux Bash 的代表性启动命令；平台矩阵脚本会从本文提取这些命令，新增入口时同步维护两端。
- 协议 `Passed` 只表示该请求实际启用的断言通过；性能、路线质量、原生部署和未覆盖的边界必须单独声明。
- 历史条目中的版本、提交、环境和数值只用于追溯当时证据，不作为当前版本的自动通过结论。

完整旧版本可由 Git history 恢复；当前历史入口：[文档历史索引](history/README.md)
