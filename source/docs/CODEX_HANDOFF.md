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
- **E 前台/后台分离：进行中；E1 已完成，进入 E2 前台稳定替换。**
- **F 情景预热：未开始。**
- **G SSD 冷存储：未开始。**
- **H 质量与响应验收：未开始。**

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

实机验证 E4-A：高质量前台形成后限制 supplemental/potion 长尾，但不缩主 Beam。

TEST_SUBJECT_BOSS `19137cca8baa4f26bece04828153965e` 来自 `0.40.2+e4aef0e07d82a39a8c404f30e21dd66d8a1cba60`，已闭合 E3 completion guard 第三版：真实出现 `SEARCH_E3_COMPLETION_FOREGROUND_GUARD source=current_turn_seed scope=CurrentTurnAdoption`，更差自然 final 不再覆盖已批准当前回合前台。实验体 260→520→780 HP 阶段转换也未复现旧卡死/跨阶段硬复用。

E3 多包证据同时表明：主 potion_disabled 的有效改善常在 25–40 秒才出现，不能砍主搜索；但最后一次 foreground 改善后 supplemental potion 长尾可继续 30–80+ 秒而不改善最终质量。E4-A 因而只收紧第一轮 MultiplayerSinglePlayerCore 的 supplemental 总墙钟：

1. 主 Beam / Novelty / potion_disabled、BeamWidth、节点、并发与质量排序完全不变。
2. 只有已经存在正式批准且存活的 foreground 时才启用。
3. supplemental 至少获得 20 秒；若 foreground 刚改善，则至少保留到“最后一次改善后 30 秒”。
4. deadline 仍受原请求总预算上限约束；不会延长原预算。
5. 单人模式、没有批准前台、用户显式 Force potion、以及 NO_VICTORY_ESCALATION 全部保持原预算。
6. completion guard 继续兜底；E4 不引入新的路线质量比较。
7. 新日志 `SEARCH_E4_SUPPLEMENTAL_BUDGET` 记录 start/last improvement/original deadline/E4 deadline。

下一份长 Elite/Boss 包重点验证：主 potion_disabled 的最后有效 replace 时间是否不变；supplemental 是否在 E4 deadline 附近结束；最终/foreground 质量不得比 E3 基线回退。若出现被截断后明显更差的合法 potion 路线，撤回/放宽 E4 deadline，不调整质量排序。

## 当前未验证边界

- E1 已通过实机验证。E2 的 keep/replace 诊断与 pinned 合同已加入，仍需一份 current HEAD 实机日志证明真实后台候选按生产质量排序稳定替换/保留。
- U5/U6 历史 Host/Client observation → fresh replan 的部分真实多人边界仍不是 pinned replay 可替代的证据。
- GitHub Issue #8：多人 Safe Execute 的 Headbutt / turn-start Choice 仍需 current HEAD Host/Client 复验。
- GitHub Issue #9：Vicious 战略估值修复仍需 comparable current multiplayer root 复验。
- `fix/test-subject-phase-transition-guard` 仍有未并入 `main` 的独立 ContinuationLifecycleGuard 修复；在确认被现有逻辑替代或合入前不删除该分支。

## 仓库维护边界

- 当前工作树只保留生产源码、长期规范、可重跑测试/fixture、当前计划和单一 handoff。
- 日志、问题包、benchmark 结果、一次性审计和阶段流水账不进入当前树。
- 历史事实优先从 Git history 或外部问题包定向恢复。
- 分支清理只删除已并入或明确被新实现取代的历史分支；有独立未审修复的分支保留。
