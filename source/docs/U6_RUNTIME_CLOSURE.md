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
