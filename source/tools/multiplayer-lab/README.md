# Multiplayer Phase 0 lab

本目录只提供可审计的 MP-0A/MP-0B 实机测试基础设施。它不会启动自动
Lobby、修改能力表，也不会把 `UNVERIFIED` 推断为 `PASS`。Client 默认仍是
Probe；只有显式传入 `-MultiplayerMode advisor` 才设置 Advisor 环境变量，且该
入口仍只允许当前回合、本地玩家、只显示路线的搜索。

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
  非零 ID。`-FastMpMode host|join` 只在显式指定时传给当前
  二进制；`-MultiplayerMode probe|advisor` 只设置 CombatSolver 进程环境，
  默认不设置；它的结果仍是 UNVERIFIED，不构成连接证据。
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

退出码：0 为 PASS，1 为矛盾/无效 Probe，2 为缺失或仍为 UNVERIFIED。真实
Host/Client 运行证据必须带可审查的日志位置；单进程模拟和合成 JSON 不可作为
通过证据。

当前阻碍和未验证事实记录在
`source/docs/multiplayer/blockers/`。
