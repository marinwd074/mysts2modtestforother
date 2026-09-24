# Multiplayer Phase 0 lab

本目录只提供可审计的 MP-0A/MP-0B 实机测试基础设施。它不会启动自动
Lobby，也不会把 `UNVERIFIED` 推断为 `PASS`。Client 默认仍是 Probe；显式传入
`-MultiplayerMode advisor` 只启用 Advisor 搜索。MP-2 提供显式的
`-MultiplayerMode safe-execute` opt-in，以及只接受本目录创建的
`ClientCombatSolver` 私有实例、Probe evidence 的 `safe-execute-lab` 证据模式。

> 开始任何 Host/Client 实机运行前先读
> [多人实机运行手册](../../docs/multiplayer/RUNBOOK.md)。其中记录了必须关闭
> Steam transport、Mod 首次加载后重启、ClientId、Graceful 停止等已知操作事实。

## 阶段边界

MP-0A 只验证连接兼容：

~~~text
vanillaHostAcceptedClient
hostUnaware
noCustomNetworkPackets
wireModelCompatibility
~~~

MP-0B 只验证 Client-only read-only 状态：

~~~text
localPlayerIdentity
localHand
localDrawPile
drawAfterDraw
shuffleOrder
localDiscardExhaust
enemyStateSync
multiplayerScaling
remoteWorldDelta
probeReadOnly
lifecycle
singleplayerRegression
~~~

`localPlayCardSync`、`localEndTurnSync`、`fastActionStress` 属于 MP-2 Safe
Execute，不再是 MP-0 的必需项。`FastMP` 命令行入口即使能启动，也不等于
Lobby、wire 或战斗证据。

## 脚本

- `prepare-instances.ps1` 使用 `headless-runtime.ps1` 创建带 ownership marker
  的私有 Host/Client game snapshot。支持 `HostVanilla`、`ClientVanilla`、
  `ClientRitsuOnly`、`ClientCombatSolver`，不会修改正式 Steam 安装或正式
  `MODS`。默认实例根目录为仓库 D: 盘下的
  `.local/multiplayer-lab/runtime-<instance>`；脚本会拒绝 C: 或其他盘符，避免
  把大型测试快照写入系统盘。
- `start-host.ps1` / `start-client.ps1` 只启动指定私有 snapshot，使用独立
  `APPDATA`、`LOCALAPPDATA` 和日志；同机 Lab **默认自动传入
  `--force-steam=off`**，即使调用方忘记 `-ForceSteamOff` 也不会重新踩坑。
  `-ForceSteamOff` 仍可显式写出以强调意图；只有专门验证 Steam transport
  时才使用 `-AllowSteam`。默认保留可见 UI，允许用户手动建房、
  加入、选角色和 Ready。`-ClientId` 只用于同一台机器上同时运行多个
  `FastMpJoin` 客户端；原生默认值是 `1000`，每个客户端必须使用不同的
  非零 ID。`-FastMpMode host|join` 只在显式指定时传给当前二进制。
   `start-client.ps1` 要求显式传入非空的
   `-MultiplayerMode probe|advisor|safe-execute|safe-execute-lab`，只设置当前 Lab 进程环境；
  `safe-execute-lab` 额外要求 `ClientCombatSolver` ownership/profile marker
  和 Lab Probe evidence 环境；`safe-execute` 是明确的正式能力 opt-in。
- 当前 Modded Client 的固定流程是：**第一次启动只用于加载 Mod；完成 Mod 加载并
  重启一次游戏后，第二次启动才进入正式 Host/Join/Smoke**。第一次启动不得作为
  multiplayer runtime evidence。重新准备/重建 Client snapshot、替换 Mod payload，
  或游戏再次提示需要重启时，重复这一步。
- 同一 `InstanceRoot` 的 `Roaming`/`Local` 目录会跨进程保留，后续可直接复用
  已准备的实例而不重新复制游戏快照；进程真正重启时仍会重新加载 Mod DLL，
  未写入存档的当前战斗或房间状态不保证恢复。更换构建产物或游戏底座时重新运行
  `prepare-instances.ps1`；同一产物的启动/停止不应删除实例根目录，未变化时准备命令
  会保持 snapshot 不变。
- `stop-owned-instances.ps1` 只接受显式 instance root，并同时校验 marker、
  PID、进程出生时间和 executable path；没有 ownership 证据就停止。默认使用
  `Graceful` 关闭窗口并等待游戏正常退出，让 CombatSolver journal 有机会排空；
  只有清理卡死实例时才显式使用 `-Mode Force`，强制结束可能丢失缓冲证据。
- `collect-results.ps1` 只复制指定实例的日志/Probe JSONL，并生成
  `UNVERIFIED` matrix 模板，模板包含 Vanilla、RitsuLib、CombatSolver 三组
  `profileResults`；Lab 进程会把诊断写入实例下的
  `diagnostics/CombatSolver-BugReports/`，不会误收集桌面上其他运行的证据；脚本
  不会修改实例状态或生成 PASS。
- `validate-phase0-results.ps1` 是只读证据校验器。`-Phase MP-0A` 不要求
  Probe，但要求三组 profile 都有明确结果；`-Phase MP-0B` 要求真实 Probe
  JSONL，默认 `-Phase All` 同时校验三组 profile、字段矩阵和 Probe。
- `compare-probe-public-state.ps1` 只读比较两个独立 CombatSolver Client
  Probe 的分段敌人公开状态；只有每段 seed 和观察到的有序敌人状态集合完全相同
  才报告 `PASS`，采样窗口不同报告 `UNVERIFIED`，且不会自动修改 Phase 0 矩阵。
  比较器优先使用 schema v2 的 `runSeed`/`combatSegmentId`，同时兼容旧的
  schema v1 归档。
- `validate-mp2b-results.ps1` 已泛化为 bounded N-action Safe Execute 校验器：用
  `-MinActions <n>` 要求最少动作数、`-MaxActions <ceiling>` 锁定有限上限，并按同一
  request/session 校验连续 action index、每个原生 `PlayCardAction`、每次重验证、单调
  WorldVersion、无 EndTurn/药水/Choice/Replay/远端目标和部署后的新搜索。默认参数仍
  兼容历史 MP-2B 两动作日志；`test-mp2b-validator.ps1` 当前覆盖 6 个合成用例，其中
  包含 5-action 正常通过；合成日志不能替代真实 Host/Client 证据。
- `validate-mp2b-interference-results.ps1` 泛化校验任意动作 i 后的远端干扰：记录
  `MP2B_REMOTE_DELTA_ABORT`，用 `-MinCompletedActions` 约束中止前已完成动作数，证明不捕获
  action i+1、没有 EndTurn/药水/Choice/Replay，并在中止后重新搜索。若一个 Client journal
  包含多次尝试，可用 `-RequestId <deployment-request-id>` 选择完整 session；
  `test-mp2b-interference-validator.ps1` 当前覆盖 6 个合成用例，其中包含“两张牌后中止”；
  它与正常 bounded N-action 验证器不能互相替代。
- `validate-carry-ranking-results.ps1` 只读验证 Carry Ranking R1/R2 runtime journal。R1 要求真实 root 至少出现一个 `all_player_threats>0` 的公开原版攻击威胁；R2 要求最终选中路线真实出现 `carryPreference>0`、`threatsRemoved>0` 且 `remoteRiskAfter<remoteRiskBefore`。某局没有形成正向 Carry 选择返回 `UNVERIFIED`，不误报实现失败；证据自相矛盾才返回 `FAIL`。`test-carry-ranking-validator.ps1` 覆盖 PASS/UNVERIFIED/FAIL 三类合成解析并接入 L1 CI；合成日志不能替代 Host/Client 实机证据。

### Snapshot 同步合同

`prepare-instances.ps1` 使用 snapshot schema 2，将实例 game root 视为持久的
base-game snapshot，并把 RitsuLib/CombatSolver 放在 profile overlay 中：

- 游戏版本或底座文件变化、旧 schema、底座完整性校验失败，才执行 staging 全量重建。
- CombatSolver 构建变化只更新 CombatSolver overlay 文件；RitsuLib 变化只更新 RitsuLib
  overlay 文件。`HostVanilla` 没有这些 overlay，因此不会因 CombatSolver 构建变化重建。
- marker 会拆开保存 `baseGameId`、`ritsuArtifactId`、`combatSolverArtifactId`；输出中的
  `snapshotAction` 为 `FULL_REBUILD`、`OVERLAY_UPDATED` 或 `REUSED`，并分别给出
  `baseGameAction`、`ritsuAction`、`combatSolverAction` 和 `copiedFiles`。`syncMode` 保留
  `full-rebuild`、`overlay-incremental`、`unchanged` 供兼容审计。`-ForceRebuild` 仍可显式
  要求全量重建，也用于切换已有实例的 profile。
- Overlay 先复制到带 hash 校验的临时 managed tree，再在停止游戏后 rename 替换；Ritsu
  和 CombatSolver 的 DLL/JSON/辅助文件不会在同一 managed overlay 内出现半更新状态，失败
  会回滚旧目录。
- 增量更新仍只在私有 owned root 内进行，逐文件拒绝 reparse point，并在目标游戏进程运行
  时禁止覆盖；正式证据仍与 snapshot/运行目录隔离，不会写入 Steam 安装或正式 `MODS`。

Lab Client 会自动设置 `COMBATSOLVER_MULTIPLAYER_PROBE_EVIDENCE=1`，因此
Probe JSONL 只落在实例诊断目录；普通桌面运行不会因为 Probe 观察而持续写证据。

## 推荐流程

先分别准备三类组合：

~~~powershell
pwsh -NoLogo -NoProfile -File .\prepare-instances.ps1 -Profile HostVanilla -Instance mp-host
pwsh -NoLogo -NoProfile -File .\prepare-instances.ps1 -Profile ClientRitsuOnly -Instance mp-client-ritsu
pwsh -NoLogo -NoProfile -File .\prepare-instances.ps1 -Profile ClientCombatSolver -Instance mp-client-solver
~~~

再启动：

~~~powershell
$labRoot = 'D:\yingye\CombatSolver\.local\multiplayer-lab'
pwsh -NoLogo -NoProfile -File .\start-host.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-host" `
  -ForceSteamOff
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -MultiplayerMode safe-execute `
  -ForceSteamOff
~~~

同一 Host 上启动第二个本地 Client 时，必须使用不同的 ID，例如
`-ClientId 1001`；否则 Host 会按重复 peer ID 拒绝连接。

手动完成 Host/Join、角色和 Ready 后，停止时只传入本次准备过的 root：

~~~powershell
pwsh -NoLogo -NoProfile -File .\stop-owned-instances.ps1 `
  -InstanceRoot 'D:\yingye\CombatSolver\.local\multiplayer-lab\runtime-mp-host'
pwsh -NoLogo -NoProfile -File .\stop-owned-instances.ps1 `
  -InstanceRoot 'D:\yingye\CombatSolver\.local\multiplayer-lab\runtime-mp-client-solver'
~~~

收集和校验：

~~~powershell
pwsh -NoLogo -NoProfile -Command "& '.\collect-results.ps1' -InstanceRoot @('D:\yingye\CombatSolver\.local\multiplayer-lab\runtime-mp-host', 'D:\yingye\CombatSolver\.local\multiplayer-lab\runtime-mp-client-solver')"
pwsh -NoLogo -NoProfile -File .\validate-phase0-results.ps1 `
  -Phase MP-0A -MatrixPath .\.local\multiplayer-lab\results\<run>\phase0-matrix.template.json
~~~

若下一轮使用 Host + 两个 CombatSolver Client 观察公开敌人状态：

~~~powershell
pwsh -NoLogo -NoProfile -File .\prepare-instances.ps1 `
  -Profile ClientCombatSolver -Instance mp-client-observer
pwsh -NoLogo -NoProfile -File .\compare-probe-public-state.ps1 `
  -LeftProbePath 'D:\yingye\CombatSolver\.local\multiplayer-lab\results\<run>\client-a\probe\multiplayer-probe-*.jsonl' `
  -RightProbePath 'D:\yingye\CombatSolver\.local\multiplayer-lab\results\<run>\client-b\probe\multiplayer-probe-*.jsonl' `
  -OutputPath 'D:\yingye\CombatSolver\.local\multiplayer-lab\results\<run>\enemy-state-comparison.json'
~~~

比较报告是辅助证据，仍需人工确认 Host/Client 身份、生命周期和安全边界。

## MP-2B 两动作 Smoke（历史实机；复验步骤）

MP2B 使用同一套 HostVanilla + ClientCombatSolver、Steam transport off 和 Mod
warm-up 后的正式第二次 Client 启动；正式入口命令为：

~~~powershell
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute
~~~

用户手动完成 Join、Ready、进入本地玩家回合，确认点击前没有自动出牌后只点击一次
“执行本回合”。当前实现最多连续执行两张已分类为安全的本地普通牌；每张牌都应通过
原生 `PlayCardAction`，费用/能量和手牌随实际动作变化，完成后不自动 EndTurn，界面
应回到等待下一回合/可重新计算的安全边界。保留日志直到动作后的新 debounce search
出现，再用默认 `Graceful` 停止实例。

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -OutputPath '.\.local\multiplayer-lab\results\mp2b-summary.json'
~~~

正常 Smoke 通过要求验证器返回 `MULTIPLAYER_MP-2B_PASS`。另做一次远端干扰场景：
第一张牌完成、第二张牌尚未执行时，由另一 Client 进行一次公开动作；预期当前 Client
记录 `MP2B_REMOTE_DELTA_ABORT`、不再捕获第二个原生动作并重新搜索。该场景是安全边界
证据，不应按正常两动作 PASS 计数。验证远端干扰日志：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-interference-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -RequestId '<deployment-request-id>' `
  -OutputPath '.\.local\multiplayer-lab\results\mp2b-interference-summary.json'
~~~

验证器返回 `MULTIPLAYER_MP-2B_REMOTE_ABORT_PASS` 才表示安全中止证据完整。2026-09-20
实机正常 Smoke 与远端干扰 Smoke 均已通过，摘要见
`docs/multiplayer/evidence/mp2b-smoke-2026-09-20.json`。

## MP-2C bounded N-action Smoke

MP-2C 使用上面的正式 `safe-execute` 启动方式，但一次用户点击可连续执行当前安全本地
`PlayCard` 前缀，policy ceiling 为 6；每张牌都必须等待原生队列完成、稳定 `WorldVersion`
并通过现有重验证，才允许下一张。用户只点击一次“执行本回合”，选择至少三张连续安全牌的
回合；观察 UI 的 `正在执行 n/6`、费用/能量减少、手牌减少，以及完成后保持当前回合并重新
计算最新路线。该段是 MP-2C 历史基线，不包含 Reactive Carry 的跨回合 Safe EndTurn。

正常验证命令：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -MinActions 3 `
  -MaxActions 6 `
  -OutputPath '.\.local\multiplayer-lab\results\mp2c-summary.json'
~~~

应返回 `MULTIPLAYER_MP-2C_PASS`，并证明同一 request 的 `action_index=0..N-1`、每张牌
后重验证、WorldVersion 单调前进、`end_turn=false`、无 forbidden action 和 fresh search。

远端干扰验证：先让两张牌成功，再由另一 Client 在下一张牌前制造公开变化：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-interference-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -RequestId '<deployment-request-id>' `
  -MinCompletedActions 2 `
  -MaxActions 6 `
  -OutputPath '.\.local\multiplayer-lab\results\mp2c-interference-summary.json'
~~~

应返回 `MULTIPLAYER_MP-2C-remote-interference_PASS`，并证明中止后没有下一张原生动作且
发生 fresh search。2026-09-20 的真实正常/干扰结果均已通过，摘要见
`docs/multiplayer/evidence/mp2c-smoke-2026-09-20.json`。

## Reactive Carry Foundation Smoke

当前显式 `-MultiplayerMode safe-execute` 允许安全路线在最新边界通过原生
`EndPlayerTurnAction` 结束本地回合。只有当前部署已完成、动作队列为空、WorldVersion
稳定、没有待处理选择且 route/generation/本地玩家身份仍一致时才会消费一次 EndTurn
授权；接受后旧 session、route 和 authorization 立即清除。下一本地回合必须重新
Probe、capture、search、authorize。默认多人仍是 Probe，Potion、Choice、Replay、
Instant、队友控制和旧跨回合路线复用仍关闭。

本阶段的三轮实机 Smoke：

- A：安全牌序列、原生 Safe EndTurn、多人推进、下一回合 Fresh Probe/Search。
- B：EndTurn 后由观察 Client 做一次普通公开行动；用 `-RequestId` 选择完整 session，
  验证公开变化出现在下一次 fresh search 前。
- C：3 个不同 request/turn 的连续本地回合；观察 Client 只在 Solver 已结束回合后正常
  行动，整份正式 journal 不得出现远端中止、旧授权复用或自定义网络 marker。

验证命令：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-reactive-carry-results.ps1 `
  -LogPath '<post-restart-client-combat-journal.jsonl>' `
  -Smoke B -RequestId '<request-id>' `
  -OutputPath '.\.local\multiplayer-lab\results\reactive-carry-summary.json'
~~~

Smoke A/B 在同一 journal 含有额外尝试时传 `-RequestId`；Smoke C 不传该参数并要求至少
3 个 distinct request/turn。2026-09-20 A/B/C 均 PASS，摘要见
[`reactive-carry-smoke-2026-09-20.json`](../../docs/multiplayer/evidence/reactive-carry-smoke-2026-09-20.json)。

## Multiplayer Carry Ranking v1 R1/R2 Smoke

R1/R2 使用显式 `advisor` 或 `safe-execute` 的 CombatSolver Client；默认 Probe 不运行 Carry Ranking。R1 只需要在普通原版怪物显示攻击 Intent 的本地搜索根中观察公开威胁分类；R2 需要该次搜索最终选中一条消灭至少一个已证明 `AllPlayers` 威胁的路线。无需让队友按预定路线出牌，也不读取队友手牌、能量、药水或私有遗物。

验证命令：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-carry-ranking-results.ps1 `
  -LogPath '<client-combat-journal.jsonl>' `
  -Phase All `
  -OutputPath '.\.local\multiplayer-lab\results\carry-ranking-r1-r2.json'
~~~

R1 已于 2026-09-20 的 `SLIMES_WEAK` Advisor fixture 实机 PASS。随后修复并实机确认 `carryWindow=current_turn_pre_end`：Carry 只观察 startTurn 的第一次 EndTurn 前状态，未来 T2/T3 击杀不会提前获得当前威胁移除收益。当前 `f54506c3` 回归中，窗口检查 PASS、11/11 Carry contracts PASS；两次 R2 fixture 的公开威胁均为 15 durability，而当前 T1 没有精确 lethal 的等价攻击路线，因此 validator 返回 UNVERIFIED。不要再为此人工刷 Seed/HP；R2 decisive runtime 仅在今后自然出现合适完整 pre-carry tie 时补证据。

退出码：0 为 PASS，1 为矛盾/无效证据，2 为缺失或仍为 UNVERIFIED。真实
Host/Client 运行证据必须带可审查的日志位置；单进程模拟和合成 JSON 不可作为
通过证据。

当前能力边界与未验证事实以
`source/docs/multiplayer/LIMITATIONS.md` 和 `source/docs/CODEX_HANDOFF.md` 为准。
