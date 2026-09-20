# Multiplayer Lab 实机运行手册

本文件记录已经在真实 Host/Client 流程中确认、且新对话不应重新试错的操作事实。
任何 Multiplayer Lab 运行前先读本页。

## 职责分工

- **Codex/Agent 负责游戏进程与技术流程**：构建代码、准备/刷新隔离实例、执行 `start-host.ps1` / `start-client.ps1`、完成 Mod warm-up 后的必要重启、按需要执行 Graceful stop、定位日志、运行 validator、分析证据和修复代码。
- **用户只负责游戏窗口内的人工操作与观察**：Host/Join、角色选择、Ready、进入战斗、点击“执行本回合”、操作第二个 Client 制造干扰，以及确认卡牌/能量/格挡/UI 等实际现象。
- Codex/Agent **不得代替用户点击或操作游戏 GUI**；当流程推进到需要人工交互时，应停止自动操作并明确告诉用户当前要点击什么、预期看到什么。
- 用户完成该 GUI 步骤并反馈后，Codex/Agent 再继续进程管理、日志读取、验证或下一轮启动。

## 固定前置条件

1. **同一台电脑上的 Lab 默认关闭 Steam transport。**
   - `start-host.ps1` / `start-client.ps1` 现在默认等价于 `-ForceSteamOff`，内部传入 `--force-steam=off`。
   - 命令仍建议显式写 `-ForceSteamOff`，便于日志和人工复核。
   - 只有专门验证 Steam transport 时才使用 `-AllowSteam`；普通 FastMP / Lab 不要开 Steam。

2. **Modded Client 第一次启动不是测试运行。**
   - `ClientRitsuOnly` / `ClientCombatSolver` 第一次启动会先加载 Mod，并需要重启一次游戏。
   - 完成 Mod 加载和这次重启后，再启动同一个 Client instance；**第二次启动才用于 Host/Join/Ready/Smoke**。
   - 第一次 warm-up 的日志不能作为 multiplayer runtime evidence。
   - 重新准备/重建 Client snapshot、替换 Mod payload，或游戏再次提示需要重启时，重新执行 warm-up。

3. **ClientId 必须唯一。**
   - 单 Client 通常用 `1000`。
   - 同一 Host 上第二个本地 Client 使用其他非零值，例如 `1001`。

4. **保持实例隔离。**
   - Host 使用 `HostVanilla`。
   - Solver Client 使用 `ClientCombatSolver`。
   - 不混用 Host/Client 的 `APPDATA`、`LOCALAPPDATA`、logs 或 instance root。

5. **正式证据只取重启后的运行。**
   - 记录第二次 Client 启动返回的 `logPath`。
    - MP-2A 历史基线只在这次运行里点击一次“执行本回合”；当前 MP-2C Smoke 使用
      显式 `-MultiplayerMode safe-execute`，Lab 证据 Smoke 仍使用 `safe-execute-lab`。
   - 等待动作完成、WorldVersion 更新和新 debounce search 后再停止。

6. **停止默认 Graceful。**
   - 使用 `stop-owned-instances.ps1` 默认模式。
   - `-Mode Force` 只用于清理卡死实例；强杀可能截断 CombatSolver journal，不能把该运行当完整证据。

## 实例 snapshot 增量同步

`prepare-instances.ps1` 的 game root 使用 schema 2：底座文件保留为持久
base-game snapshot，RitsuLib 与 CombatSolver 作为 profile overlay 增量同步。
游戏版本/底座变化、底座完整性失败或旧 schema 才会触发 staging 全量重建；CombatSolver
构建变化只更新 CombatSolver payload，RitsuLib 变化只更新 Ritsu payload，HostVanilla
不会因为 CombatSolver 构建变化重建。输出中的 `syncMode` 会标明
`full-rebuild`、`overlay-incremental` 或 `unchanged`；`snapshotAction` 会标明
`FULL_REBUILD`、`OVERLAY_UPDATED` 或 `REUSED`，并同时给出 `baseGameAction`、
`ritsuAction`、`combatSolverAction`、三个独立 identity 和 `copiedFiles`。

RitsuLib 与 CombatSolver overlay 都先在临时 managed tree 中完成 SHA-256 校验，再执行
目录级 rename；目标进程仍运行时不会进入替换阶段，失败会保留/恢复旧 managed tree。

仍须使用现有 `-ForceRebuild` 处理显式全量重建或 profile 切换。增量同步不改变既有安全
合同：只操作带 ownership marker 的 D: 私有 root，拒绝 reparse point，运行中的目标游戏
禁止覆盖，正式证据继续落在隔离的 diagnostics/results 路径而不是 game snapshot。

## 推荐启动顺序

> 以下 Host/Client 启动、Mod warm-up 重启和 Graceful stop 均由 **Codex/Agent 执行**；只有进入游戏窗口后的点击交给用户。

~~~powershell
$labRoot = 'D:\yingye\CombatSolver\.local\multiplayer-lab'

pwsh -NoLogo -NoProfile -File .\start-host.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-host" `
  -ForceSteamOff

# Client 第一次：只做 Mod 加载 warm-up
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff
~~~

让 Client 完成 Mod 加载并重启。不要把这次当正式测试。

~~~powershell
# Client 第二次：正式运行
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute-lab
~~~

然后再进行 Host/Join/Ready 和对应 Smoke。

## MP-2 Safe Execute 正式 token Smoke

正式 token 只接受明确的 `COMBATSOLVER_MULTIPLAYER_MODE=safe-execute` opt-in，
默认多人仍保持 Probe。为保持实例隔离，第一轮正式 token Smoke 仍使用本目录
准备的 `HostVanilla + ClientCombatSolver`，但 Client 第二次启动改为：

~~~powershell
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute
~~~

用户仍只做一次 Host/Join/Ready、进入战斗、确认未自动出牌后点击“执行本回合”。这段
只用于复核 MP-2A 历史一动作基线；验证器会检查 `FORMAL_CAPABILITY`、单个原生
`PlayCardAction`、动作后 `WorldVersion` 失效和新的 debounce search。它不代表当前
 MP-2C N-action 实机已通过。

## MP-2B 两动作 Smoke（已完成实机；复验步骤）

正式 Client 第二次启动继续使用上面的 `HostVanilla + ClientCombatSolver`、Steam
transport off 和 Mod warm-up 后的隔离实例，但模式必须是：

~~~powershell
# 用户执行
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute
~~~

用户手动完成 Host/Join/Ready 并进入本地玩家回合；确认点击前没有自动出牌后，只点击
一次“执行本回合”。MP2B 一次 deployment 最多执行两张通过安全分类的本地普通牌，
每张都必须通过原生 `PlayCardAction`。观察费用/能量减少、手牌减少和动作完成后的
稳定世界；不会自动 EndTurn，完成后 UI 应回到“等待下一回合”或可重新计算的安全边界。
保留 Client 日志直到动作后的新 debounce search 出现，再用默认 `Graceful` 停止。

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -OutputPath '.\.local\multiplayer-lab\results\mp2b-summary.json'
~~~

正常两动作 Smoke 只有在验证器返回 `MULTIPLAYER_MP-2B_PASS` 且人工确认 Host/Client
身份与 UI 行为后才能记为 PASS。另做一次远端干扰 Smoke：第一张牌完成、第二张牌尚未
执行时，由另一 Client 进行一次公开动作；预期当前 Client 记录
`MP2B_REMOTE_DELTA_ABORT`，不再捕获第二个原生动作，并启动新的搜索。该场景只证明
安全中止与重搜，不计入正常两动作 PASS。远端干扰日志使用独立验证器：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-interference-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -OutputPath '.\.local\multiplayer-lab\results\mp2b-interference-summary.json'
~~~

只有返回 `MULTIPLAYER_MP-2B_REMOTE_ABORT_PASS` 才能记为安全中止证据；正常两动作
验证器不能替代这个场景。

2026-09-20 实机结果：正常两动作验证器返回 `MULTIPLAYER_MP-2B_PASS`，同一 deployment
完成 `DEFEND_IRONCLAD`、`STRIKE_IRONCLAD` 两张本地普通牌；远端干扰验证器对真实
`request_id=1` 返回 `MULTIPLAYER_MP-2B_REMOTE_ABORT_PASS`，第一张牌完成后捕获到
远端公开变化，未捕获第二张原生牌，并启动新的搜索。摘要见
[`evidence/mp2b-smoke-2026-09-20.json`](evidence/mp2b-smoke-2026-09-20.json)。同一日志若包含
多次用户尝试，干扰验证器可用 `-RequestId <deployment-request-id>` 选择一个完整 session。

## MP-2C 当前回合 bounded N-action Smoke

MP-2C 直接复用同一 SafeExecutionSession，把 MP-2B 的固定两动作改为有限上限
`MaxActionsPerDeployment=6`。正常 Smoke 必须选择至少三张连续安全本地普通牌；用户只点击
一次“执行本回合”，等待每张牌完成、费用/能量与手牌实际变化、UI 显示“正在执行 n/6”，
最后显示保持当前回合并重新计算最新路线。它不会自动 EndTurn。

正常日志验证：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -MinActions 3 `
  -MaxActions 6 `
  -OutputPath '.\.local\multiplayer-lab\results\mp2c-summary.json'
~~~

必须看到同一 `request_id` 的连续 `action_index=0..N-1`，每张均为原生
`PlayCardAction`，每张牌后有重验证且 `WorldVersion` 单调前进，最终 `end_turn=false`，
无 Potion/Choice/Replay/Remote Target/EndTurn，部署后有 fresh search。验证器应返回
`MULTIPLAYER_MP-2C_PASS`。

干扰 Smoke 选择至少三张安全牌：前两张成功后，在下一张牌开始前由另一 Client 制造公开变化。
预期当前 Client 记录 `MP2B_REMOTE_DELTA_ABORT`，完成动作数至少为 2，不出现下一个
`NATIVE_ACTION_CAPTURED`，并在中止后重新搜索：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-interference-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -RequestId '<deployment-request-id>' `
  -MinCompletedActions 2 `
  -MaxActions 6 `
  -OutputPath '.\.local\multiplayer-lab\results\mp2c-interference-summary.json'
~~~

验证器应返回 `MULTIPLAYER_MP-2C-remote-interference_PASS`。游戏内所有点击仍由用户完成；
Codex 只负责启动/停止进程、读取 journal 和运行验证器。2026-09-20 实机结果：正常运行
由一次点击自动完成 3 张本地普通牌，牌后手牌/能量分别从 `5/3` 变为 `4/2`、`3/1`、
`2/0`，返回 `MULTIPLAYER_MP-2C_PASS`；干扰运行在同一 `request_id=1` 完成前两张后，
用户通过另一 Client 打出公开牌，主 Client 返回 `MP2B_REMOTE_DELTA_ABORT`，未捕获第 3 张
原生动作并启动 fresh search，返回 `MULTIPLAYER_MP-2C-remote-interference_PASS`。摘要见
[`evidence/mp2c-smoke-2026-09-20.json`](evidence/mp2c-smoke-2026-09-20.json)。

## MP-2A 收尾

游戏进程停止仍由 Codex/Agent 负责，默认使用 Graceful；只有游戏窗口中的点击交给用户。

~~~powershell
pwsh -NoLogo -NoProfile -File .\stop-owned-instances.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver"
~~~

随后只验证**第二次正式 Client 运行**的日志：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2a-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -OutputPath '.\.local\multiplayer-lab\results\mp2a-summary.json'
~~~

默认安装仍不因本手册自动进入 Safe Execute；Multiplayer Instant、Potion、Choice、
自动 EndTurn、Full Auto、跨回合和队友目标也保持关闭。MP2B 当前已在上述受控范围内
通过；后续扩大能力边界仍需独立计划和独立实机证据。
