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

4. **保持实例隔离，但长期复用同一角色的实例根目录。**
   - Host 使用 `HostVanilla`，默认实例固定为 `mp-host`。
   - Solver Client 使用 `ClientCombatSolver`，默认实例固定为 `mp-client-solver`。
   - Ritsu-only / Vanilla Client 分别固定为 `mp-client-ritsu` / `mp-client-vanilla`。
   - 不混用 Host/Client 的 `APPDATA`、`LOCALAPPDATA`、logs 或 instance root。
   - **不要为了每个 Smoke / 功能名创建新的 RuntimeRoot。** 日志已有独立 runId，证据隔离不依赖新实例。
   - `Roaming` / `Local` 是每个实例的持久用户设置层；窗口模式、音量、键位、`settings.save` 等应跨 game snapshot 重建保留。
   - `-ForceRebuild` 只允许重建 `game` snapshot，不应删除 `Roaming` / `Local`。只有明确需要“全新用户配置”测试时才另建实例或手工清理用户数据。

5. **正式证据只取重启后的运行。**
   - 记录第二次 Client 启动返回的 `logPath`。
    - MP-2A/MP-2C 历史基线按各自入口运行；当前 Reactive Carry Smoke 使用显式
      `-MultiplayerMode safe-execute`。
   - 等待动作完成、WorldVersion 更新和新 debounce search 后再停止。

6. **停止默认 Graceful。**
   - 使用 `stop-owned-instances.ps1` 默认模式。
   - `-Mode Force` 只用于清理卡死实例；强杀可能截断 CombatSolver journal，不能把该运行当完整证据。

## 实例 snapshot 增量同步

实例用户数据和 game snapshot 生命周期分离：`runtime-*\Roaming` 与 `runtime-*\Local`
属于持久层，`runtime-*\game` 属于可重建层。窗口/显示设置位于游戏的 Roaming
用户目录；因此普通 prepare、overlay 更新、base-game rebuild 和 `-ForceRebuild` 都必须
保留同一实例的用户设置。若测试脚本换了新的 RuntimeRoot，则会得到新的 APPDATA，
表现为窗口模式等设置恢复默认，这不应作为常规测试流程。

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

## Shared Console Mod（所有端同装）

为了避免“只有 Solver Client 开控制台”造成联机 Mod/数据不一致，Lab 现在提供
`install-shared-console-mod.ps1`。它不把第三方 Mod 文件提交进仓库；只接受用户本地已有的
JSON-only console enabler，并把**完全相同的文件**复制到所有参与本轮测试的 owned
Host/Client 私有实例，同时校验 SHA-256 一致。

推荐使用轻量的 Dev Console Enabler 一类 Mod，只负责开启游戏自带控制台。先准备实例，再安装：

~~~powershell
$labRoot = 'D:\yingye\CombatSolver\.local\multiplayer-lab'
pwsh -NoLogo -NoProfile -File .\install-shared-console-mod.ps1 `
  -ConsoleModPath 'D:\path\to\DevConsoleEnabler.json' `
  -InstanceRoot @(
    "$labRoot\runtime-mp-host",
    "$labRoot\runtime-mp-client-solver",
    "$labRoot\runtime-mp-client-observer"
  )
~~~

规则：

- Host 和所有参与 Client 必须安装同一文件、同一 SHA-256。
- 运行中的实例禁止修改。
- `prepare-instances.ps1` 重建 game snapshot 后需要重新运行本脚本。
- 该脚本只解决“各端 Mod 集合一致”问题；它**不证明任意 debug command 都是网络安全的**。
- 正式 runtime evidence 仍要单独确认具体命令不会造成 desync。现有单 Client
  Console Fixture 继续视为 diagnostic-only。

## Multiplayer Console Fixture v1 — 非正式联机证据

> **2026-09-22 实机限制：不要再用 Console Fixture 构造正式 Host/Client 证据。**
> 已确认在目标 0.107.1 联机中，启用/执行控制台相关 fixture 会触发“游戏数据不相同”
> 一类联机一致性/不同步提示；提示中出现的 `1000` 很可能是本地 FastMP ClientId/NetId，
> 不是应被解释为某个卡牌/数值数据本身。无论具体内部校验点为何，该运行已经受到
> debug state injection 干扰，因此不得用来证明 MultiplayerOnly、Tag Team、Beacon 等
> 原生多人语义或 Safe Execute runtime 能力。
>
> Console Fixture 仅保留给离线/隔离诊断、命令链路开发和合成 validator。正式多人
> differential 必须从正常游戏状态进入，或只使用不改变游戏状态的 observation patch。


Console Fixture 只用于 owned `ClientCombatSolver` Multiplayer Lab 实例，用游戏自己的
`DevConsole.ProcessCommand()` 执行命令。真实多人中，命令仍由游戏检查 `IsNetworked`，
networked command 会走原生 `ConsoleCmdGameAction` / `ActionQueueSynchronizer`，不新增
CombatSolver 网络协议，也不模拟键盘输入。

v1 白名单：`card`、`power`、`energy`、`block`、`potion`、`draw`、`heal`、`damage`。
`god`、`instant` 等 local-only/debug convenience 命令不允许进入 fixture。Runtime 还会
反射确认目标游戏里的实际 command 存在且 `IsNetworked=true`，否则 fail closed。

先静态验证 fixture：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-console-fixture.ps1 `
  -FixturePath .\fixtures\tag-team-basic.example.json
~~~

以下启动方式仅供**隔离诊断**复现 console command 链路，不属于正式 multiplayer evidence：

~~~powershell
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute-lab `
  -ConsoleFixturePath .\fixtures\tag-team-basic.example.json
~~~

`start-instance.ps1` 会先验证输入，并把 fixture 复制到该 owned instance 的
`console-fixtures\active.json`；Runtime 只接受 instance root 内的路径。普通桌面启动、
`HostVanilla`、`ClientRitsuOnly` 或缺少 probe-evidence ownership marker 的进程都不能
执行 fixture。它不会修改正常 `settings.save`，而是在 Lab 进程内部创建允许 debug
commands 的 `DevConsole`。

若为了诊断仍在 owned Lab 中运行 fixture，它只执行一次；该运行即使连接成功也必须标记为 diagnostic-only。日志应按顺序出现
`FIXTURE_ARMED`、每条命令的 `FIXTURE_COMMAND_START` / `FIXTURE_COMMAND_RESULT`，最后
`FIXTURE_COMPLETE`。运行时链路验证：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-console-fixture-results.ps1 `
  -LogPath '<post-restart-client-log-or-journal>' `
  -FixturePath .\fixtures\tag-team-basic.example.json `
  -OutputPath '.\.local\multiplayer-lab\results\console-fixture-summary.json'
~~~

该 validator 的 PASS **只证明 fixture 被 Lab Runtime 完整调度**。Host/Client 的实际
状态一致性以及 Tag Team/Beacon 等牌的语义仍需各自 differential；不得把 fixture PASS
直接升级成多人牌 runtime PASS。


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
# Codex/Agent 执行
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute
~~~

用户手动完成 Host/Join/Ready 并进入本地玩家回合；确认点击前没有自动出牌后，只点击
一次“执行本回合”。MP2B 历史基线一次 deployment 最多执行两张通过安全分类的本地
普通牌，每张都必须通过原生 `PlayCardAction`；该历史 Smoke 不包含 Reactive Carry
的自动 EndTurn。观察费用/能量减少、手牌减少和动作完成后的稳定世界；保留 Client
日志直到动作后的新 debounce search 出现，再用默认 `Graceful` 停止。

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

MP-2C 历史 bounded N-action Smoke 直接复用同一 SafeExecutionSession，把 MP-2B 的固定
两动作改为有限上限 `MaxActionsPerDeployment=6`。正常 Smoke 必须选择至少三张连续安全
本地普通牌；用户只点击一次“执行本回合”，等待每张牌完成、费用/能量与手牌实际变化、
UI 显示“正在执行 n/6”，最后保持当前回合并重新计算最新路线。该历史运行不自动
EndTurn；跨回合能力见下面的 Reactive Carry。

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

## Reactive Carry Foundation Smoke

Reactive Carry 是当前显式 `safe-execute` 的跨回合边界。一次部署的安全本地牌序列若以
当前路线的 `EndTurn` 结束，Runtime 只在最后一次边界复核成功后通过原生
`EndPlayerTurnAction` 结束回合；随后旧 session、route generation 和 authorization
全部失效。下一本地回合必须重新 Probe、capture、search、authorize，不从上一回合恢复。
Potion、Choice、Replay、队友控制、Instant 和旧跨回合路线复用仍关闭。

本阶段固定做三轮代表性真实 Smoke：

- **A**：本地安全牌序列 → 原生 Safe EndTurn → 多人推进 → 下一回合 Fresh Probe/Search。
- **B**：Safe EndTurn 后、下一本地决策前由观察 Client 做一次普通公开行动；验证公开
  变化被观察且下一决策来自 fresh search。若同一 journal 有多次尝试，使用
  `-RequestId` 只选择一个完整 session。
- **C**：连续 3 个本地回合，每回合一次 Solver“执行本回合”；观察 Client 只在 Solver
  已结束回合后正常行动，不在部署期间制造远端干扰。要求 3 个 distinct request/turn、
  每回合 native EndTurn 和 fresh search，且没有 abort/stale/custom-network marker。

通用验证器：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-reactive-carry-results.ps1 `
  -LogPath '<post-restart-client-combat-journal.jsonl>' `
  -Smoke A -RequestId '<request-id-when-journal-has-extra-attempts>' `
  -OutputPath '.\.local\multiplayer-lab\results\reactive-carry-summary.json'
~~~

Smoke C 不传 `-RequestId`，以便验证整份正式 journal 没有中止或旧授权复用。退出码仍为
0=PASS、1=FAIL、2=UNVERIFIED。2026-09-20 的 A/B/C 均已通过，机器摘要见
[`evidence/reactive-carry-smoke-2026-09-20.json`](evidence/reactive-carry-smoke-2026-09-20.json)。

## Carry v2 / Multiplayer Safe Auto Smoke

Safe Auto 只在显式 `-MultiplayerMode safe-execute` 下可用。进入稳定本地回合后，只点击一次
“安全自动：关”把它切成“安全自动：开”；之后 **不要再点击“执行本回合”**。目标是连续至少
3 个本地回合都由 Runtime 自己完成：fresh/validated search → 新
`MultiplayerSafeExecutionSession` → 安全本地普通牌 → 原生 Safe EndTurn → 下一本地回合重新
Probe/Search/authorize。旧 request/session/route authorization 不得跨回合复用。

运行时日志必须包含每回合 `MP_SAFE_AUTO_ARMED`；第一回合如果复用按钮开启前已经完成的最新
路线，会记录 `source=existing_result`，后续正常搜索完成记录
`source=search_completion`。任何 Potion、Choice、teammate/unknown target、
multiplayer-only card 等当前不支持边界都应停止 Safe Auto，而不是不断重算同一局面。

正式三回合验证命令：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-safe-auto-results.ps1 `
  -LogPath '<post-restart-client-combat-journal.jsonl>' `
  -MinLocalTurns 3 `
  -OutputPath '.\.local\multiplayer-lab\results\safe-auto-summary.json'
~~~

PASS 必须同时证明：Safe Auto 在测量窗口只启用一次；至少 3 个 distinct request/turn；
3 个回合都有自动 arm、Safe EndTurn、fresh Probe/capture 和 fresh search；启用后没有新的
`UI_ACTION action=deploy`；没有 `MP_SAFE_AUTO_STOP`、远端部署中止、旧 request 复用或
自定义网络路径。2026-09-22 真实 Host/Client 三回合 Smoke 已通过上述验证器，
结果见 [`evidence/safe-auto-runtime-2026-09-22.json`](evidence/safe-auto-runtime-2026-09-22.json)。

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

默认安装仍不因本手册自动进入 Safe Execute。显式 Safe Execute 已包含 Reactive Carry，
并新增实验性 Safe Auto；Safe Auto 只持续重新授权当前本地安全边界，不等同于单人 Full Auto。
Multiplayer Instant、Potion、Choice、单人 Full Auto、Replay 和队友目标仍保持关闭。
MP2B/MP2C 历史边界、Reactive Carry 与 Safe Auto 的证据分别见上文；后续扩大能力边界仍需
独立计划和独立实机证据。
