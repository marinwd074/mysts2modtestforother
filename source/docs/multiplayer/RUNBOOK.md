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
   - 每次启动 Client 都必须显式传入非空的 `-MultiplayerMode`；正式 safe-execute 流程使用
     `-MultiplayerMode safe-execute`。
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
  -MultiplayerMode safe-execute `
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

## 完整 GM Console 测试源码

为了后续多人测试方便，完整 `TheBookOfAges / GM Console` 已作为 **test-only submodule**
接入：

`source/tools/multiplayer-lab/MultiplayerTestTools/TheBookOfAges`

固定上游 commit：

`234a74ccbaf46d7e385ed318c64857f1f7a90cae`

这次不再裁掉 UI：保留原作者的 GM 页面、图片、本地化、卡牌/能力/药水/遗物/怪物等工具，
以及其多人 `GameAction / INetAction / ActionQueueSynchronizer` 同步实现。该模块不进入
CombatSolver 正式项目或发布包；未来测试结束可直接删除整个 `MultiplayerTestTools`
目录和 submodule 记录。

首次本机使用：

~~~powershell
git submodule update --init --recursive -- source/tools/multiplayer-lab/MultiplayerTestTools/TheBookOfAges
~~~

运行时不再维护额外的 Workshop / 外部 Console Mod 安装脚本。完整 GM Console
测试源码已作为 test-only submodule 固定在仓库中；需要测试时由 Codex 在本地构建该模块，
并把构建产物作为 Multiplayer Lab 测试依赖部署到所有参与端。

要求：

- Host 和所有参与 Client 使用同一测试工具源码 revision / 构建产物。
- 测试工具只用于 Multiplayer Lab，不进入 CombatSolver 正式发布包。
- pinned 0.107.1 的一次最小 Host/Client Smoke 已确认同构 DLL/PCK 正常进战斗、双向 `energy 1` 与单张原生加牌同步；见 [`evidence/gm-console-multiplayer-smoke-2026-09-22.json`](evidence/gm-console-multiplayer-smoke-2026-09-22.json)。Tag Team 实际出牌语义仍未验证。

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
全部失效。下一本地回合必须重新 Probe/capture；若 live teammate state、local continuation state 与全部战斗 RNG 都和 Joint 预测完全一致，可以复用该 continuation，但旧 deployment/session/authorization 仍必须失效并为新回合重新授权；任一字段偏离则必须 fresh search。
Potion、Choice、Replay、队友控制、Instant 和旧跨回合路线复用仍关闭。

本阶段固定做三轮代表性真实 Smoke：

- **A**：本地安全牌序列 → 原生 Safe EndTurn → 多人推进 → 下一回合 Fresh Probe/Capture；状态完全一致时允许 exact Joint continuation reuse，否则 Fresh Search。
- **B**：Safe EndTurn 后、下一本地决策前由观察 Client 做一次普通公开行动；验证公开
  变化被观察且下一决策来自 fresh search。若同一 journal 有多次尝试，使用
  `-RequestId` 只选择一个完整 session。
- **C**：连续 3 个本地回合，每回合一次 Solver“执行本回合”；观察 Client 只在 Solver
  已结束回合后正常行动，不在部署期间制造远端干扰。要求 3 个 distinct request/turn、
  每回合 native EndTurn、fresh Probe/Capture，并且下一计划只能二选一：exact Joint continuation reuse 或 Fresh Search；没有 abort/stale/custom-network marker。

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
3 个本地回合都由 Runtime 自己完成：Fresh Probe/Capture → exact Joint continuation reuse 或 Fresh Search → 新
`MultiplayerSafeExecutionSession` → 安全本地普通牌 → 原生 Safe EndTurn。旧 request/session/route authorization 不得跨回合复用；只有预测状态与战斗 RNG 完全一致时路线 continuation 才能复用。

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
3 个回合都有自动 arm、Safe EndTurn、fresh Probe/capture；每个下一回合要么出现 exact Joint continuation reuse，且 `SEARCH_REUSED` 明确 `old_authorization_dead=true new_authorization_pending=true`，要么走 Fresh Search；启用后没有新的
`UI_ACTION action=deploy`；没有 `MP_SAFE_AUTO_STOP`、远端部署中止、旧 request 复用或
自定义网络路径。2026-09-22 真实 Host/Client 三回合 Smoke 已通过上述验证器，
结果见 [`evidence/safe-auto-runtime-2026-09-22.json`](evidence/safe-auto-runtime-2026-09-22.json)。

## Joint Forecast Continuation Smoke

新联合预测的 continuation 实机验证分成两种，不再把“每回合都 Fresh Search”当作唯一正确结果：

- **Reuse**：选择尽量确定的局面（推荐让观察队友没有可打牌或不做额外动作），本地 Safe EndTurn 后等待下一本地回合。要求 Fresh Probe/Capture 后出现 `SEARCH_REUSED` 与 `MP_LOCAL_XTURN_CONTINUATION_REUSED ... local_state_exact=true reason=exact`；live/predicted `ContinuationStamp` 同时覆盖本地战斗状态与 Shuffle/CardGeneration/CardSelection/EnergyCosts/Targets/Orb/MonsterAi/Niche RNG。
- **Mismatch**：在 EndTurn 后让观察队友执行与预测世界不同的可读动作。默认要求 `MP_LOCAL_XTURN_CONTINUATION_REJECTED ... reason=remote_public_mismatch`，随后 `SEARCH_REUSE_MISS` 使用同一 reject reason，并启动 Multiplayer Advisor 或 Safe Execute Fresh Search。不得再出现同一 turn/route 的 continuation reuse。

验证命令：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-joint-continuation-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -Mode Reuse `
  -OutputPath '.\.local\multiplayer-lab\results\joint-continuation-reuse.json'

pwsh -NoLogo -NoProfile -File .\validate-joint-continuation-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -Mode Mismatch `
  -ExpectedRejectReason remote_public_mismatch `
  -OutputPath '.\.local\multiplayer-lab\results\joint-continuation-mismatch.json'
~~~

退出码仍为 0=PASS、1=FAIL、2=UNVERIFIED。Reuse 与 Mismatch 都通过后，才把 Joint continuation runtime 从合同覆盖提升为实机证据。

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
