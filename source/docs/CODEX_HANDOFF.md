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
- **H 质量与响应验收：H0/H1 已完成，当前进入 H2。** 固定输入历史 A/B 已证明质量/路线/固定工作不回归；剩余门槛是真实 Host/Client 连续回合的高血复用 → 斩杀窗口重算。

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

### H1 已完成：固定输入质量 / 固定工作 A/B

历史基线固定为 `d745b0018e27f2013f7df580bde99523f1ec12ed`，current 为
`1d0860ff3161172da006567c689b002deb368270`。GitHub Actions H1 run
`36685079529` 在同一 pinned STS2 / RitsuLib 0.107.1、DOP=1、相同 Beam/节点/时间预算下，
对 simple / draw_energy / teammate 各取得 4 个 clean-process 样本并按 ABBA 交错。

三组均为：

- 质量完全相同，完整动作路线相同。
- expanded nodes / transitions 完全相同。
- 无 TimeLimit 样本。
- final publication 中位数变化分别为 simple `+0.368%`、draw_energy `+0.559%`、
  teammate `+1.736%`，均低于现行固定工作 `2%` 回归线。
- simple：4 战损、0 药、T3 胜利，1963 nodes / 5165 transitions。
- draw_energy：6 战损、0 药、T2 胜利，823 / 2642。
- teammate：4 战损、0 药、TurnLimit，391 / 1026；这是 detached 双玩家固定根，不替代网络实机。

因此 H1 结论为 **PASS**：当前 Rolling Horizon 主线没有在这三个固定输入上降低路线质量或增加搜索工作，
也没有超过固定工作性能回归门槛。它**不证明**真实多人跨回合响应已经改善。

### 当前任务：H2 真实多人连续回合验收

H2 只验证默认 `MultiplayerSinglePlayerCore`，不要求开启 `UseMultiplayerPrediction`，也不依赖 F1/G。

需要由 current build 的真实 Host/Client journal / 问题包覆盖同一场或可比场景中的两个边界：

1. **高血 / 非斩杀窗口**：队友只降低仍存活敌人的 HP 时，
   `in_lethal_window=false`、`enemy_hp_route_changed=false`；旧 continuation/路线应继续成立或走轻量恢复，
   不应仅因为这次普通伤害产生 cold full search。
2. **进入斩杀窗口或真实语义变化**：`in_lethal_window=true`、目标死亡、阶段转换、Block/意图等关键字段变化时，
   必须停止宽松 HP 复用，从最新 live root 重新定根/搜索；旧目标和旧部署授权不能继续使用。

同包同时统计：首个可采用当前回合结果、最终结果时间，exact continuation / R1 / cold-search 次数，
同回合重复重算次数，最终战损与 `combat_ended_turn`。小样本只报告原始值与中位数，不声称可靠 p95。

H2 通过后即可关闭 Rolling Horizon A→H 主计划；F0 继续保持 `behavioral_reuse=false`，
F1 与 G 作为可选研究项，不阻塞默认 local-core 收尾。

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
