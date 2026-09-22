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
- Multiplayer MP-0 / Advisor / Safe Execute MP-2A/B/C / Reactive Carry 已有真实 Host/Client 基线。
- Multiplayer Safe Auto 已完成真实 3 个本地回合 Smoke：每回合 fresh request/search、原生 PlayCardAction + EndPlayerTurnAction，无旧授权跨回合复用。
- Carry Ranking R1 已有 runtime 证据；R2 decisive runtime 仍 `UNVERIFIED`，不是当前 blocker。
- 旧的 CombatSolver 单 Client Console Fixture 整组已删除。
- 完整 `TheBookOfAges / GM Console` 作为 **test-only submodule** 保留在：
  `source/tools/multiplayer-lab/MultiplayerTestTools/TheBookOfAges`
  固定上游 commit `234a74ccbaf46d7e385ed318c64857f1f7a90cae`。它不进入 CombatSolver 正式构建/发布。

## 当前未完成

1. 验证 Host + 各 Client 使用同一 GM Console 构建时，最小加牌/资源修改是否仍出现 game-data mismatch。
2. 用同步稳定的测试工具获得 MultiplayerOnly / Tag Team 真实语义证据。
3. 只有 Tag Team 原生语义和安全边界都通过后，才设计 **TAG_TEAM-only** Safe Execute whitelist；其他 MultiplayerOnly 牌继续 fail closed。
4. Boot Up Strength 精确数值 differential 仍可补，但不阻塞当前多人阶段。

## 当前开发 / 性能规则

- 默认上下文只读本 handoff + 任务直接相关文件；不要批量加载 dated performance/audit/strategy 历史。
- v0.15 起，上游 release notes 与一次性 performance/strategy JSON/patch 不再保留在当前树；追溯旧结果使用 Git history。
- 主项目 Release 已排除 `src/Testing/**`；`tools/**` 和 test-only GM Console 不属于正式程序集。
- 问题包的 Godot 游戏日志只在真正导出问题包时同步，不再在 Mod 初始化时复制。
- 不通过增加搜索时间、Beam、内存或 GC 预算掩盖正确性问题。

## 验证入口

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\verify-target-version.ps1
pwsh -NoLogo -NoProfile -File .\source\tools\verify-refactor-boundaries.ps1
pwsh -NoLogo -NoProfile -File .\source\tools\run-contract-tests.ps1
~~~

真实多人运行前再读 `source/docs/multiplayer/RUNBOOK.md`。
