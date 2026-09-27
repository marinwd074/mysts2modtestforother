# Codex 当前交接

> 只记录当前生产状态、当前风险和下一任务。已完成阶段过程从 Git history 恢复，不在本文件维护流水账。

## 基线

- CombatSolver `0.40.2`
- STS2 / RitsuLib pinned `0.107.1`
- .NET 9 / Godot 4.5.1 / `STS2_01071`
- 单人与多人共享生产搜索/模拟核心。
- 多人部署权限只属于本地玩家；不会部署队友动作。
- MultiplayerOnly 牌保留真实牌堆状态与抽牌距离，但不进入主动搜索/自动执行。

## 当前主目标：Rolling Horizon Reuse（2026-09-27）

- **阶段 C 已闭合，阶段 D 已进入生产实现。** 最新仪式兽问题包证明 future-turn normalized local-core shadow 在真实 `state_mismatch` fresh re-root 后出现 validated hit，且 output mismatch / collision 继续为 0；因此不再扩展 C 的观测面。
- D 第一小步复用现有 P1/P2 continuation repair，但拆开旧总开关：`LocalCoreR1RecoveryEnabled=true`，而 enumeration hint / P3 继续关闭。Runtime 只把旧路线中当前真实回合、普通无 Choice、带精确 CardStateKey 的连续 PlayCard 冻结为 R1 seed；从新 live root 重新模拟，失败立即 cold fallback，不继承旧 score、旧 simulator 或部署授权。
- R1 probe 仍最多使用原 5% 探针上限，但**不再扣减普通 Beam 的 MaxExpandedNodes / SoftTimeBudget**。若新根重放重新得到完整合法胜利，则仅转成现有 `PrimarySearchIncumbent` 的可证明 HP/回合下界，供普通 Beam 剪掉数学上不可能更优的分支；普通 Beam 宽度、枚举顺序、节点/时间上限、并发、Smart Potion 与 FinalOrdering 不变。诊断：`MP_LOCAL_XTURN_R1_RECOVERY`、`R1_REROOT_RECOVERY`、`primary_budget_unchanged=true`。
- **D2 已实现**：R1 probe 即使没有形成完整胜利，只要结果仍存活且是正常 SearchCompletion，也会从该**新根重新模拟结果**抽取连续的当前回合普通 PlayCard 前缀，回灌为 baseline Beam 的 ordering-only hint。冷根仍是唯一初始根，候选集合/合法性/retention/FinalOrdering 不变；一旦 Beam 偏离该前缀，hint 自动失效。Beam refinement 明确清空该 hint，保留独立冷序作为质量交叉检查。诊断增加 `validated_hint_actions`、`candidate_set_unchanged=true`、`refinement_hint=false`。 R1 hint 的排序优先级低于 `EstimatedLethal` 与 `UrgentDefense`，只高于普通价值启发式，避免旧路线提示压过即时斩杀或必要防御。

- **D3.1 已实现为纯 shadow validation**：R1 probe 在 `PruneFinal` 后最多保存 256 个 retained state 的纯值 evaluation；key 使用 exact `StateFingerprint` 加 turn/actionCount、药水/累计战损、traits/boundary 以及稳定 fingerprint 化的 `CombatProgressState`。baseline 最多观察 4096 个 retained state，先按 StateFingerprint 快速过滤，再对完整 key 比较 Score、HP/Block、敌方状态、资源与主要 retention 标量。没有 SearchNode/snapshot/simulator 跨 solver 保留，也没有跳过 replay/evaluation。

- **D3.2 已实现为首验后复用**：R1 probe 在 retained state 上额外冻结 BeamRankScore；baseline 第一次命中 exact/path-aware key 时仍调用原 `ComputeBeamRankScore` 并按 double bits 精确核对，只有该 key 首验一致后，后续重复 BeamRankScore 调用才直接返回缓存标量。任一 `store_conflict`、value `output_mismatch` 或 `beam_rank_mismatch` 都会把本次请求的复用门禁永久关闭并退回原计算。Replay、Snapshot、StateFingerprint、候选 admission、Beam 宽度/预算、FinalOrdering 与部署权限均不跳过。

- **D3.3 已实现为 request-local exact nonterminal transition hydration**：R1 probe 对普通无 Choice、无 EndTurn/药水/特殊 continuation 的 PlayCard 保存最多 32 个可 fork 预测后态；key 沿用 exact parent StateFingerprint + 完整动作 identity + policy identity，并在命中时核对 parent CombatIdentity/StateText。baseline 对每个 key 的第一次命中仍执行真实 replay，同时逐项核对 output StateKey + Continuation StateText；只有首验通过后，同 key 的后续调用才 fork 已缓存后态并重新执行 Snapshot/evaluation/transposition/retention。任何 store conflict、parent collision 或 output mismatch 都会关闭本次请求的 hydration。
- D3.3 不跨请求保存 simulator，不使用 local-core normalization，不复用旧 Score/SearchNode/部署授权。特殊 replay seed、Choice checkpoint、round checkpoint、VerifyIncrementalSearch 均禁止 hydration。baseline 主搜索返回后立即 Release 32 项原型，避免它们跨 Smart supplemental/audit 成为长期 GC 根。诊断新增 `R1_TRANSITION_HYDRATION` 与结果字段 `r1_transition_hydration_hits`；关注 `first_validations/validated_keys/hydration_hits/output_mismatches/reuse_disabled`。
- **D3.4 已实现为 frontier 等价性 shadow，不改变搜索行为。** R1 probe 与 baseline 只在 `PruneFinal` 比较 plain retained frontier：所有节点必须处在同一 turn/actionCount，且没有 Cycle/CrossTurn/OrderedMutation 租约/债务、未补偿 prediction gap 或风险状态。签名保持 retained 顺序，并包含 exact StateKey、路径损失/资源/traits、CombatProgress、完整动作链、TurnSetup 身份与父链 retention ranks；最多保存 16 个 R1 frontier 签名。
- D3.4 诊断：`R1_FRONTIER_SHADOW mode=plain_exact_ordered ... stored_signatures=... baseline_observations=... key_misses=... validated_hits=... signature_mismatches=... skipped_frontiers=... behavioral_reuse=false`。只有真实 fresh re-root 测试出现 `validated_hits>0`，且对应 frontier 无复杂租约/未知语义，才进入 D3.5“恢复 exact frontier”；当前不减少 expanded nodes。
- **D3.4B 已实现为 key-miss 分类，不改变搜索行为。** 每个 baseline miss 按 R1 probe 已存 key 互斥分类为 `turn_miss`、`action_count_miss`、`node_count_miss`；最多保留 16 条 `baseline(turn/action/nodes) → nearest_probe(turn/action/nodes)` 样本。汇总字段新增 `turn_misses/action_count_misses/node_count_misses`，逐样本日志为 `R1_FRONTIER_MISS`。若后续主要是 `node_count_miss`，才研究稳定 exact 子集恢复；若主要是 turn/actionCount miss，则先修 R1/baseline 深度对齐，不能直接放宽 frontier key。

- **D3.4C 已实现 exact retained-subset overlap，仍不改变搜索行为。** 新实机包在 Turn 5 给出 `turn_misses=0 / action_count_misses=5 / node_count_misses=1 / validated_hits=0`；同层样本为 R1 `turn=5 action_count=3 nodes=1` 对 baseline `turn=5 action_count=3 nodes=28`，证明整 frontier 等价并非合适恢复抽象。D3.4C 因此按 `turn + actionCount` 存 R1 plain retained 节点的 exact 语义 fingerprint 集合，在 baseline 同层比较 intersection，并输出 `probe_subset_hits/exact_set_hits/partial_overlap_hits/zero_overlap_hits` 与逐样本 `R1_FRONTIER_SUBSET`。只有 probe 节点稳定成为 baseline exact 子集且无 signature mismatch，D3.5 才设计“稳定 exact 子集/已展开子树恢复”；不再计划整 frontier 替换。

- 最新 EXOSKELETONS NORMAL 包完整跑出 D3.4C：`R1_FRONTIER_SUBSET turn=3 action_count=4 probe_nodes=4 baseline_nodes=60 intersection_nodes=0 probe_subset=false`，且 `zero_overlap_hits=1`。这否定了“恢复 R1 retained frontier/subtree 而不改变普通 Beam 候选集”的 D3.5 原方案；继续强行恢复会把 baseline 本来淘汰的节点重新注入，存在质量风险。
- 同包的 R1 transition hydration 更有价值：3 个 exact key 中 `first_validations=3 / validated_keys=2 / output_mismatches=1`，但旧实现因单个坏 key 把整请求 `reuse_disabled=true`，导致 `hydration_hits=0`。D3.3B 改为 per-key fail-closed：parent collision/output mismatch/store conflict 只拒绝对应 key；其他 key 仍需各自首验通过后才可 hydration。日志新增 `rejected_keys` 和逐项 `R1_HYDRATION_REJECTED_KEY action=... reason=...`。当前 D3.5 frontier 恢复暂停，优先验证 D3.3B 是否产生真实 hydration hit。
- 两份 ENTOMANCER ELITE 问题包来自同一场战斗的不同导出时间点。generation 2 成功进入 R1：`captured_actions=2 / resume_kind=seeded_search / R1_REROOT_RECOVERY status=bound_established`。D3.3B 本身稳定：`entries=32 / first_validations=2 / validated_keys=2 / output_mismatches=0 / rejected_keys=0 / reuse_disabled=false`，但 `hydration_hits=0`。原因不是 key 失效，而是 baseline 对这两个 exact parent/action 仅各遇到一次：首次必须真实 replay 首验，之后没有第二次重复；协调器又在 baseline 结束立即释放 cache。同一请求后续 potion-required 成员仍有约 41k + 50k transitions，却完全用不到这两个已验证 key。
- **D3.3C 已实现 request-tail hydration 生命周期**：baseline 仍输出 `R1_TRANSITION_HYDRATION stage=baseline`，但不释放；supplemental audits 仅继承 `R1TransitionHydrationCache`，不继承 R1 evaluation/frontier shadow；takeover / deterministic block potion / acceptable-loss early return 显式释放；正常请求尾输出 `R1_TRANSITION_HYDRATION_TAIL stage=request_tail validated_keys=... hydration_hits=...` 后释放。候选、Beam、评分、预算与部署权限均不变。

- 最新 EXOSKELETONS NORMAL 包成功进入 R1：`SEARCH_REUSE_MISS reason=state_mismatch` → `MP_LOCAL_XTURN_R1_RECOVERY actions=1` → `resume_kind=seeded_search` → `R1_REROOT_RECOVERY status=bound_established`。但在 baseline/Novelty 继续搜索前触发两次 `SEARCH_FAILURE`：`SimulationSnapshot.Simulator` 已由 `CloneValueOnlyForTransitionMemo` 释放。根因是 battle-scoped R0 terminal memo 返回的 simulator-free terminal snapshot 被 Novelty 的 terminal observation 无条件送入 `CaptureNoveltyFacts`。修复：只有 `IsTerminal && !HasSimulator` 时跳过 novelty facts；该 terminal 不进入 OPEN，返回值只影响被忽略的终局 novelty 统计。任何 nonterminal simulator-free snapshot 仍 fail-fast。此包因此尚不能作为 D3.4C subset 验收，需用修复后的构建复测。

- 新 EXOSKELETONS 实机包出现两次真实 `state_mismatch`，但均为 `resume_kind=cold_search seed_actions=0`，因此 D3.4C subset 根本未运行。根因是 R1 seed admission 复用了 exact continuation 的 `CombatIdentity`；该 identity 包含完整 enemy roster，队友提前击杀/移除敌人会在 R1 replay 之前把 seed 拒绝。修复后 exact continuation 仍要求完整 CombatIdentity 一致；仅 R1 seed admission 接受“同 seed + 同 players + actual enemy roster 是 expected roster 的子集”，新增/替换敌人、换玩家、换 seed、WorldVersion 未推进、scaling/card constraint 变化仍拒绝。新增 `MP_LOCAL_XTURN_SEED_CAPTURE admission_reason=... captured_actions=...`。- D3.2 诊断新增 `beam_rank_stores`、`beam_rank_first_validations`、`beam_rank_validated`、`beam_rank_mismatches`、`beam_rank_reuses`、`reuse_disabled`，并标记 `behavioral_reuse=beam_rank_after_first_validation`。Pinned harness 增加状态机合同：未首验不可复用、首验一致后可复用、任一 mismatch 后不可再复用。
- D3.1 只挂 baseline：Beam refinement 明确 `R1EvaluationShadowCache=null`，Novelty/P3/Smart supplemental 不借此获得候选或执行权限。诊断 `R1_EVAL_SHADOW mode=exact_path_aware ... store_conflicts=... validated_hits=... output_mismatches=... behavioral_reuse=false`。验收要求先看到 `validated_hits>0` 且 `store_conflicts=0`、`output_mismatches=0`，之后才进入 D3.2 真正复用 evaluation。

- 用户已将 `docs/Rolling_Horizon_Reuse_Architecture.md` 设为新的主执行目标；其阶段顺序 A → B → C → D → E → F → G → H 优先于下方历史 Quality-first “下一任务”描述。既有质量与执行保护继续作为约束，不重复施工。
- 阶段 A“现状与测量”已按 current HEAD `2c0c9274c5d83519e67ed4933ea5f37024e47239` 完成定向核对。设计文档最初基线 `eb70db3` 到 current HEAD 仅前进 2 个提交；其中生产代码变化是默认 multiplayer local-core 的 Smart Potion 调度去掉重复 early scout，不改变阶段 A 的 continuation / refresh / publication 分类合同。
- 候选发布时间已有 `SEARCH_E0_TIMELINE`，覆盖首次生成、完成评估、最终选中和首次发布；请求成员/阶段另有 `SEARCH_E0_MEMBER` / `SEARCH_E0_PHASE`。因此阶段 A 不新增另一套计时器。
- 跨根恢复已有明确分类：精确 continuation 记录 `resume_kind=exact_continuation`；精确失败后建议重放记录 `resume_kind=seeded_search`；无可复用建议记录 `resume_kind=cold_search`。P2 continuation-seed incumbent 仍从真实新根重放，不能原样部署旧结果。
- 同回合轻微 drift 已由 bounded refresh 记录 `MP_PLAN_REFRESH`，包含 `full_restart`、`prefix_replay`、`first_action_changed`、bounded work 与 `replay_latency_ms`；真正 fresh search 由 `MP_REACTIVE_FRESH_SEARCH reason=...` 保留触发原因。由此已能区分冷搜、精确续用、建议/前缀重放和完整重启。
- 对照固定上游 `Torch1230/CombatSolver@d231e9e51a0e58d6bfa1373c6265cce13ffd9a45`：Beam/portfolio、CrossTurn stand-pat/probe 与 value-only `SolvedRouteCache` 属共同或可移植基础；WorldVersion 驱动的多人重规划、bounded multiplayer refresh、continuation-seed 新根重放及 local-core 多人边界属于本 fork 现有扩展。
- 阶段 A **不修改搜索结果、预算、排序、并发或 Safe Execute 行为**，因此以现有诊断/合同作为基线闭合，不重复实现。
- 阶段 B“延迟影响合同”已完成：新增 `DeferredImpactCoverage` / `DeferredImpactOutcome` 旁路元数据，并挂到已有 `TurnOutcome` 与 Smart Block Potion 确定性重放路径；覆盖边界明确为 `CurrentTurn`、`NextLocalTurnStart`、`CombatTerminal`、`Incomplete`。该元数据**不进入 Score、Beam retention、FinalOrdering 或执行权限**，真实未来效果仍由模拟器唯一结算，避免重复扣费/重复加收益。
- 阶段 B pinned 0.107.1 定向验收通过（Actions run `36291423215`）：Release 与 `U0U1PinnedHarness` 均 0 warning / 0 error；Biased Cognition `Focus 4 → 3` 且覆盖从 `CurrentTurn → NextLocalTurnStart`，Outmaneuver 下一回合 Energy `3 → 5` 且覆盖闭合，当前回合 Bash 斩杀为 `CombatTerminal` 且 `ProjectedHp == PlayerHp == 80`。测试中确认 Borrowed Time 在 0.107.1 是**本回合牌费用 +1 的即时负担**，不是下一回合能量债务，因此不再用错误语义作延迟合同样本。
- 阶段 C“战斗级 R0 转移缓存”已进入受控落地：生产侧已有同战斗共享的 exact terminal R0 memo，只允许普通无 Choice `PlayCard`、无 checkpoint/fork-seed 的安全终局边；命中返回 simulator-free value snapshot，不持有可部署权限。键包含 parent `StateFingerprint`、完整动作身份和 policy/version identity，并用 parent semantic `StateText` 二次校验碰撞。
- 阶段 C 的非终局普通 `PlayCard` 暂时只启用 **shadow validation**：真实 replay 每次仍执行，`ActionReplayCache` 只记录 exact parent/action/policy 的 immutable output 是否一致，不改变 Score、Beam、预算、路线选择或 Safe Execute。这样先证明可复用性，再决定是否设计非终局纯值 hydration；当前明确禁止直接把 simulator-free snapshot 当作可继续展开节点。
- Phase C pinned 0.107.1 定向验收 run `36296652325` 通过：CombatSolver Release 与 pinned harness 均 0 warning / 0 error；terminal memo 为 `entries=1 hits=1`，cache on/off StateKey 一致，dynamics/policy/Choice 变化均拒绝；nonterminal shadow 为 `entries=1 validated_hits=1 collision=0 mismatch=0`，cache-off StateKey 一致。兼容静态门禁漏登记 `CombatBeamSolver.R0TransitionMemo.cs` 已补齐，run `36296743107` 通过。
- 阶段 C 的 shadow telemetry 已接入搜索结果与问题包诊断：独立记录 observations/stores/validated hits/collision rejects/output mismatches/dropped stores，并记录 validation 开销与“若未来允许命中可省掉的真实 replay 时间”。这些指标也进入 OfflineSearchHarness fixed-work metrics；不混入真正的 `TransitionCacheHits`。
- telemetry 定向验收 commit `a3be9c42`：compatibility run `36297242489` PASS，Phase C run `36297242539` PASS；Release 与 pinned harness 均通过。重复非终局 Bash 得到 `ShadowSolverObservations=1`、`ShadowSolverValidatedHits=1`、0 collision、0 mismatch，并确认 validation cost / potential-saved cost 均有记录。
- 阶段 C 完整搜索级跨请求 fixed-work A/B 已通过（run `36298685076`）。同一 combat/root 的 cache-off、warm、repeated 三次完整搜索均保持 `SameRoute=true`、`SameQuality=true`、`SameFixedWork=true`；三者均为 `306 expanded / 1352 transitions`。warm 写入 770 个非终局 shadow entry；repeated 得到 `770/770` validated hit（hit ratio 100%）、0 collision、0 output mismatch、0 dropped store。该样本中 shadow validation 自身约 `58.0932 ms`，对应可避免的重复真实 replay 约 `192.3156 ms`，另有 11 次 terminal R0 真命中。
- A/B 首次完整 Beam 运行暴露并修复了一个真实 R0 所有权缺口：terminal memo 返回 simulator-free snapshot 后，`BuildCandidate` 原本仍读取 `after.Simulator` 来计算 transition purity。现在 terminal memo 在存储时冻结该 purity 元数据，cache hit 后从 value snapshot 读取，不重新持有/恢复 simulator；parent 侧真实 simulator 仍负责遗物与动作分类。Release/pinned full-search 合同覆盖该路径。
- Phase C CI 已把 `phaseCFullSearch.Status == PASS` 固化为硬门槛并打印完整 A/B evidence；workflow concurrency group 升级为 `phase-c-validation-v2` 以绕过一次卡住的旧并发槽，仍保持 `cancel-in-progress: true`。
- 阶段 C 的 pinned 证据已经足够证明“同根跨请求非终局转移高度可复用且当前 key/output 合同稳定”，但**仍不直接开启非终局真实命中**。下一小步是用当前诊断字段收集真实多人问题包的 `shadow_replay_*` 运行时分布，确认实际 replan/root drift 下命中率与 mismatch；只有真实运行也持续 0 mismatch 且收益显著，才设计不持有 live simulator/GameObject 的非终局纯值恢复格式。
- 为该实机门槛新增 `source/tools/analyze-phase-c-shadow.ps1`：可直接输入问题包 zip、目录或单日志，汇总 RESULT 中的 observations / validated hits / mismatch / collision / terminal hits / validation ms / potential saved ms，并计算总体 hit ratio 与 potential net saved ms。该脚本只分析证据，不影响游戏运行。
- 首个真实多人问题包 `CombatSolver-0.40.2-MECHA_KNIGHT_ELITE-f97d6310af4143d6ade754b6f1d7c727.zip` 已核对：telemetry 字段存在，首轮正式搜索为 `6083 expanded / 32762 transitions`，后续有一次 route reuse；但两条 RESULT 都是 `shadow_replay_observations=0`。原因不是旧 DLL，而是生产搜索当时仍要求 `DetailedDiagnostics` 才执行 shadow validation。
- commit `74876505` 已改为**多人生产轻量采样**：实际 multiplayer root（`playerCount > 1`）即使关闭 DetailedDiagnostics，也会对普通安全非终局 R0 转移做最多 64 个 observation/搜索；单人生产搜索不启用该额外采样，DetailedDiagnostics 仍可全量观测。达到上限后停止额外 validation，并在 RESULT 输出 `shadow_replay_sample_limit=64` 与 `shadow_replay_sample_capped`。同时修正分析脚本原先错误寻找 SEARCH_PHASE 的问题，改为读取实际承载 telemetry 的 RESULT 行。
- 轻量采样验证：compatibility run `36300466197` PASS；Phase C run `36300466192` PASS。
- 第二个真实多人问题包 `CombatSolver-0.40.2-OWL_MAGISTRATE_NORMAL-778725cda178458d9415c46cf6bcbade.zip` 暴露了首版采样实现的两个结构问题：第一次正式结果为 `64 observations / 0 stores / 64 dropped`；第 3 回合 fresh search 则膨胀到 `9178 observations / 0 stores / 9178 dropped`，单 shadow validation 即消耗约 `11443.877 ms`。原因是 64 上限是每个 worker 的本地计数，8-lane 并行与 portfolio member 可各自重复采样；同时同一 battle-scoped cache 被前一个 stale/member 搜索灌满 4096 项，后续全部 dropped。该局仍保持 `0 mismatch / 0 collision`。
- 同一问题包的路线复用表现良好：`searches=3 reused=3`；第 2 回合允许 remote enemy HP decrease 后 exact continuation，第 4、5 回合 exact state-text continuation；只有第 3 回合因真实 local state mismatch fresh search。replan audit 为 `state_mismatch=1 deployment_drift=0 continuation_missing=0 manual_divergence=0`。
- commit `8ec1a47f` 将生产 shadow sampling 改成**搜索请求级共享原子预算**：同一个 request 中 Novelty/Beam/所有并行 lane 合计最多 64 次；最终 RESULT 读取共享聚合 telemetry，而非仅选中 solver/member 的局部计数。预算只在实际满足安全非终局 observation 条件时消费，避免 terminal/risk 分支浪费额度。这样一次 stale/member 搜索最多向 battle cache 写 64 条，不再能单请求灌满 4096 项。
- 请求级预算修复验证：compatibility run `36302333294` PASS；Phase C run `36302333296` PASS（Release、pinned harness、R0/full-search contract 全通过）。下一步需要用包含 `8ec1a47f` 或更新 HEAD 的构建再打一局多人战斗；目标是看到 fresh search 的 `shadow_replay_observations <= 64`、`dropped_stores` 不再因单请求爆仓、并开始出现跨请求 validated hits，同时继续要求 mismatch/collision 为 0。仍未开启非终局真实缓存命中。
- Queen Boss 长战斗问题包 `CombatSolver-0.40.2-QUEEN_BOSS-fd8bc47c872040f89cc51dc02b6dfcfd.zip` 来自 **8ec1a47f 之前的旧构建**，所以其中 shadow sampling 超限/mismatch 不再作为当前 Phase C 证据；但搜索质量与 UI 行为仍可用于定位。首轮从 `07:06:43.512` 开始，直到 `07:10:39.042` 才完成正式结果，约 235.5 秒；期间约 5 秒即出现 `SEARCH_E1_EARLY_PUBLISH`，但 action bar 在 searching 状态无条件隐藏 execute，因此玩家只能看到“停止计算”，无法点击现有 `ApplyCurrentTurn`。
- Queen 同一包还确认 rolling-horizon 终局跳变：早期正式路线连续两次都是 `0 projected_battle_hp_lost`、`TurnLimit` 的存活路线；第 4 回合 fresh search 在 18.0 秒内切换为窗口内完整胜利，路线同一回合连续打 3 张 Offering，`sold_hp=18`、`projected_battle_hp_lost=24`，仅因为第 6 回合能斩杀。该路线随后真实部署。问题不是“祭品必须禁用”，而是 `MultiplayerSinglePlayerCore` 的三回合窗口把 `complete_victory` 设为绝对首键，覆盖了低战损目标。
- commits `ea7a33bc` + `b033a3ef` 将该行为限定修正在 **MultiplayerSinglePlayerCore**：单人完整路线与实验 team-prediction 排序不改；local-core 在短窗口中先要求存活/不消费 death-save，再比较战略战损与已实现成长，同质量才优先完整胜利与更早结束。FinalPlanOrdering、Beam final retention、Coordinator 跨成员/药水结果与动态展示共用同一 opt-in 质量模式，避免某一层重新把高战损窗口斩杀抬回第一。没有新增 5%/固定 HP 容差，也没有禁用 Offering。
- 同一提交修复搜索中的采用入口：`SolverActionBar` 不再在 searching 时无条件隐藏 execute；当现有 `CurrentTurnPreview` 允许 `CanApplyCurrentTurn` 时显示“应用当前回合”，仍调用原 `RequestApplyCurrentTurn` / materialization / Safe Execute 链，不把未物化 speculative route 直接授权执行；完整可物化路线仍走“采用当前路线”。
- Queen 修复验证：最新 compatibility run `36303861214` PASS；Phase C run `36303861195` PASS，完整 Release、U0/U1 pinned harness、R0/full-search contract 全通过。Pinned 新增 Queen-style 合同：`24 HP complete victory` 不得压过 `0 HP surviving horizon`，而 `0 HP complete victory` 在同战损下仍优先。
- 最新实机包 `CombatSolver-0.40.2-BYRDONIS_ELITE-206c52a19e0c412da4b10dae86a821aa.zip` 已确认由 `0.40.2+daeff58b1631c215eea451cc04fbe817404fe5b6` 构建。Queen 后的滚动时域与 shadow 采样均生效：首次**有效**完整搜索为 `8749 total expanded / 51718 total transitions`，生产 shadow sampling 严格为 `64 observations / 64 stores / 0 hits / 0 collision / 0 mismatch / 0 dropped`，validation 约 `11.688 ms`；随后第 2 回合 exact-state continuation、第 3 回合 compatible remote-enemy-HP/shared-state continuation，均未重算。
- Byrdonis 同时暴露一个新的无效重搜：generation 17 在 world version 7、本地 `Turn 1 / Start` 启动；约 6ms 后本地仅从 `Start → Play`，`MultiplayerRouteChangeTracker.Version` 因 `local_turn_boundary` 从 1→2。之后队友使敌人 HP `182→165` 并改变自己的 Rage/Block，本地牌/HP/能量保持一致。约 7.72 秒后搜索完成时日志明确为 `full_stamp_match=false local_stamp_match=true`，却仅因 route version 1→2 被判 `SEARCH_STALE`，随后 generation 18 又完整搜索约 8.03 秒。该重复搜索不属于真实本地状态变化。
- commit `d898a0c9` 将多人 route tracker 拆成两个语义：原 `Version` 继续负责 debounce/调度；新增 `InvalidationVersion` 只在真正允许使路线失效的事件（当前主要是启用斩杀窗 HP 重算时的 enemy-HP invalidation）增长。搜索完成、等待部署、已完成路线部署与 continuation/refresh 的 route compatibility 只比较 `InvalidationVersion`；`local_turn_boundary` 仍能触发调度但不再让正在运行的 local-core 搜索结果过期。没有放松 local stamp、本地状态或真实斩杀窗失效检查。
- Byrdonis stale 修复合同已加入 U0/U1 pinned harness：调度边界必须满足 `Version=1 / InvalidationVersion=0`；真实 enemy-HP invalidation 必须同时推进 scheduling 与 invalidation；route-scoped completion 在 world `7→22`、full stamp 不同但 local stamp 相同且 invalidation 不变时必须接受，invalidation 变化时必须拒绝。Phase C run `36305583667` PASS（完整 Release + pinned harness + R0/full-search），最终 compatibility run `36305705115` PASS。
- 后续实机包 `CombatSolver-0.40.2-BYRDONIS_ELITE-194b779e710d4e18931d39d5e7994a98.zip` 创建于 2026-09-27 08:09 UTC，早于 `d898a0c9`（08:14 UTC），因此其中首回合 `local_turn_boundary` stale 不能验证失效/调度版本修复。但该包提供了 Phase C 需要的真实 re-root 样本：accepted generation 24 为 `64 observations / 64 stores / 0 exact hits / 0 mismatch / 0 collision`；第 2 回合因真实 local mismatch（enemy HP `165→150`、finished-card-play `4→6`、本地 Strength `3→5`）再次 cold search，仍为 `64 / 64 stores / 0 exact hits / 0 mismatch / 0 collision`。第 3 回合随后以 compatible remote-enemy-HP/shared-state continuation 直接复用。
- 这组数据说明 battle-scoped cache 本身没有坏：每轮都稳定写入且 0 mismatch；问题是 exact parent StateFingerprint 把已明确允许的 remote/shared drift 也编码进 key，导致真实多人 re-root 下跨请求命中率为 0。generation 23→24 尤其具有代表性：local-core stamp 兼容、只是远端/共享状态变化，exact shadow 仍完全 miss。
- commit `482ca129` 增加 **MultiplayerSinglePlayerCore normalized shadow A/B**，不增加任何 replay，也不改变 exact cache/搜索/部署。对同一批最多 64 个已完成真实 replay 的样本，同时使用现有 `LiveCombatStamp` local-core normalization 去除已被 route-scoped completion 明确允许的 `P/R/E*/AI*/MS*` remote/shared 字段，并在无 Gold Axe 依赖时归一化 shared finished-card-play count。归一化 parent/action 使用独立 battle-scoped shadow cache，output 比较 normalized state + boundary/turn/death/risk/gap；Score 固定为 0，只用于测量未来“transition delta/hydration”是否可能，不证明完整 snapshot 可直接复用。
- 新问题包 RESULT 会同时输出 `shadow_local_core_observations/stores/validated_hits/collision_rejects/output_mismatches/dropped_stores`。分析脚本也会给 exact 与 local-core normalized 两组 hit ratio。只有 normalized 在真实 fresh re-root 下出现稳定 hit 且 `output_mismatches=0`，才继续设计非终局 delta/hydration；当前仍 **不跳过模拟、不启用非终局真实缓存命中**。
- normalized shadow A/B 验证：compatibility run `36306269116` PASS；Phase C run `36306269102` PASS，完整 Release、pinned harness、R0/full-search contract 全通过。Pinned 额外验证 synthetic remote/shared drift 在 local-core normalization 后 key/output 等价，并由独立 shadow cache 从 store 转为 validated hit。
- 仪式兽 Boss 问题包 `CombatSolver-0.40.2-CEREMONIAL_BEAST_BOSS-c7fae94db05a452da92c47c84552d11e.zip` 暴露“显示路线 ≠ 应用当前回合路线”。点击前 checkpoint `000011-search_request_Manual` 显示 start turn 4 的完整两回合路线，预计累计战损 `36`、20 actions，当前回合从 `Offering → Strike → Corruption → Offering → Burning Pact... → EndTurn` 开始，并在 turn 5 斩杀。点击后 checkpoint `000012-search_current_turn_adopted` 实际物化为另一条仅 4 actions 的当前回合路线：`Defend → Skill Potion(True Grit) → True Grit → EndTurn`，预计累计战损 `13`。
- 日志确认这是 takeover 重新选候选而非显示延迟：`UI_ACTION action=apply_current_turn` 后约 624ms，worker 记录 `SEARCH_CHECKPOINT_ADOPTED scope=current_turn route_candidate=current_turn_candidate ... projected_battle_hp_lost=13 expanded=2162`。旧实现只让 ApplyCurrentTurn 请求携带 kind；UI 展示的 `CurrentTurnPreview` 没有绑定 materializer，worker 收到请求后使用当时更新后的 `CurrentBestNode/CurrentTurnCandidateNode`，因此可以“偷换”用户看到的路线。
- commit `de12c74c` 将 ApplyCurrentTurn 改成与 route adoption 相同的**已渲染候选绑定**：每个真正发布到 UI 的 current-turn preview 同时生成 `candidate_version + exact actions + lazy materializer`；主线程仅在 `RenderedCurrentTurnAdoptionSeed` 存在时允许按钮，点击请求冻结该 seed。Beam/Coordinator 收到请求后优先物化该 exact displayed seed，不再重新选择更新后的 incumbent。新增诊断 `SEARCH_CURRENT_TURN_DISPLAYED_CHECKPOINT`，UI_ACTION 记录 candidate_version/displayed_actions。Safe Execute、合法性检查、真实多人状态兼容边界均未放宽。

- 最新仪式兽问题包 `CombatSolver-0.40.2-CEREMONIAL_BEAST_BOSS-d3ee7d75d1ec42c88e70a478567eef51.zip` 证明旧“偷换当前回合 seed”已修复：成功 adoption 的冻结 seed 为 3 actions，实机确实执行 `SECOND_WIND → POMMEL_STRIKE+ → EndTurn`。但点击前搜索已记录 `searched_turn_layers=3 last_planned_turn=4`，adoption 后却变成 `CurrentTurnAdoption / PartialLocalCrossTurnProjection / continuations=0 / future_route_preserved=false`。
- 根因有两层：Coordinator 只在新 progress 非 null 时局部替换 current/future preview，可能把新 current-turn candidate 与旧 speculative future 拼成一个 UI；同时 `CombatBeamSolver.Phases` 只对实验 `MultiplayerLocalCrossTurn` 保留 CurrentTurnAdoption continuation，错误排除了默认 `MultiplayerSinglePlayerCore`。修复后完整 route preview 同时生成同候选的 current-turn adoption seed；Coordinator 原子替换 current/future bundle；默认 local-core CurrentTurnAdoption 保留已物化 continuation；Runtime 有 continuation 时继续显示完整路线。Deployment 仍只过滤并执行 `StartTurnNumber` 的动作，不授权未来回合。
- 该修复新增 pinned 合同：RequestApplyCurrentTurn 必须保留同一 rendered seed 的对象身份、candidate version 与 exact actions；CompleteTakeover 后 seed 必须清除。验证：compatibility run `36308113398` PASS；Phase C run `36308066682` PASS，完整 Release、pinned harness、R0/full-search contract 全通过。
- 阶段 C 的实机门槛进一步收窄：仪式兽 generation 5 的真实 `state_mismatch` fresh re-root 对旧 local-core shadow 为 0 hit，但同一 Turn 4 的 generation 6 手动请求可达到 `64/64 validated hits` 且 0 mismatch/collision。说明缓存正确、同根跨请求复用成立，剩余缺口集中在前一请求没有留下足够的“下一真实根附近”样本。
- 当前生产 shadow 总额度仍为 64，但不再全部先到先得：当前回合最多占 48，保留 16 给 `parent.Turn > startTurn` 的未来本地回合普通 R0 转移。新增 `shadow_replay_sample_current_turn/future_turn` 诊断。真实 replay、Beam、评分、搜索预算、路线选择、缓存真实命中与 Safe Execute 均不改变。
- 下一次 current-HEAD 实机验收：前一请求应出现 `shadow_replay_sample_future_turn > 0`；随后真实下一回合若 fresh re-root，观察 `shadow_local_core_validated_hits` 是否从 0 上升，并继续要求 `output_mismatches=0`、`collision_rejects=0`。若仍为 0，再证明是 normalized key 差异而不是采样饥饿后继续分类。
- 同一仪式兽包也给出 Phase C 的正向实机证据：被采用的 turn-4 fresh search 中 exact shadow 与 local-core normalized shadow 都是 `64 observations / 64 validated hits / 0 collision / 0 mismatch / 0 dropped`；exact potential-saved 约 `23.441 ms`。这说明在同一真实 root 的重复工作上 exact cache 合同已稳定；非终局真实命中仍未开启。

## 当前多人架构

1. **共同搜索核心**：单人完整路线与多人本地跨回合共用 Beam、Novelty、成长、遗物、药水和长期收益基础。
2. **团队目标/情景**：Team Objective、Shadow teammate forecast、Scenario Matrix / Robust 和 Carry 统一属于实验多人预测栈。生产默认关闭该栈，使用真实多人战斗根 + local-single-core；仅“设置 → 常规 → 多人模式 → 启用多人预测算法（实验）”一个总开关控制是否启用整套预测栈。
3. **交错预测**：实验预测栈开启时支持 local → forecast-only teammate → local 的 detached 模拟；队友节点没有 deployment authority。U5 reverse-order 等价探针只在详细诊断开启时运行；生产搜索不再为纯日志额外 Fork/重放每条队友路线。队友联合预测与 Robust 情景复评继续作为内部搜索阶段保留，但不再暴露独立用户开关，避免与总开关形成无效/矛盾组合。
4. **路线刷新与跨回合热启动**：轻量 HP/Block drift 可对少量保留候选做 bounded refresh；精确 continuation 仍优先。进入新回合后若精确状态不匹配、但战斗/本地玩家/多人规则身份兼容，默认 local-single-core 会保留旧路线当前回合的普通牌建议。P2 起该建议不再占普通 Beam 初始槽位，而由独立、最多 5% 节点/时间额度的 incumbent probe 从真实新根重放；中途失效只保留已验证前缀，第一步不可用则冷搜。实际 probe 工作从主搜索余额扣除。
5. **Safe Execute**：逐本地动作使用原生提交、稳定等待和 predicted/live semantic post-state 对照；P2 seed incumbent 只有完整胜利并经现有正式质量准入后才成为手动可采用结果，不能复活旧 request/generation，也不会自动提前授权旧动作。
6. **药水**：本地药水已接入多人 Safe Execute；药水槽、策略面板、Smart/保护/强制与单人共用。多人只读取/消耗本地玩家药水，玩家类目标只允许自己，攻击/状态药仍可作用敌人；是否值得消耗药水固定按本地玩家的单人药水基线判断，队友战损不能改变用药资格。
7. **遗物策略**：多人复用单人遗物模拟、计数器和策略面板，但策略所有权只绑定本地玩家。队友持有的遗物不会显示为“已持有”，也不会生成本地遗物计数目标；队友遗物的真实战斗效果仍可作为战场状态参与多人预测。
8. **特殊牌策略**：多人复用单人的本地特殊牌策略。至亮之炎的最大生命消耗额度只统计本地动作玩家自己的使用，队友使用不会占额度；成长机会只扫描本地牌组及本地复制来源。依赖战斗历史计算变量的 Gold Axe、Voltaic、Tear Asunder、Pull From Below、Murder、Supermassive 在多人也保留状态指纹，不再因 PlayerCount > 1 被关闭。
9. **本地核心专项 P0–P4**：P0 候选刷新、P1 新根建议重放、P2 独立 incumbent、P3 Smart 跨家族调度均已按任务书完成；P4 又移除了 calculated-history 重扫、单 captured-player 药水指纹排序和 Snapshot 内多处 LINQ 分配。三项 P4 生产优化都有 pinned 0.107.1 fixed-work A/B，路线、最终质量和节点/转移工作保持一致；不启用跨根 transition cache、整树 re-root、额外 DFS/MCTS。

U0–U6 的实现与 pinned/合同阶段均已完成。U5/U6 的部分真实 Host/Client observation → fresh replan 边界仍保留为运行时未验证项，不由 pinned replay 代替。

## Quality-first 当前状态

- **第一项：bounded multiplayer refresh**：已实现并通过 pinned/合同；真实多人正反例仍可继续补证据。
- **第二项：本地药水执行闭环**：已实现并通过 pinned/合同；真实多人自动喝药仍属于实机验证边界。
- **第三项：实际坏路线定位**：进行中。原则是只修复能由当前 HEAD 问题包证明的路线质量缺口，不凭感觉调整 Robust/Beam 权重。

已确认的第三项样本：

- 历史 ANGER 样本：未完成多人路线曾被 `AngerCopiesGenerated` 的最终排序过早惩罚；已有定向修复。
- 历史 X1 T3 空推荐：等价质量下 `ActionCount` 短路线曾压过当前回合实际出牌；已有定向修复。
- Beam retention A/B 诊断已接入：同一真实候选池同时观察 TeamObjective 与 legacy 单人排序，不改变生产选择。
- 最终排序归因新增始终可用的 `MP_QUALITY_LAYER`：记录原 baseline 胜者、Scenario 后的原始 baseline rank、Chance 后最终 baseline rank，并直接标记 `baseline|scenario_robust|shadow_chance`。它不依赖 U4 全矩阵完整，因此 E4 严格提前淘汰时也能判层；原 `MP_QUALITY_SORTING` 继续只承担完整 U4 三策略对照。
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

## 搜索效率 E1（2026-09-25，已关闭）

- E0 已确认两个可离线复现单人样本存在“正式终局候选已选中，但仍等待 materialization / annotation replay 才首次发布”的真实延迟；E1 只修这个已证明的发布缺口，没有提前采用未完成 Scenario 复评的候选。
- 当前实现只在 production `FinalOrdering.Select` 已完成、Scenario rerank（若启用）与 deterministic block-potion insertion 已确定后，生成不持有 simulator 的正式路线预览并通过既有 `SolverProgress` 发布；完整 `SolverResult` 仍继续完成 replay、遗物标注和压平。
- 早发布的正式预览不会携带 `SolverRouteAdoptionSeed`，因此不会留下闭包访问仍存活或随后释放的 `SearchNode/SimulationSnapshot`。Coordinator 只有在该候选通过现有全局 `SolverInterimResultOrdering` 展示准入后，才记录 `Published` milestone。
- 固定 0.107.1 A/B（run `36102487372`）：
  - 简单攻防：baseline selected→published **2195.050 ms** → E1 **7.592 ms**。
  - 抽牌/能量：baseline **124.567 ms** → E1 **6.805 ms**。
  - 两个样本均 `sameRoute=true`、`sameQuality=true`、`sameWork=true`；最终动作、战损/药水/结束边界，以及 expanded / transitions 都未改变。
- E1 计划中的“消除重复评估”按证据条件处理：当前没有确认到同一正式 evaluation context 下 Scenario Matrix / final ordering 被重复复评的生产缺口，因此没有为了完成阶段引入跨候选/跨根缓存，也没有缓存可变 simulator。现有请求内缓存继续沿用。
- 最终验证：compatibility run `36102487522` 全 PASS；Pinned run `36102487372` 的 Release、E0/E1 A/B、U0/U1、U2、P0 contracts、P0/P1 runtime 与历史 P0 A/B 分类链全部 PASS。
- **E1 已关闭。** 后续若出现“同一正式上下文重复复评”的新证据，再单独加入严格键控的请求内不可变结果缓存；当前不扩大缓存面。

## 搜索效率 E2（2026-09-25，已关闭）

- 第一切片 `d92d25bb` 建立 `SearchStepStatus` / `SearchWorkAllowance` / `SearchStepResult` / `IResumableSearch` 与完整父节点安全点；第二切片把 incumbent、frontier/completed、active/ended/nextPlays、fallback、父节点游标和内存高水位集中进成员状态。
- 第三切片从 `3d60b7f0` 开始把整个搜索成员改成真正可离开调用栈再恢复的 `SearchMemberExecutionSession`：`SolveCore` 由 iterator state machine 保存阶段位置，session 的 `Step(allowance)` 在完整父节点/并行 wave 提交后返回 `Yielded`，下一次 `Step` 从同一成员状态继续。
- 当前代码 HEAD `a77d4085`：主 Beam portfolio 成员已由 `CombatSearchCoordinator` 按 **256 个已提交父节点**为生产切片连续 Step；当前仍一次把同一成员跑完后才进入下一个成员，因此 E2 没有改变 portfolio 成员顺序、选择规则或 E3 调度语义。
- `SimulationNotificationIsolation` 只覆盖每次 `Step` 的真实执行区间，session 暂停时不会继续占用通知隔离；诊断字段继续保持既有 `frontier=` / `ended=` 格式。
- deterministic P0 fixed-work A/B 已直接对成员 session 验证三种执行方式：continuous、1-parent slice、8-parent slice。三者完整 18 个 action token 逐项相同，均为 `NodeLimit`、projected loss=0、final HP=66、enemy HP=4、expanded=1200、choice branches=0、continuations=3、transitions=5958、committed parents=1200。
- 实际恢复次数也被门禁验证：1-parent 为 **1200 yields**，8-parent 为 **150 yields**；因此不是“开了接口但没有真的暂停”。
- 生命周期门禁 PASS：cancel probe 在提交 1 个父节点后取消，累计父节点不增加且所有 live simulator 已释放；Dispose probe 同样在 1 个父节点后释放所有 live simulator，并拒绝后续 resume。
- 最终验证：compatibility run `36095849302` 的 static-consistency 与 L1 contract-tests 全 PASS；Pinned 0.107.1 run `36095849283` 的 Release、E0、U0/U1、U2、P0 contracts、P0/P1 runtime、历史 P0 A/B 全链 PASS。
- 本阶段始终没有改变 Beam 宽度、评分、Robust、药水/遗物/特殊牌、多人数值语义或执行权限。
- **E2 已关闭。** 后续除非再次修改 member-session 状态所有权、安全点、取消/Dispose 或累计预算语义，否则不继续在 E2 增加可恢复搜索改造。

## 搜索效率 E3（2026-09-25，已关闭）

- **交付结论：生产采用 E3A 固定轮转；E3B 自适应不启用。**
- Smart 用药层复用 E2 `SearchMemberExecutionSession`，每个工作片最多 256 committed parents / 1024 transitions。多个 exact-potion 成员可同时驻留；暂停成员不累计自己的搜索预算钟，请求级 deadline 仍按真实墙钟统一约束。
- fixed 与 serial 的 deterministic P0 门禁完全等价：动作序列、boundary、战损、终局 HP、敌方 HP、结束回合、显式药水数一致；总工作均为 **1536 expanded / 6310 transitions**，两个 `potion_required` 成员均为 **2146 / 2172 transitions**。
- fixed 确认是真轮转而非提前建 session：第二个用药成员在约 **646.5 ms** 首次获得真实工作，而第一个成员约 **1053.9 ms** 才完成；serial 中第二成员要等第一个约 **653.2 ms** 完成后才开始。
- E3A 已在 `3073c757` 作为 full-search 生产默认启用；不新增 UI，不改变目标函数、Beam 排序、Robust、Smart 用药资格、遗物/特殊牌、多人数值或部署权限。Novelty 与没有 E0 证据的其他 Beam 组合未在本阶段重构。
- E3B 保留原计划的实验实现：同硬约束类别内用 `Δq/Δt` EMA，完整胜利/消除死亡风险使用独立优先级，并保留探索份额、重要候选优先和确定性 tie-break；内存压力可把驻留成员收缩到 1。
- 本轮 pinned 资格结果：`AdaptiveSameQuality=true`、`AdaptiveSameFixedWork=true`，但 `AdaptiveBonusSlices=0`，因此 `AdaptiveTriggered=false`、`AdaptiveReducedWork=false`、`AdaptiveQualified=false`。该样本没有证明 adaptive 比 fixed 更早达到质量或减少工作，所以按计划保持关闭，不为了“自适应”增加生产复杂度。
- 最终验证：compatibility run `36100277486` 的 static-consistency / L1 contracts 全 PASS；Pinned 0.107.1 run `36100277430` 的 Release、E0、U0/U1、U2、P0 contracts、P0/P1 runtime 与历史 P0 A/B 分类链完成。P1 的 5 秒 timed 样本在已胜利状态触及 TimeLimit，但 5000-node fixed-work 两种目标均 PASS，历史分类为 `FIXED_WORK_PASS_TIME_BOUNDARY`，不作为搜索语义回归。
- **E3 已关闭。** 后续若没有新的 time-to-quality 数据证明 adaptive 稳定优于 fixed，不重新打开 E3B。

## 搜索效率 E4（2026-09-25，已关闭）

- 生产继续使用原 **Robust** 固定四压力情景语义；E4 只改变这些情景的复评组织方式，不修改团队目标、Beam 排序、药水/遗物/特殊牌、多人牌过滤或部署权限，也没有提高默认总预算。
- 严格模式现在先完整评估第一个可用 current-decision 作为 incumbent。后续候选继续复用当前 U3 的一次共享 Shadow 搜索，但情景 replay 按 incumbent 中更可能暴露弱点的压力顺序执行。
- 对部分已评情景构造合法的 Robust 乐观下界：存活/终局字段只使用已经不可逆暴露的坏结果；worst-loss / worst-player / team-loss / enemy-durability 使用已观察最大值；未知 mean-loss 明确取 **负无穷**，未知情景从不按 0 或“良好结果”写回矩阵。
- 只有该候选在“未评情景全部取得最理想值”的情况下，仍按现有完整字典序严格落后于一个已完整评估的 incumbent，才允许停止剩余 replay。等价/tie 情况不会剪枝；被跳过的格子保持 `Unknown`，并记录 `strict_eliminated / strict_reason / skipped_scenario_replays`。
- 最终 Robust 排序允许“完整候选 + 已被严格证明淘汰的候选”形成闭合比较；普通预算中断/Unsupported 造成的 `Unknown` 仍 fail closed 回原 baseline。U4 nominal-reference / bounded-risk 诊断只有所有候选都完整时才运行，不把 E4 缺失格子伪造成完整矩阵。
- 小型完整枚举门禁覆盖：严格渐进与全量复评选择同一 Robust 胜者、保留同一 baseline tie-break；另有“前三个压力情景都更好、最后一个情景才把候选翻成劣势”的反例，严格模式必须看到最后一格后才能淘汰。另一个明显劣势候选在首个压力情景后可安全跳过剩余 **3** 次 replay。
- E4 的近似“首动作一致率 C”没有进入生产：计划本身规定它不是正确率/置信度，也不是严格停止证明。若未来要启用，必须作为单独近似策略做 A/B，不混入本次保持结果等价的优化。
- 验证：compatibility run **36104370041** 的 `static-consistency` / `contract-tests` 全 PASS；Pinned 0.107.1 run **36104358801** 的 Release、E0、U0/U1、U2、P0/P1 与历史分类链全部 PASS。
- **E4 已关闭。** 当前实现能真实省掉的是已生成四压力路线之后的部分 scenario replay；共享 Shadow 搜索仍按一次/候选完整计费，不把这段成本伪装成 E4 收益。
## 搜索效率 E5（2026-09-25，已关闭）

- **生产只启用 E5A 策略引导动作枚举；E5B 中途估值不启用。** 本阶段只改变合法卡牌动作进入 replay 的先后，不改变动作集合、每节点候选额度、Beam retention / final ordering、Robust、Smart 用药、搜索预算或部署权限。
- 串行 Expand 与并行 PrepareCardActions 统一复用同一份 prepared-action 枚举，避免两条搜索路径以后再次出现动作顺序漂移。
- 动作顺序使用确定性只读提示：**估计可立即斩杀 > 当前存在预计掉血时的防御 > 每资源战略价值 > 原始战略价值 > 低资源成本 > 原始稳定顺序**。战略价值复用已有 CardChoiceSupport.CardValue；目标生命、当前能量/星能只用于排序提示，不参与剪枝或结果评分。
- 排序是完整候选集上的稳定优先级，不会因为提示分低而删除动作。legacy 手牌顺序保留为 pinned A/B 测试入口，不暴露用户设置。
- Pinned 0.107.1 E0 A/B（run **36106327175**）两个代表根均保持 sameQuality=true、sameRoute=true：
  - simple：legacy **836.554 ms / 369 expanded-at-generation**；E5 **947.748 ms / 369**。确定性生成工作量不变，约 111 ms 墙钟差按运行噪声处理，不把它宣称为收益。
  - draw_energy：legacy **1641.287 ms / 839 expanded-at-generation**；E5 **1633.226 ms / 823**，最终赢家提前 **16** 个展开节点生成，墙钟约提前 **8.062 ms**。
- 这组证据证明 E5A 至少能在抽牌/能量根上让最终优质路线更早出现，同时 simple 根没有增加生成所需展开数；但没有证据表明剩余主要瓶颈来自“路线已生成却因中途估值过低被 Beam 丢弃”。因此按计划不修改 Beam 中途估值，避免把动作排序与评分语义混在同一阶段。
- 合同增加纯排序门禁，覆盖斩杀、防御、价值效率和完全平手时的稳定原顺序。最终验证：compatibility PR run **36106409949** 的 static-consistency / contract-tests 全 PASS；Pinned run **36106327175** 的 Release、E0/E1/E5 A/B、U0/U1、U2、P0/P1 与历史分类链全 PASS。
- **E5 已关闭。** 后续只有新的真实坏路线证据明确显示“目标动作已经生成，但在中途评分/Beam 保留处被淘汰”时，才单独重开中途估值工作。

## 搜索效率 E6（2026-09-25，已关闭）

- **E6A 不启用新的 pre-replay 可交换顺序剪枝。** 当前生产模型没有一份已证明完备、同时覆盖 Hook、战斗历史、RNG、死亡/复活、Choice/调度以及多人观察机会的动作读写集；按原计划，未知 Hook 必须视为全局依赖。仅凭卡牌类型、目标或现有 `IsPure` 历史分类不足以证明 `F_B(F_A(s)) = F_A(F_B(s))`，因此不把经验性“看起来可交换”升级成硬剪枝。
- 生产继续保留现有 **完整 replay 后的精确状态去重**：`ExactTranspositionKey` 固定建模战斗状态，`TranspositionFrontier` 再以保守 Pareto label 区分药水/卖血/累计战损、团队与最弱队友损失、ActionCount/Score、路线 traits、边界/死亡/胜利、PredictionGaps 与 CombatProgress。它不能省掉首次 replay，但不会为了 E6 把未知副作用当作等价。
- 重新接通并修复 `TranspositionFrontierChecks`：合同现在跟随 current label 结构，验证完整等价 label 会合并；Boundary、PlayerDead、AllEnemiesDead、PredictionGaps、CombatProgress 不同不会被误并；同时用独立 comparator 做随机决策对照，并保留 singleton frontier 分配回归检查。该检查已加入 PowerShell/Bash contract 入口。
- **E6B 不启用局部 exact-kill DFS。** E0/E5 当前证据没有出现“主 Beam 候选池漏掉一条已知合法斩杀”：E0 的多人慢例是最终赢家直到后置 portfolio 成员才生成，E5 只证明动作顺序可让部分赢家更早出现。原计划明确要求只有 E0 证明 Beam 漏斩杀时才启动 DFS；现在加入会成为没有证据支持的额外搜索成本，并破坏“同一请求总预算”约束。
- 本阶段因此**不改变生产搜索行为、Beam/Robust/Smart 药水/遗物/特殊牌语义，也不增加默认节点、时间或情景预算**。E6 的交付是把两类高风险优化的启用条件锁死，而不是为了阶段编号强行加入近似剪枝。
- **E6 已关闭。** 未来只有新的 current-HEAD 问题包能证明“存在合法短窗口斩杀，但完整候选池没有生成它”时，才重开 E6B，并限定为共享现有请求预算的当回合/短窗口 DFS；若要重开 E6A，则必须先有覆盖相关 Hook/历史/RNG/死亡/调度语义的完备读写契约与反例门禁。

## 当前未验证边界

- 当前 HEAD 的真实多人 Beam retention A/B：需要在“明显不如手打”的合法局面上确认更好路线究竟在 Beam、portfolio、U3/U4 还是执行层丢失。
- U5/U6 的真实远端插入后 WorldVersion advance → fresh search → old request dormant。
- 具体连续抽牌/Choice 链仍应以新问题包为准，不把旧日志当当前代码证明。

## 下一任务

搜索效率 **E0–E6 已按证据全部关闭**。下一任务回到 Quality-first 第三项：只处理 current HEAD 新出现、且有问题包与合法手打前缀证明的明显坏路线；先沿 `FINAL_CANDIDATE → MP_QUALITY_SORTING → FINAL_SELECTION → Safe Execute` 定位丢失层，再只修改有证据的那一层。



## 2026-09-25 LOUSE_PROGENITOR T3 cross-family time-to-quality

- 新问题包 `ece84d5632d449f7b8717c9ef68f54df`：最终 4 回合路线在 T3 使用 `OFFERING+`，并通过 `ATTACK_POTION -> FIEND_FIRE` 压缩战斗。
- E0：winner `potion_required` 首次生成约 45.235s，最终发布约 46.439s；Novelty ~5.010s，无药 Beam ~37.903s 且 `TimeLimit`，一药水层 ~3.527s。结论：跨搜索家族的串行 starvation。
- 当前修复：Novelty 后插入 bounded exact-one-potion scout；它与主 Beam 共用同一总预算。完整 scout 在最终无药基线下重新校验后可复用，截断/不再合格则回退原 E3 Smart audit。
- 实机回归关注：T3 的 `potion_required` member 应显著早于无药 Beam 完成获得工作；最终路线质量不得退化。需要用户用当前 HEAD 重跑同类局面确认真实 time-to-quality。


## 2026-09-25 multiplayer quality rollback to local single-player core

- 5 个连续实战包显示跨战斗退化，不是单卡问题。四个 DECIMILLIPEDE 包合计 307 次 FINAL_SELECTION，233 次未发生 Scenario/Chance rerank，坏路线主要由 multiplayer baseline 直接产生。
- 最明显样本中本地 projected_hp 已为 -25/-28、all_players_alive=false，但 AdaptiveLethalTempo 仍按团队损失/敌方耐久区分死亡路线；这与用户“单人算法直接打多人高血量怪物反而更好”的 A/B 观察一致。
- production policy 已切换为 local-single-core：真实多人 root + 单人质量排序；team objective / teammate forecast / scenario reevaluation 生产关闭。多人网络、怪物语义、目标语义、安全执行与 continuation 不变。
- 旧多人质量层保留为测试/研究代码，可由测试直接 override SearchPolicySnapshot，不删除历史 U2/U3/U4 证据。
- 新诊断：`MULTIPLAYER_QUALITY_MODE mode=local_single_core team_objective=false teammate_forecast=false scenario_reevaluation=false`。


### CI contract migration for local-single-core

- `E0PinnedHarness --scenario teammate` now explicitly opts into Advisor capability before root capture, so the detached two-player root is an admitted local-player multiplayer search instead of an optional unsupported probe.
- The teammate E0 scenario now validates the **production** policy: `MultiplayerLocalCrossTurn` route semantics with Team Objective, teammate forecast and scenario reevaluation all disabled; any Shadow phase is a failure.
- `test-u2-search-kernel.ps1` was updated from the obsolete “production multiplayer keeps team objective enabled” assertion to the new local-single-core production contract. Experimental U3/U4/Shadow contracts remain intact and separately tested.


- local-single-core also disables `MultiplayerCarryRankingContext` inside FinalPlanOrdering when Team Objective is off. Carry remains available to experimental/team-objective tests, but production local quality no longer has a remote-risk tie-break after otherwise equal local routes.


- Pinned E1 early-publication A/B no longer treats exact expanded/transition equality as semantic correctness. The callback changes wall-clock overhead while portfolio refinement is time-gated, so exact work can differ even with the same route and quality. `sameWork` remains in evidence; route and final quality are still hard failures.


## 2026-09-25 multiplayer prediction master switch

- Added persisted `UseMultiplayerPrediction`; default is `false`, preserving local-single-core.
- General settings exposes one experimental toggle. Off: real multiplayer root + local single-player quality ordering. On: Team Objective + teammate forecast + Scenario/Robust + Carry ranking.
- Multiplayer objective selection is disabled in the UI while the master switch is off.
- E0 pinned coverage uses the same detached two-player root for both switch states; U2 static contracts verify the production gate.

## 2026-09-25 upstream backport

- Backported upstream PR #127 prediction semantics: Power/relic-driven power applications explicitly use a null card source, and removed creatures reject later predicted Power application.
- Adapted the Hand Drill hook to the pinned 0.107.1 path in `AfterDamageGivenMirrors`; the newer-version hook path is kept aligned as well.
- Backported upstream PR #134's bounded fresh-resource stand-pat lane: only the first 64 candidates in existing beam order receive the expensive cross-turn roll-out.
- Deliberately did not merge upstream UI, Loadout, ServerGC, or newer-game-version compatibility changes.

## 2026-09-25 upstream hot-path backport

- Backported upstream PR #125 fast lanes without changing search ordering or budgets.
- AfterBlockBroken, AfterCardPlayed, AfterAttack, and AfterModifyingHpLostAfterOsty now use the existing mirrored-hook participation mask instead of scanning listeners that cannot handle the hook.
- The hook enumerator now applies masks independently to segmented run-listener snapshots and has an unsuspended cleanup mode for paired attack state.
- Death lifecycle fingerprinting replaces per-node LINQ OrderBy allocations with stable inline sorting; COMBATSOLVER_VERIFY_FAST_LANES=1 can reconcile both fast lanes against the old behavior.

## 2026-09-25 ModelDb.GetId cache backport

- Backported the isolated route-preserving part of upstream PR #114: ModelDb.GetId(Type) now memoizes the immutable Type-to-ModelId result.
- Null and exception behavior stays on the native path; no ModelDb content or model instance is cached.
- Registered in the normal RitsuLib patch set and the OfflineSearchHarness patch inventory.
- No transposition-table limits, learned portfolio experiments, GC truncation, or other decision-changing PR #114 changes were imported.

## 2026-09-25 Crossbow generation cache backport

- Backported the measured Crossbow-only part of upstream PR #114.
- Crossbow now reuses the existing root-captured character attack pool through GetDistinctUnlockedCharacterAttacksForCombat; RNG selection, card instance creation, add-to-hand, and free-this-turn semantics are unchanged.
- The five turn<=1 relic generation sites remain on their existing code because upstream measured no meaningful search-path benefit there.

## 2026-09-25 multiplayer history-counter owner fix

- PR #118 incremental history counters were already present, but their owner binding still disabled counters whenever Players.Count > 1.
- Multiplayer detached roots now bind the counter owner to the unique RootActionPlayers entry (the local search player); single-player/non-root fallback remains unchanged.
- Shared finished-play count still records every simulated play, while owner-scoped draw/generation/orb/hit counters remain local-player scoped.
- E0's real two-player detached fixture now verifies GetCounters(local) on the root simulator and an ordinary fork.
- U2Runtime now installs ModelDbGetIdCachePatch so pinned 0.107.1 harnesses verify the new Harmony target.

## 2026-09-25 runtime gate reduction

- Multiplayer Safe Execute no longer performs a second one-action solver replay before every native action.
- Per-action continuation/remote semantic fingerprints are no longer authorization gates; action continuation now keeps only native-action capture, queue-idle, advancing WorldVersion, and stable WorldVersion.
- Safe-execution boundary snapshots no longer compute duplicate local/remote fingerprints.
- Multiplayer root capture no longer runs the full post-capture pile/orb/remote-fingerprint verification pass.
- Core search StateFingerprint remains intact for beam deduplication, transpositions, cycle detection, RNG/state identity, and other search semantics.

## 2026-09-25 continuation fingerprint removal

- Removed the dedicated multiplayer continuation remote-state fingerprint from SimulationSnapshot, continuation expectations, validation input, and reuse matching.
- Continuation reuse still requires the existing ContinuationStamp plus combat/local-player identity, multiplayer scaling/card rules, and an advanced WorldVersion.
- Search retention no longer computes a teammate-state fingerprint at every future turn boundary, and terminal continuation building no longer replays solely to recover that fingerprint.
- The carry-ranking fingerprint is intentionally left separate for now; it is not a continuation admission gate.

## 2026-09-25 probe and carry fingerprint cleanup

- Multiplayer probe now computes only the compact fingerprint that actually advances WorldVersion; the extra reactive-public and local-boundary fingerprints and their delta log were removed.
- The compact fingerprint value is no longer printed in the normal OBSERVED log; only the fact that WorldVersion advanced is logged.
- Carry Ranking no longer stores or computes duplicate PublicFingerprint / RemotePublicFingerprint values. It uses its captured immutable player/enemy arrays plus WorldVersion.
- The old MultiplayerContinuationRemoteFingerprint implementation and its probe wrapper were removed after continuation admission stopped consuming them.
- Evidence validators now key off compact WorldVersion advancement rather than the removed remote-public diagnostic hash.

## 2026-09-25 gate collapse

- Safe Execute post-action revalidation now has only five facts: native action captured, native queue idle, WorldVersion advanced, WorldVersion stable, and whether another planned action exists.
- Removed the retired hand-removal, energy/stars, target-identity, enemy-delta, remote-delta, and semantic-replay compatibility facts and their failure diagnostics.
- MultiplayerSafeExecutionBoundary now carries only WorldVersion and observation sequence; it no longer snapshots piles, powers, enemies, teammate state, HP, block, energy, or stars for every played action.
- Safe EndTurn preflight was reduced from ten duplicated gates to four: current combat lifecycle, local playable turn, no pending choice, and stable WorldVersion. Session.TryBeginEndTurn remains the single owner of turn, route-generation, authorization-state, and accepted-WorldVersion checks.
- Removed the retired RemoteOrUnknownChange post-action decision and enemy-token heuristic gate. Pre-action WorldVersion invalidation remains the stale-route boundary.
