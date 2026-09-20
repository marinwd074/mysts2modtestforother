# Multiplayer Phase 0 lab

本目录只提供可审计的 MP-0A/MP-0B 实机测试基础设施。它不会启动自动
Lobby、修改能力表，也不会把 `UNVERIFIED` 推断为 `PASS`。Client 默认仍是
Probe；显式传入 `-MultiplayerMode advisor` 只启用 Advisor 搜索。MP-2A
另提供 **Lab-only** 的 `-MultiplayerMode safe-execute-lab`：它只允许由本目录
创建的 `ClientCombatSolver` 私有实例获得单牌执行能力，不是正式玩家 opt-in。

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
  `APPDATA`、`LOCALAPPDATA` 和日志；默认保留可见 UI，允许用户手动建房、
  加入、选角色和 Ready。`-ClientId` 只用于同一台机器上同时运行多个
  `FastMpJoin` 客户端；原生默认值是 `1000`，每个客户端必须使用不同的
  非零 ID。`-FastMpMode host|join` 只在显式指定时传给当前二进制。
  `-MultiplayerMode probe|advisor|safe-execute-lab` 只设置当前 Lab 进程环境；
  `safe-execute-lab` 额外要求 `ClientCombatSolver` ownership/profile marker
  和 Lab Probe evidence 环境，普通桌面进程或手写 `safe-execute` token 不会
  获得执行能力。
- 同一 `InstanceRoot` 的 `Roaming`/`Local` 目录会跨进程保留，后续可直接复用
  已准备的实例而不重新复制游戏快照；进程真正重启时仍会重新加载 Mod DLL，
  未写入存档的当前战斗或房间状态不保证恢复。只有更换构建产物时才需要重新
  `prepare-instances.ps1`，同一产物的启动/停止不应删除实例根目录。
- `stop-owned-instances.ps1` 只接受显式 instance root，并同时校验 marker、
  PID、进程出生时间和 executable path；没有 ownership 证据就停止。
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
- `validate-mp2a-results.ps1` 校验单牌 Safe Execute 日志：Lab capability、
  恰好一个原生 `PlayCardAction`、无药水/自动 EndTurn、动作后 WorldVersion
  失效以及新的 debounce 搜索。它可输出机器 JSON 摘要；缺少真实运行证据返回
  `UNVERIFIED`，不会把静态合同推断成实机 PASS。
- `test-mp2a-validator.ps1` 只测试上述验证器本身的 PASS/FAIL/UNVERIFIED
  判定，并由 L1 CI 调用；合成日志绝不作为多人实机证据。

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
  -InstanceRoot "$labRoot\runtime-mp-host"
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000
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

## MP-2A 单牌 Smoke

准备 Vanilla Host 与 CombatSolver Client 后，用 Lab-only 模式启动 Client：

~~~powershell
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -MultiplayerMode safe-execute-lab
~~~

进入战斗后等待路线稳定，只点击一次“执行本回合”。MP-2A Runtime 会只取第一张
通过安全分类的本地普通牌；即使路线后面还有动作，也不会在同一 deployment 继续。
完成后继续保留进程数秒，让 Probe 观察动作后的世界变化并触发新搜索，再停止实例。

使用启动输出中的本轮 `logPath` 验证：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2a-results.ps1 `
  -LogPath '<client-run.log>' `
  -OutputPath '.\.local\multiplayer-lab\results\mp2a-summary.json'
~~~

只有验证器返回 `PASS`，并人工确认 Host/Client 身份与 UI 行为后，才可形成
MP-2A 真实 Smoke 证据。该 Lab token 不得改名或推广为正式 `safe-execute`
玩家入口。

退出码：0 为 PASS，1 为矛盾/无效证据，2 为缺失或仍为 UNVERIFIED。真实
Host/Client 运行证据必须带可审查的日志位置；单进程模拟和合成 JSON 不可作为
通过证据。

当前阻碍和未验证事实记录在
`source/docs/multiplayer/blockers/`。
