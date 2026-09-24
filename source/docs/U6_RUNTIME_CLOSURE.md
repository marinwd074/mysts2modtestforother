# U6 — 实机闭环与清理

状态：**代码清理与实机判定器已完成；真实 Host/Client U6 smoke 仍 UNVERIFIED。**

## 已完成

- 删除失效的固定 Safe Execute action ceiling：不再保留生产 `MaxActionsPerDeployment=32`。
- 删除只为历史 MP-2A 单动作合同存在的 `TakeMp2ADeploymentSlice` 与旧 limit reason aliases。
- Safe Execute capability 不再宣称固定数字上限，改为 `action_limit=selected_route`；实际 session 的 `max_actions` 仍等于本次有限搜索路线的可执行容量。
- 旧 MP-2B/MP-2C 日志 validator 仍可读取历史数字 `max_actions` 证据，但这只属于证据兼容，不再形成生产边界。
- Pinned 0.107.1 workflow 现在同时监听：
  - `MultiplayerSafeExecutePolicy.cs`
  - `MultiplayerSafeLocalActionClassifier.cs`
  - `SolverController.cs`
  - `SolverController.Deployment.cs`
- 新增 `validate-u6-runtime-closure.ps1`，只判定离线无法证明的网络链；synthetic 合同明确区分 PASS / FAIL / UNVERIFIED。

## U6 实机唯一必补链

不再重复实机验证 U5 已由 pinned production replay 覆盖的五类顺序语义。真实双端只需要一次可控场景：

1. Safe Execute 走到 `kind_teammateforecast` 边界并记录 `MP_U5_OBSERVE`。
2. 另一真实玩家在该观察窗口改变可读远端状态。
3. 本地看到更高 `WorldVersion` 且 `MP_REACTIVE_WORLD_DELTA remote_public_changed=true`。
4. 旧 request 在 forecast boundary 后不得再出现新的 `NATIVE_ACTION_CAPTURED`。
5. 随后出现基于新 WorldVersion 的 fresh debounced search。
6. 若再次部署，必须使用新的 request id，且 `search_world_version` 不早于远端变化版本。

第二次部署不是 U6 PASS 的必要条件；只要已经证明旧授权停止、远端 WorldVersion 前进并触发基于新版本的 fresh search，即可闭环。若日志中确实出现后续部署，validator 会额外检查其授权一致性。

验证命令：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\validate-u6-runtime-closure.ps1 `
  -LogPath <client CombatSolver log> `
  -OutputPath .\.local\multiplayer-lab\results\u6-runtime-closure.json
~~~

退出码：

- `0`：PASS
- `1`：发现旧 request 复活、 stale authorization 等矛盾，FAIL
- `2`：缺少真实 forecast / remote delta / fresh search 链，UNVERIFIED

## 证据边界

Pinned / compatibility 只能证明编译、合同与 detached production replay。U6 不把这些结果冒充真实 Host/Client ownership/network timing。

真实 U6 smoke PASS 后，才把 U6 整体标记 COMPLETE。之后再讨论本地精确斩杀、缓存、增量修补或更远期预测；这些不是 U6 当前验收项。


## U6-C — 一次真实 Host/Client smoke

本阶段只跑 **Vanilla Host + 1 个 CombatSolver Client**。Vanilla Host 同时充当真实队友，不再启动第三个 Client。

### Codex / Agent 执行

在仓库根目录使用 PowerShell 7：

~~~powershell
Set-Location 'D:\yingye\CombatSolver'

# 1. 构建当前分支；AfterTargets 会刷新 artifacts/CombatSolver。
pwsh -NoLogo -NoProfile -File .\source\tools\build-local-stack.ps1 -Configuration Release

# 2. 刷新两个固定隔离实例。
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\prepare-instances.ps1 `
  -Profile HostVanilla -Instance mp-host
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\prepare-instances.ps1 `
  -Profile ClientCombatSolver -Instance mp-client-solver
~~~

若本次 CombatSolver overlay 被更新，先启动一次 Solver Client 完成 Mod load warm-up，并在游戏要求重启后正常退出/重启；这次日志不计证据。正式运行必须使用 warm-up 后的新进程。

正式运行：

~~~powershell
$labRoot = 'D:\yingye\CombatSolver\.local\multiplayer-lab'

pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\start-host.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-host" `
  -ForceSteamOff

pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -MultiplayerMode safe-execute `
  -ForceSteamOff
~~~

记录第二条启动命令 JSON 中的 **正式 Client `logPath`**；只有该日志用于 U6-C。

### 用户只做一次 GUI 场景

1. Host 建房；Solver Client Join。
2. 选角色、Ready，进入普通战斗。
3. 在 Solver Client 有可执行推荐路线时，点击一次“执行本回合”。
4. 等 Solver 的本地动作停在 teammate forecast 边界后，立即在 **Vanilla Host** 打出一张会改变公开战斗状态的普通牌。
5. 不再重复刷场景；保留游戏运行片刻，让远端状态同步和 fresh search 发生。

目标日志链必须包含：

~~~text
MP2B_DEPLOY_END stop_reason=kind_teammateforecast
MP_U5_OBSERVE boundary=teammate_forecast replan=true
MP_REACTIVE_WORLD_DELTA remote_public_changed=true world_version=<higher>
SEARCH_DEBOUNCED_START world_version=<same-or-higher>
~~~

forecast boundary 后，旧 request 不得再有新的 `NATIVE_ACTION_CAPTURED`。若之后又发生部署，必须是新 request，且其 `search_world_version` 不早于远端变化版本。

### Codex / Agent 收尾

GUI 场景完成后使用默认 Graceful 停止：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\stop-owned-instances.ps1 `
  -InstanceRoot @(
    "$labRoot\runtime-mp-client-solver",
    "$labRoot\runtime-mp-host"
  )
~~~

然后只对正式 Client 的 `logPath` 运行：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\validate-u6-runtime-closure.ps1 `
  -LogPath '<formal-client-logPath>' `
  -OutputPath .\.local\multiplayer-lab\results\u6-runtime-closure.json
~~~

判定：

- `MULTIPLAYER_U6_PASS` / exit 0：U6-C PASS。
- exit 1：U6-C FAIL；直接按 validator 的 FAIL evidence 修复，不换场景掩盖问题。
- exit 2：U6-C UNVERIFIED；说明本次没有形成完整 forecast → remote delta → fresh search 证据，不把它写成 PASS。

U6-C 不重复 U5 的 pinned 顺序语义测试，也不进入 U6-D handoff 清理。
