# CombatSolver 2026-09-19 多人 Phase 0 交接

## 接手入口

下一轮先读取本文件、仓库根目录 `AGENTS.md`、`source/AGENTS.md`，再读取：

- [Phase 0 矩阵](../multiplayer/evidence/phase0-matrix-2026-09-19.json)
- [MP-0 阻碍记录](../multiplayer/blockers/MP-0-UNVERIFIED-2026-09-19.md)
- [多人 Lab README](../../tools/multiplayer-lab/README.md)
- `D:\yingye\Multiplayer Apply\CombatSolver-multiplayer-current-issues-and-revised-phase0-plan.md`
- `D:\yingye\Multiplayer Apply\CombatSolver_full_repo_audit_multiplayer_readiness_2026-09-19.md`
- `D:\yingye\Multiplayer Apply\CombatSolver_multiplayer_reduced_feature_strategy_2026-09-19.md`

本交接对应的功能基线是 CombatSolver `0.40.2`、游戏 `0.107.1`、兼容符号 `STS2_01071`；功能证据基线和本次审计提交均以当前 `main` 的 `git log -1` 为准。

## 当前结论

MP-0 的完整矩阵仍为 `UNVERIFIED`，但已按 post-test audit 拆分为 **MP-0 Core = PASS**、**MP-0 Hardening = INCOMPLETE**。生命周期缺口不再阻塞 MP-1 Advisor 的受控实现入口；默认运行仍是 Probe，Advisor 只能显式 opt-in，Safe Execute 仍 blocked。

- Phase 0 矩阵：14 项 `PASS`，仅 `lifecycle` 为 `UNVERIFIED`。
- MP-0A 三组连接矩阵已通过：`HostVanilla + ClientVanilla`、`HostVanilla + ClientRitsuOnly`、`HostVanilla + ClientCombatSolver`。
- 新 DLL 的单 Client 只读牌堆、实例 token、洗牌、Discard/Exhaust、远端摘要和多人缩放证据已通过。
- 双 Client 对照已通过：Host 使用 Vanilla，两个 CombatSolver Client 使用不同本地 ID `1000` / `1001`；两个独立 Client 的同 Seed 敌人公开状态集合差异为 `0/0`。
- 该敌人状态结论只表示 Client-to-Client 公共状态对照，不表示自定义 Host 协议已经实现或验证。
- 最终双 Client 归档时有三场战斗开始、两场结束，第三场已开始但未收尾；未捕获连接建立后的干净退出/重新加入闭环，进程停止本身不计作生命周期证据。
- 已补齐的 post-test audit contract：本地私有与远端公开/Unknown 知识边界、local-player-only root、远端私有读取 fail closed、多人缩放脱离 live state、未建模远端遗物 hook fail closed、Probe Lab-only evidence、schema v2 的 `runSeed`/`combatSegmentId`、紧凑 fingerprint 快速路径和可选的每条证据 flush。

## 最终实机证据

最终 D: 隔离归档：

`D:\yingye\CombatSolver\.local\multiplayer-lab\results\mp0-c-dual-client-20260919\20260919-201041-fbab53a5`

- Client A Probe：234 条记录，本地 `netId=1000`，`playerCount=3`。
- Client B Probe：243 条记录，本地 `netId=1001`，`playerCount=3`。
- 合计 `477/477` 条记录满足 `readOnly=true`、`searchStarted=false`、`actionsEnqueued=false`、`customNetworkPacketSent=false`。
- 敌人状态对照报告：[enemy-state-comparison.json](D:/yingye/CombatSolver/.local/multiplayer-lab/results/mp0-c-dual-client-20260919/20260919-201041-fbab53a5/enemy-state-comparison.json)。
- 最终矩阵：[phase0-matrix-2026-09-19.json](D:/yingye/CombatSolver/source/docs/multiplayer/evidence/phase0-matrix-2026-09-19.json)。

新 DLL 的牌堆实例 token 证据仍以以下 C 组归档为准：

`D:\yingye\CombatSolver\.local\multiplayer-lab\results\mp0-c-new-dll-20260919\20260919-193408-4dffbcd3`

当前 DLL：[CombatSolver.dll](D:/yingye/CombatSolver/artifacts/CombatSolver/CombatSolver.dll)，约 `4,196,352` 字节；本次源码审计已完成 Release 构建并复制到该路径。

## 下一轮优先事项：Hardening 与 Advisor 受控验证

生命周期仍是 MP-0 Hardening 的唯一未完成证据；下一轮需要用户手动操作游戏，启动新游戏前必须先告知用户，不能自动点击 UI。目标是捕获清晰的连接后生命周期闭环：

1. 仅使用 D: 隔离实例启动 Vanilla Host、CombatSolver Client A 和 Client B。
2. Client A 使用 `1000`，Client B 使用 `1001`；同一 Host 上不得让两个客户端使用原生默认的重复 ID `1000`。
3. 用户手动完成加入、角色/Ready，并至少进入一次有 Probe 的战斗。
4. 在连接已建立后，由用户手动让一个 Client 正常退出并重新加入同一 Host；重新加入后再观察一次战斗或 Lobby 状态。
5. 先收集日志和 Probe，再停止实例；不要把 `stop-owned-instances.ps1` 或进程被终止当作退出/重新加入证据。
6. 用收集结果重新运行 `validate-phase0-results.ps1 -Phase All`；只有日志、Probe 和生命周期顺序都可审查时，才更新矩阵。

推荐复用的 D: 实例根目录：

- Host：`D:\yingye\CombatSolver\.local\multiplayer-lab\runtime-mp-host-card-instance-20260919`
- Client A：`D:\yingye\CombatSolver\.local\multiplayer-lab\runtime-mp-client-card-instance-20260919`
- Client B：`D:\yingye\CombatSolver\.local\multiplayer-lab\runtime-mp-client-observer-20260919`

启动命令只作为下一轮手动测试入口，不在本次交接时执行：

```powershell
$labRoot = 'D:\yingye\CombatSolver\.local\multiplayer-lab'
$toolRoot = 'D:\yingye\CombatSolver\source\tools\multiplayer-lab'

pwsh -NoLogo -NoProfile -File "$toolRoot\start-host.ps1" `
  -InstanceRoot "$labRoot\runtime-mp-host-card-instance-20260919" -ForceSteamOff
pwsh -NoLogo -NoProfile -File "$toolRoot\start-client.ps1" `
  -InstanceRoot "$labRoot\runtime-mp-client-card-instance-20260919" `
  -ClientId 1000 -ForceSteamOff
pwsh -NoLogo -NoProfile -File "$toolRoot\start-client.ps1" `
  -InstanceRoot "$labRoot\runtime-mp-client-observer-20260919" `
  -ClientId 1001 -ForceSteamOff
```

## 安全与清理边界

- Runtime 保持 `MultiplayerProbe` 只读路径；未完成 MP-0 前不启用 Advisor/Safe Execute。
- 以后只在 D: 准备和运行多人测试；正式 Steam 安装没有被本轮修改。
- 当前 Host、Client A、Client B 进程均已停止。
- 首次失败收集留下的精确 D: 临时目录为 `D:\yingye\CombatSolver\.local\multiplayer-lab\results\mp0-c-dual-client-20260919\20260919-201017-22244263`，约 `7.55 MiB`，可恢复；删除命令被执行策略拦截，未改用其他删除方法。清理前必须再次确认精确路径和用途。
- 早期 C: 测试目录和桌面旧日志仍未删除；它们不是下一轮 D: 测试入口。除非用户再次明确授权并且删除策略允许，不要切换删除方法。

## 交接完成标准

完成下一轮后，必须同时满足：

- 若要宣布完整 MP-0 PASS，生命周期检查必须从 `UNVERIFIED` 变为有日志和 Probe 支持的 `PASS`，矩阵整体状态才可从 `UNVERIFIED` 改为 `PASS`；
- 当前 post-test audit 交接可在不改变上述事实的前提下完成：Core/Hardening/Advisor/Safe Execute 状态必须明确，默认 Probe 与显式 Advisor 门禁必须保持可审计；
- 保持 `probeReadOnly=true`、无自定义网络包、无自动出牌/结束回合；
- 更新本阻碍文件和本交接文档，执行定向校验、`git diff --check`，提交并推送 GitHub；
- 每轮对话继续提供当前 [CombatSolver.dll](D:/yingye/CombatSolver/artifacts/CombatSolver/CombatSolver.dll) 链接。
