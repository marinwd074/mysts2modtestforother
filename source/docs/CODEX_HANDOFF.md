# Codex 当前交接

> 只记录当前状态。历史批次、旧提交和旧测试流水账从 Git history 查，不在本文件累积。

## 基线

- CombatSolver: `0.40.2`
- STS2 / RitsuLib: pinned `0.107.1`
- .NET 9 / Godot 4.5.1 / `STS2_01071`
- 正确性优先；未知多人语义 fail closed。
- `RootActionPlayers` 只允许本地玩家；不生成或执行队友动作。

## 当前已确认

- 单人 0.107.1 卡牌/怪物兼容审计已完成主要收口；Axebot `AXEBOTS_NORMAL` 旧 `RespawnCount` 崩溃已由用户实机确认解决。
- pinned monster target fanout：63 个可确定 move 已建模；Knowledge Demon 的远端 Choice 继续 fail closed。
- v0.14 已把多人怪物目标 dispatcher 从 64KB 主文件拆到独立 `MonsterMoveEffects.MultiplayerTargets.cs`；普通效果统一 per-player，9 个 mixed move 已明确拆成 target-effect × players + owner-effect × 1，2 个 RNG 特例保持显式实现。已有怪物 HP 直接使用 root snapshot，不做二次人数缩放。
- v0.16 将 63 项多人怪物目标分类合并为一次 target-mode switch，避免每个模拟 Move 依次经过 3 套字符串分类器；支持范围不变。
- Multiplayer MP-0 / Advisor / Safe Execute MP-2A/B/C / Reactive Carry 已有真实 Host/Client 基线。
- Multiplayer Safe Auto 已完成真实 3 个本地回合 Smoke：每回合 fresh request/search、原生 PlayCardAction + EndPlayerTurnAction，无旧授权跨回合复用。
- Carry Ranking R1 已有 runtime 证据；R2 decisive runtime 仍 `UNVERIFIED`，不是当前 blocker。
- 旧的 CombatSolver 单 Client Console Fixture 整组已删除。
- 完整 `TheBookOfAges / GM Console` 作为 **test-only submodule** 保留在：
  `source/tools/multiplayer-lab/MultiplayerTestTools/TheBookOfAges`
  固定上游 commit `234a74ccbaf46d7e385ed318c64857f1f7a90cae`。它不进入 CombatSolver 正式构建/发布。
- 2026-09-22 同构 GM Console 实机 Smoke 已通过：Host/Client 加载同一 DLL/PCK 与 BaseLib 构建，进入同一战斗且没有 game-data mismatch；Host 和 Client 各发起一次 `energy 1` 并在两端执行，Host 发放并实际打出原生 `CARD.TANK`，owner 为 Host player 1、无目标，动作在两端结算并生成 checksum。摘要见 `docs/multiplayer/evidence/gm-console-multiplayer-smoke-2026-09-22.json`。Client 反向打出本轮按用户要求未执行。
- MultiplayerOnly 卡统一为**搜索/推荐可见、玩家手动出牌**：Safe Execute 不自动打任何 MultiplayerOnly 卡，包括 Lift；路线遇到多人牌时停止自动前缀但保留 Safe Auto，等待玩家使用游戏原生目标选择手动出牌，状态变化后立即 Fresh Search。删除/停止继续发展 LIFT 等多人牌的跨玩家自动执行白名单。
- `AnyAlly` 空目标问题已从目标生成层修正，并补齐相同根因的 `AnyPlayer` 分支；卡牌/药水的玩家目标枚举使用预测态目标解析，不靠部署期判空或异常兜底。`RootActionPlayers` 仍只包含本地玩家。
- 单人搜索算法向多人本地跨回合模式的第一批迁移已落地：`SinglePlayerFullRoute` 与 `MultiplayerLocalCrossTurn` 现在共用 full-search heuristics，因此 Novelty Portfolio、成长预算、遗物目标、成长机会目标和长期收益评估不再因多人能力表中的 `CanCrossTurnSearch=false` 被关闭；`MultiplayerCurrentTurnOnly` 仍保持精简。执行权限、队友动作、共享 Shuffle RNG 边界和多人牌手动出牌规则均未放宽。
- 问题包 `25b905c1322b41e6b9a8e10baeae5606` 复现 0 费 Anger 被遗漏：T2 手牌含 `ANGER(0)`，Solver 选择 Tremble→Dismantle→Strike→EndTurn，并在 Shuffle 边界形成 `PartialLocalCrossTurnProjection`。已修正多人未完成路线的最终排序：确定的 Enemy HP 进展现在先于 Anger copy 长期惩罚；单人和完整胜利路线保持原排序。
- 多人新基线改为“完整联合战斗预测 + 滚动重规划”：旧的 partial-route/Carry 补丁仅作为历史兼容层，不再作为目标架构。第一阶段已把预测根中现有的完整队友状态正式暴露为 `TeammateForecastStates`，包含 Hand/Draw/Discard/Exhaust/Play 的有序语义快照、Energy/Stars、HP/Block、Phase、Turn、Orbs；Root capture 同时逐玩家核对 live 与 detached prediction 的五牌堆顺序、资源和 Orb 状态。执行权限没有变化，`RootActionPlayers` 仍只有本地玩家。
- 多人牌 UI 计划改为**抽牌距离窗口**，不再承担“预测/自动执行多人牌”的含义：只读取本地玩家当前有序 DrawPile，列出 MultiplayerOnly 卡距离牌堆顶的位置。显示规则使用 1-based 抽牌数：牌堆顶的多人牌显示“再抽 1 张”，下一张显示“再抽 2 张”；已在手牌显示“已在手牌”。每次实际抽牌、插牌、洗牌或 Fresh Search 后重新计算；发生洗牌边界或目标牌当前不在 DrawPile 时不伪造精确距离。该窗口不预测队友未来动作，也不授予任何多人牌自动执行权限。
- 新增多人专用“多人路线目标”设置：`MinimizeTeamLoss` 与默认 `AdaptiveLethalTempo`。动态斩杀按当前 root 的敌方总有效耐久判断，≤35% 时启用战损/回合联合排序，每提前 1 回合可抵消 5% 战损比；>35% 时仍按最低战损优先。Snapshot 现在从所有 captured players 的 `GetCumulativeHpLost` 精确计算 `TeamLossRatio`、`WorstPlayerLossRatio`、`AllPlayersAlive`；分母通过 `CombatRootSnapshot.CapturedPlayerMaxHp` 只读取 root 冻结值（本地 `InitialPlayerMaxHp` / 队友 forecast MaxHp），后台搜索不再触碰 live Creature MaxHP。多人完整胜利路线已按这些团队指标排序；动态斩杀也已改用真实 TeamLossRatio。单人排序不读取这些团队键。

## 当前未完成

1. `ShadowTeammatePlanner` 已从单队友 Top-K 扩为 Team Top-K：队友按 NetId 依次在同一预测世界上模拟，每处理完一个队友就按 EnemyDurability / TeamEffectiveHp / WorstPlayerEffectiveHpRatio / TeamEnergy / TeamStars / 动作数重新取 Pareto 前沿并压回全局 beam=4，因此不会形成 K^N 笛卡尔爆炸。强制结束自己出牌的 shadow 卡只结束该队友分支，切换到下一队友前会消费 prediction-only end request；所有候选仍只存在于 simulator fork，不生成 `PlanAction`。每条 Shadow route 现在还携带独立 `ProcessedEnemyDeaths`，每次卡牌分叉复制并更新，避免跨 Shadow 动作丢失敌人死亡生命周期状态；接主搜索时可直接从 parent snapshot 的集合初始化。
2. Joint EndTurn 主接线已落地，并补齐精确回放：每个 Joint EndTurn 都携带非执行的 `ShadowForecastPlan`，记录本次选中世界线的队友动作；即使队友 0-action，非 null metadata 也明确表示 Joint 世界。搜索/最终注释回放会按记录的 PlayerNetId + HandIndex + SemanticKey + TargetCombatId 在 detached simulator 中重放，再走全队 End → Enemy Side → 全队 Start；不重新跑 Top-K 猜一次。Deployment 不读取此字段，真实执行权限仍只有本地 EndTurn/本地牌。
3. Team-Safety 中途保路已接入；Joint continuation 已保存“该预测节点”的队友语义指纹，不再错误复用搜索 root 的旧队友指纹。live/predicted 共用 MultiplayerContinuationRemoteFingerprint，覆盖队友 HP/Block/Gold、Turn/Phase/Energy/Stars、五牌堆语义、Orb、药水、遗物及遗物预测状态；Power 继续由 ContinuationStamp 的全局 Power 校验负责。Root capture 会直接校验 live/predicted 队友指纹一致。下一回合只有真实队友状态与 Shadow 世界一致才可 continuation reuse；任何可读语义偏离仍因 CanSoftReuseRemotePublicDelta=false 强制 Fresh Search。2026-09-22 又修正了一个生命周期问题：历史 EndTurn 节点的 simulator 会在最终 materialization 前主动释放，因此队友 continuation fingerprint 现在与 ContinuationStamp 一样，在节点存活时冻结到 SimulationSnapshot；fallback 则用同一次 replay simulator 同时生成 stamp + remote fingerprint，BuildContinuations 不再读取历史 node.Snapshot.Simulator。新增 MultiplayerContinuationLifecycleChecks，CI 已确认 26 PASS / 0 FAIL。下一步只做本地 Release Build，再做 Joint continuation 的 Reuse + Mismatch 两个最小 runtime smoke；直接按 `docs/multiplayer/NEXT_LOCAL_JOINT_CONTINUATION_SMOKE.md` 执行。

## 当前开发 / 性能规则

- 默认上下文只读本 handoff + 任务直接相关文件；不要批量加载 dated performance/audit/strategy 历史。
- v0.15 起，上游 release notes 与一次性 performance/strategy JSON/patch 不再保留在当前树；追溯旧结果使用 Git history。
- v0.17 又移除 dated performance/strategy/audit/issue/refactoring 历史报告；怪物 root capture 的空 static-int map 改为共享实例，减少无意义分配。
- 主项目 Release 已排除 `src/Testing/**`；`tools/**` 和 test-only GM Console 不属于正式程序集。
- 问题包的 Godot 游戏日志只在真正导出问题包时同步，不再在 Mod 初始化时复制。
- 不通过增加搜索时间、Beam、内存或 GC 预算掩盖正确性问题。
- v0.18 起 `SearchGcPolicy` 的测试暂停/故障注入/计数接口独立到 `SearchGcPolicy.TestHooks.cs`；主文件只维护真实 GC 策略。
- v0.19 起高频 GC 详细追踪默认关闭；`COMBATSOLVER_GC_DIAGNOSTICS=1`、performance recording 或无人测试会重新开启，GC 行为不变。
- v0.20 将 ordered-mutation retention 中 3 处只为选最佳项而产生的 List/排序改为稳定单遍扫描；排名和 tie-break 不变。
- v0.21 继续把 continuation quality leader 与 admission claim coalesce 改为单遍扫描，减少排序、List 和重复枚举；优先级不变。
- v0.22 将 3 处 outcome-group 的 `OrderBy(...).First()` 改为稳定单遍 representative 选择；组间排序不变。
- v0.23 将 semantic companion 最终选择改为分组后单遍扫描，并把 coverage round-robin 的逐轮 `Any` 改为一次最大轮数计算；选择/tie-break/输出顺序不变。

## 验证入口

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\verify-target-version.ps1
pwsh -NoLogo -NoProfile -File .\source\tools\verify-refactor-boundaries.ps1
pwsh -NoLogo -NoProfile -File .\source\tools\run-contract-tests.ps1
~~~

真实多人运行前再读 `source/docs/multiplayer/RUNBOOK.md`。
