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
- **H 质量与响应验收：已进入 H0。** 先冻结基线与验收口径，再做固定输入 A/B，最后做真实 Host/Client 连续回合验收。

`CombatSolver_Quality_First_Next.md` 继续用于坏路线、执行质量和回归定位，但不覆盖 Rolling Horizon 的主阶段顺序。

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

## 下一任务

### 当前任务：H0 质量与响应验收基线

H 的历史对照固定为 `d745b0018e27f2013f7df580bde99523f1ec12ed`：这是
`Rolling_Horizon_Reuse_Architecture.md` 进入仓库前最后一个生产提交，并且已经包含
“非斩杀敌人掉血不触发跨回合重算”。因此 H 不会通过回退这条 local-core 策略制造虚假的复用收益。

H0 只冻结验收合同，不改变搜索、预算、排序、部署或默认设置：

- **固定输入 A/B**：同游戏/RitsuLib 0.107.1、同 encounter/seed/loadout、同搜索预算、Beam、DOP、
  NoGC 与药水策略；历史基线与 current HEAD 分别使用干净进程。性能对照沿用
  `PERFORMANCE_GUARDRAILS.md`，至少三份独立样本并以 ABBA 顺序消除热身偏差。
- **质量优先于速度**：记录 `projected_battle_hp_lost`、是否胜利、`combat_ended_turn`、
  药水/长期资源与完整动作路线。候选不能被历史基线在已知质量轴上支配；若战损相同，
  baseline 的胜利不能退化为未胜利，双方都胜利时不能无理由拖后斩杀回合。
- **固定工作量再谈性能**：同时记录 expanded nodes、transitions、总耗时、分配/GC 与主线程 gap。
  质量保持时，固定工作主指标超过现行 2% 回归线要记为回归或测量不确定，不能靠缩 Beam、
  节点、时间或 DOP 伪装提速。
- **local-core 必验语义**：高血量/非斩杀窗口的队友普通敌人 HP 下降不得制造无意义 cold search；
  进入 `IsInLethalRecalculationWindow`、目标死亡、阶段转换或其他真实语义变化时必须重新定根/
  重算，不能为了提高复用率继续旧路线。
- **响应指标**：首个可采用当前回合结果、最终结果、exact continuation/R1/cold-search 数量、
  同回合重复重算次数，以及连续本地回合的累计等待。最终报告 p50/p95 时必须注明样本数和硬件，
  小样本只报告原始值/中位数，不声称统计显著。
- **实机门槛**：固定输入只能完成 H1；H2 必须由真实 Host/Client journal/问题包验证连续回合。
  合同、headless 和 pinned fixture 不能替代该证据。

H 分三步收尾：

1. **H0（当前）**：冻结上面基线、质量向量、响应指标与 local-core 斩杀窗口合同。
2. **H1**：对 `d745b001` 与 current HEAD 跑固定输入质量/固定工作 A/B；路线不同允许，但必须解释
   质量差异，不能只比较 elapsed。
3. **H2**：真实多人连续回合验证“高血稳定复用 → 斩杀窗口重算”，并汇总首结果等待、重算/复用和
   最终战损/斩杀回合；完成后才关闭 Rolling Horizon 主计划。

F0 保留 `behavioral_reuse=false`；没有实验多人预测 exact-hit 证据前不进入 F1。G 不作为 H 的前置条件。

## 当前未验证边界

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
