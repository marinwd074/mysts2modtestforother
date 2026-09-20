---
codex_task: REACTIVE-CARRY-FOUNDATION
status: DONE
priority: P0
scope: multiplayer-runtime
baseline: 4be1edba56f3e75ce393dcbfc091fb6a12d76f50
requires_game_smoke: true
required_real_smokes: 3
game_gui_owner: user
game_process_owner: codex
---

# Reactive Carry Foundation

<!-- CODEX:CURRENT_GOAL -->

## Current Goal

在已实机通过的 MP-2C bounded N-action 基础上，一次完成：

```text
当前本地回合安全执行
→ Safe EndTurn
→ 销毁旧执行授权
→ 等待真实多人状态推进
→ 下一本地回合 Fresh Probe
→ Fresh Search
→ 继续根据真实局面行动
```

另一名玩家不可控、按自己的主观判断行动。CombatSolver **不控制、不要求配合，也不把其未来动作当确定事实**；只根据已经发生且可观察的真实世界变化持续重新规划本地玩家的最佳回应。

本阶段目标是“Reactive Carry 的生命周期基础”，不是建立完整队友预测模型。

<!-- /CODEX:CURRENT_GOAL -->

## Baseline — Do Not Rediscover

- MP-0 Core / lifecycle：PASS。
- MP-1 Advisor：受控 Smoke PASS。
- MP-2A：一动作 Safe Execute PASS。
- MP-2B：两动作与 remote-interference abort PASS。
- MP-2C：bounded N-action PASS；真实正常运行一次点击执行 3 张本地安全普通牌。
- MP-2C 已证明：远端公开变化发生在当前 deployment 中时，旧路线中止、后续原生动作不再执行并 fresh search。**不要重复这个实机测试。**
- Multiplayer Lab snapshot 已是增量 + 原子 managed overlay。
- 默认多人仍是 Probe；Safe Execute 必须显式 opt-in。
- 未知远端私有状态继续 fail closed。

## Product Intent

最终多人体验：

```text
队友按自己的判断行动
↓
真实公共世界改变
↓
CombatSolver 立即把旧计划视为过期
↓
只基于最新可证明状态重新搜索我方行动
↓
尽量改善战斗结果
```

实现方式由 Codex 根据当前架构自行决定；不要为了符合某个预设算法而复制第二套 Runtime/Search/Engine。

## Required Runtime Properties

### Safe EndTurn

自动 EndTurn 只能在当前本地 deployment 已完成且边界仍有效时发生。

EndTurn 前至少重新确认：

- combat/lifecycle 仍一致；
- 仍是允许结束的本地回合；
- 当前 route / generation / authorization 没有过期；
- 没有正在等待原生动作或不稳定 world；
- 没有已观察到但尚未纳入搜索的远端变化；
- EndTurn 是当前已接受路线的真实结束边界，而不是“动作额度用完”。

任何不确定情况都停止并 fresh search，不猜。

### Turn Boundary

一旦真实 EndTurn 被接受：

```text
旧 SafeExecutionSession
旧 deployment authorization
旧 route/current generation
旧跨动作 continuation
```

都不能授权下一本地回合的动作。

下一本地回合必须从真实状态重新：

```text
Probe
→ capture
→ search
→ authorize
```

WorldVersion 保持单调，不 reset/rebase 来伪造连续性。

### Reactive Replan

队友的未来行为不是确定输入。

- 不要求预测队友下一张牌。
- 不要求队友使用 CombatSolver。
- 不读取不可获得的远端私有手牌/药水来伪造完备信息。
- 已发生的公开变化必须使依赖旧世界的路线失效。
- 若公开变化使旧目标死亡、HP/Block/Power 改变或回合边界推进，下一次本地行动只能来自 fresh search。
- 允许以后加入 uncertainty-aware ranking / teammate model，但不是本阶段 PASS 的前提。

## Safety Boundaries

本阶段不要顺手开放：

- Potion
- Choice
- Replay
- remote/teammate control
- 自定义 CombatSolver 网络协议
- Instant 专用旁路
- 跨回合旧路线复用
- 未经证据的默认 Full Auto

Safe EndTurn 必须走现有真实/原生动作路径，不建立测试专用生产后门。

<!-- CODEX:ENTRYPOINTS -->

## Start Here

优先定向定位：

1. SafeExecutionSession / deployment lifecycle
2. 当前 multiplayer action classifier / policy
3. EndTurn 原生部署入口
4. MultiplayerWorldTracker / WorldVersion
5. search generation / stale-result invalidation
6. turn/lifecycle callbacks
7. `tools/MultiplayerSafeExecuteChecks`
8. Multiplayer Lab validators / RUNBOOK

只在需要时扩大读取范围；不要重新扫描历史 MP-0/MP-1 过程文档。

<!-- /CODEX:ENTRYPOINTS -->

## Automated Contract Target

至少覆盖以下 9 个逻辑场景；可以合并成更少 fixture，只要语义都被证明：

1. Safe EndTurn 只能消费一次授权。
2. EndTurn 前 world/lifecycle 变化会取消 EndTurn。
3. EndTurn 后旧 SafeExecutionSession 不可复用。
4. EndTurn 后旧 route / generation / authorization 不可继续部署。
5. 下一本地回合必须产生 Fresh Probe/capture 边界。
6. 下一本地回合必须产生 Fresh Search，而不是恢复旧路线。
7. 远端公开变化使依赖旧世界的搜索结果 stale。
8. 原目标被队友改变/击杀后，旧目标动作不可继续。
9. 连续多次远端变化不会让旧 deployment 重新获得授权。

测试数量不是产品目标；不要为“正好 9 项”制造重复 fixture。

<!-- CODEX:REAL_GAME -->

# Required Real Host/Client Smoke — Exactly 3 Representative Runs

这些是本阶段**必须实机**的证据。技术流程全部由 Codex/Agent 驱动：

```text
Codex:
build
→ prepare/reuse instances
→ start Host/Client
→ warm-up/restart when required
→ bring game to GUI step
→ ask user for one current click/observation
→ read logs
→ run validators
→ continue or fix
→ Graceful stop
→ summarize evidence
```

用户只操作游戏 GUI。

## Smoke A — Safe EndTurn + Next Local Turn

目标：

```text
本地安全牌序列
→ 原生 Safe EndTurn
→ 旧 session/auth 清除
→ 多人世界推进
→ 下一本地回合
→ Fresh Probe
→ Fresh Search
```

验收：

- EndTurn 只发生一次；
- EndTurn 前最后一次 revalidation 成功；
- 不复用旧 route/action authorization；
- 下一本地回合存在新的 world/search generation；
- 下一本地动作来自新的搜索。

## Smoke B — Teammate Changes the Intervening World

在我方 EndTurn 后、下一本地决策前，让另一 Client 按自己的判断制造明显公开变化，例如：

- 改变敌人 HP；
- 击杀原目标；
- 改变公开 Power/Block；
- 选择不同攻击目标。

不要求固定是哪一种，选最容易稳定制造证据的场景。

验收：

```text
remote public change
→ old plan cannot authorize local action
→ fresh world observed
→ fresh search
→ new local route reflects current world
```

这不是重复 MP-2C 的“当前 deployment 中途 abort”测试；重点是 **跨 EndTurn / turn boundary 后的真实适应**。

## Smoke C — 3 Local Turns Reactive Carry

至少连续 3 个本地回合。

另一名玩家按自己的主观判断正常玩，不需要配合预设路线。CombatSolver 负责本地玩家。

验收：

- 每个本地回合都从当前真实世界得到可追踪的新搜索；
- 可安全时执行本地路线并 EndTurn；
- 任意已观察到的队友变化不会继续消费旧授权；
- 不出现 stale route deployment；
- 不控制队友；
- 不发送新增自定义网络包；
- 连续 3 个本地回合生命周期无卡死/重复 EndTurn/旧 session 复活。

3 回合足够证明机制，不为“更多回合”机械延长 Smoke。

<!-- /CODEX:REAL_GAME -->

## 2026-09-20 实机结果

三轮代表性 Host/Client Smoke 已由 Codex 驱动进程、warm-up、Graceful stop、journal
定位和 validator；用户只完成 Host/Join/Ready、战斗操作和观察 Client 的 GUI 行为。

- Smoke A：`MULTIPLAYER_REACTIVE_CARRY_A_PASS`。`request_id=1` 在本地回合 1 完成
  3 张安全牌，经 Safe EndTurn revalidation 后捕获原生 `EndPlayerTurnAction`，清除
  session/authorization，并在下一回合完成 Fresh Probe + Fresh Search。
- Smoke B：`MULTIPLAYER_REACTIVE_CARRY_B_PASS`。干净的 `request_id=2` 完成 2 张牌和
  原生 EndTurn；EndTurn 后、下一次 fresh search 前观察到队友公开世界变化，随后以新
  回合/新搜索继续。日志中的早期干扰尝试未纳入该 request 的验证范围。
- Smoke C：`MULTIPLAYER_REACTIVE_CARRY_C_PASS`。`request_id=1/2/3` 对应本地
  `turn=1/2/3`，连续完成 `3/3/2` 张牌、三次原生 EndTurn 和三次 fresh search；无
  `MP2B_REMOTE_DELTA_ABORT`、旧授权复用或自定义网络路径。

机器可读摘要见
[`evidence/reactive-carry-smoke-2026-09-20.json`](evidence/reactive-carry-smoke-2026-09-20.json)。
日志和 validator 输出继续隔离在 `.local/multiplayer-lab/`，不复制进正式游戏目录。

## Validation / Evidence

Codex 自行选择最小充分的 validator 结构。

推荐一个通用 turn-boundary/reactive validator，而不是为每个动作数复制脚本。证据至少能关联：

```text
request/search generation
WorldVersion
local turn identity
deployment/session identity
native EndTurn
session cleared
remote public delta
fresh search
next local deployment
```

现有 MP-2C 远端中止证据可以作为历史前提引用，不重复制造。

<!-- CODEX:DONE_WHEN -->

## Done When

- [x] Snapshot 专用 selftest 已在标准 contract suite 中持续运行。
- [x] Safe EndTurn 只有在最新可证明边界上执行。
- [x] EndTurn 后旧 session / route / authorization 不可跨回合复用。
- [x] 下一本地回合必经 Fresh Probe + Fresh Search。
- [x] 远端已发生的公开变化会使相关旧结果失效。
- [x] 自动合同覆盖 turn boundary / stale authorization / reactive replan。
- [x] Release / relevant static gates PASS。
- [x] Smoke A PASS。
- [x] Smoke B PASS。
- [x] Smoke C PASS（至少 3 个本地回合）。
- [x] 三轮实机均由 Codex 驱动进程、日志和 validator，用户只做 GUI。
- [x] 默认多人能力没有静默扩大到未验证的 Potion/Choice/Replay/teammate control/Instant。

<!-- /CODEX:DONE_WHEN -->

<!-- CODEX:STOP_CONDITIONS -->

## Stop Conditions

遇到以下情况应停止扩大能力并报告根因：

- 必须猜测不可获得的远端私有状态才能安全 EndTurn；
- 必须 reset/rebase WorldVersion 才能跨回合继续；
- 必须复用上一回合 authorization 才能工作；
- 必须控制队友或新增自定义网络协议；
- 必须降低现有 MP-2C 远端变化 fail-closed 边界。

<!-- /CODEX:STOP_CONDITIONS -->

## After This

本阶段通过后，再评估是否需要：

```text
uncertainty-aware ranking
→ lightweight teammate behavior model
→ longer autonomous carry
```

不要在本阶段提前实现，除非它们是修复已观察正确性问题所必需。
