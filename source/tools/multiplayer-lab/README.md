# Multiplayer Phase 0 lab

本目录只提供可审计的 MP-0A/MP-0B 实机测试基础设施。它不会启动自动
Lobby、修改能力表、启用 Multiplayer Advisor，也不会把 `UNVERIFIED` 推断为
`PASS`。

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
  加入、选角色和 Ready。`-FastMpMode host|join` 只在显式指定时传给当前
  二进制；它的结果仍是 UNVERIFIED，不构成连接证据。
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

## 推荐流程

先分别准备三类组合：

~~~powershell
pwsh -NoLogo -NoProfile -File .\prepare-instances.ps1 -Profile HostVanilla -Instance mp-host
pwsh -NoLogo -NoProfile -File .\prepare-instances.ps1 -Profile ClientRitsuOnly -Instance mp-client-ritsu
pwsh -NoLogo -NoProfile -File .\prepare-instances.ps1 -Profile ClientCombatSolver -Instance mp-client-solver
~~~

再启动：

~~~powershell
pwsh -NoLogo -NoProfile -File .\start-host.ps1 -InstanceRoot "$env:LOCALAPPDATA\CombatSolver\multiplayer-lab\mp-host"
pwsh -NoLogo -NoProfile -File .\start-client.ps1 -InstanceRoot "$env:LOCALAPPDATA\CombatSolver\multiplayer-lab\mp-client-solver"
~~~

手动完成 Host/Join、角色和 Ready 后，停止时只传入本次准备过的 root：

~~~powershell
pwsh -NoLogo -NoProfile -File .\stop-owned-instances.ps1 `
  -InstanceRoot "$env:LOCALAPPDATA\CombatSolver\multiplayer-lab\mp-host"
pwsh -NoLogo -NoProfile -File .\stop-owned-instances.ps1 `
  -InstanceRoot "$env:LOCALAPPDATA\CombatSolver\multiplayer-lab\mp-client-solver"
~~~

收集和校验：

~~~powershell
pwsh -NoLogo -NoProfile -Command "& '.\collect-results.ps1' -InstanceRoot @('$env:LOCALAPPDATA\CombatSolver\multiplayer-lab\mp-host', '$env:LOCALAPPDATA\CombatSolver\multiplayer-lab\mp-client-solver')"
pwsh -NoLogo -NoProfile -File .\validate-phase0-results.ps1 `
  -Phase MP-0A -MatrixPath .\.local\multiplayer-lab\results\<run>\phase0-matrix.template.json
~~~

退出码：0 为 PASS，1 为矛盾/无效 Probe，2 为缺失或仍为 UNVERIFIED。真实
Host/Client 运行证据必须带可审查的日志位置；单进程模拟和合成 JSON 不可作为
通过证据。

当前阻碍和未验证事实记录在
`source/docs/multiplayer/blockers/`。
