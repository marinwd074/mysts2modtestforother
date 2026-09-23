# P0 local: Joint continuation smoke

这是 P0 的实机门禁。只做本页，不修改目标函数、Beam、Shadow 行为模型或搜索预算。

基线与故障分类口径见 [`../P0_BASELINE.md`](../P0_BASELINE.md)。

## 1. Release Build

从仓库根目录执行：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\build-local-stack.ps1 -Configuration Release
~~~

要求：0 error。若失败，停止 Smoke，只回传完整新增 error/warning。

## 2. 固定实例

~~~powershell
$labRoot = 'D:\yingye\CombatSolver\.local\multiplayer-lab'

pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\start-host.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-host" `
  -ForceSteamOff

pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute
~~~

若 Client 因 Mod 更新要求重启，本次只做 warm-up；重启后重新执行 Client 命令，第二次运行才取证。

## 3. Smoke R — exact reuse

目标：真实下一回合状态与 Shadow Joint 世界一致。

- 进入双人战斗。
- Solver Client 执行一条包含 Safe EndTurn 且具有下一回合计划的路线。
- 观察端不要额外制造偏离；尽量选择队友无额外可打动作/按预测行为自然推进的简单局面。
- 等 Solver 下一本地回合完成 fresh Probe/Capture。

验证：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\validate-joint-continuation-results.ps1 `
  -LogPath '<solver-post-restart-log>' `
  -Mode Reuse `
  -OutputPath '.\.local\multiplayer-lab\results\joint-continuation-reuse.json'
~~~

必须返回 `MULTIPLAYER_JOINT_CONTINUATION_REUSE_PASS`，并出现：
- `MP_LOCAL_XTURN_CONTINUATION_VALIDATE`
- `SEARCH_REUSED`
- `MP_LOCAL_XTURN_CONTINUATION_REUSED ... local_state_exact=true reason=exact`

## 4. Smoke M — teammate mismatch

目标：真实队友状态偏离 Shadow 世界时绝不能继续旧路线。

重新开一场干净测试。在 Solver Safe EndTurn 之后、下一本地决策前，让观察 Client 执行一个会改变可读队友状态的原生动作。

验证：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\validate-joint-continuation-results.ps1 `
  -LogPath '<solver-post-restart-log>' `
  -Mode Mismatch `
  -ExpectedRejectReason remote_public_mismatch `
  -OutputPath '.\.local\multiplayer-lab\results\joint-continuation-mismatch.json'
~~~

必须返回 `MULTIPLAYER_JOINT_CONTINUATION_MISMATCH_PASS`，并证明：
- `MP_LOCAL_XTURN_CONTINUATION_REJECTED ... reason=remote_public_mismatch`
- `SEARCH_REUSE_MISS ... continuation_reject_reason=remote_public_mismatch`
- 随后启动 Fresh Search
- 同一 turn/route 不得出现 continuation reuse

## 5. 结束

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\stop-owned-instances.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver"

pwsh -NoLogo -NoProfile -File .\source\tools\multiplayer-lab\stop-owned-instances.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-host"
~~~

Reuse + Mismatch 都 PASS 后，再对两个结果日志运行 P0 分类器：

~~~powershell
pwsh -NoLogo -NoProfile -File .\source\tools\classify-p0-baseline-result.ps1 -LogPath '<reuse-log>','<mismatch-log>' -OutputPath '.\.local\multiplayer-lab\results\p0-classification.json'
~~~

预期至少能把 mismatch 样例识别为 `teammate_prediction_deviation`，且不能出现 `simulation_error`。

两项 smoke 与分类均通过后，Joint continuation runtime 阶段封口并记录到 P0 基线；随后进入 P1 统一目标。不要继续重复同一 smoke，除非后续修改触及 continuation、root capture、Shadow replay 或 runtime deployment 边界。
