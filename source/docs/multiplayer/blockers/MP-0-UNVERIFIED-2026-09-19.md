# MP-0 阻碍与未验证项（2026-09-19）

本文件只记录当前多人适配的阻碍、失败和未验证事实；它不构成 PASS 证据，也不解除 `MultiplayerProbe` 门禁。

## 已确认

- 本机游戏版本为 `v0.107.1`，commit `59260271`，与当前 CombatSolver 目标版本一致。
- 直接启动 `SlayTheSpire2.exe --headless --force-steam off --clientId 2026091901 --fastmp=host nomods` 可以进入主菜单并持续运行；15 秒内日志没有出现 `ENetHost`、lobby 或可确认的监听成功记录，随后只停止了本次探针进程。
- 当前游戏程序集的错误提示显示 `fastmp` 期望值为 `host`、`load` 或 `join`；交接材料中使用的 `host_standard` 尚未被当前二进制证实，不能直接作为测试命令。
- 客户端 Mod 加载探针未执行：包含临时安装与递归清理的命令被执行策略拒绝，命令在启动前失败，没有修改游戏 `MODS`，没有启动客户端，也没有产生待清理副本。
- 本轮 Host 临时日志的显式清理命令也被执行策略拒绝；`D:\yingye\CombatSolver\.local\multiplayer-lab\cli-probe-host\host.log`（7,640 字节）仍在本机，未提交到 GitHub。

## 本轮已处理的问题

- 原先直接向正式游戏 `MODS` 暂存 Client 的启动探针已撤掉，不再作为测试路径。
- 已加入 `source/tools/multiplayer-lab/prepare-instances.ps1`、`start-host.ps1`、`start-client.ps1`、`stop-owned-instances.ps1` 和 `collect-results.ps1`；它们复用 `headless-runtime.ps1` 的私有快照与 ownership marker，并把 Host/Client 的 APPDATA、LOCALAPPDATA、日志隔离到实例目录。
- `validate-phase0-results.ps1` 已拆分 MP-0A（连接兼容）和 MP-0B（只读状态）；`localPlayCardSync`、`localEndTurnSync`、`fastActionStress` 不再阻塞 MP-0，移至 MP-2 Safe Execute。
- 本轮仅完成 PowerShell 语法和差异检查，没有运行新的双实例；上述脚本实现不构成 MP-0 证据。

## 隔离启动实测（仍不是 MP-0 证据）

- `HostVanilla` 私有快照已准备到 `D:\yingye\CombatSolver\.local\multiplayer-lab\instances\mp-host-20260919`；`ClientVanilla`、`ClientRitsuOnly` 和 `ClientCombatSolver` 私有快照也已准备完成。正式 Steam 安装的 `MODS` 项数仍为 0。
- 首次 Host 快照准备暴露了共享 snapshot 函数对无 RitsuLib profile 无条件写 marker 的错误；已修正为仅在 plan 含 RitsuLib 文件时创建 marker。失败 staging 已由 ownership 清理，源游戏未变更。
- Host 私有实例可启动到主菜单并由 marker 安全停止；Client 私有实例能识别 RitsuLib/CombatSolver manifest，启动日志显示 Mod 排序为 RitsuLib → CombatSolver。
- 使用当前二进制已确认的 `--fastmp=host` 和 `--fastmp=join` 做了有界双实例启动探针：Host 日志为 `D:\yingye\CombatSolver\.local\multiplayer-lab\instances\mp-host-20260919\logs\20260919-130414-host-0d3b45eb.log`，只确认启动到主菜单，未出现已确认的 ENet listener/lobby；Client 日志为 `D:\yingye\CombatSolver\.local\multiplayer-lab\instances\mp-client-solver-20260919\logs\20260919-130431-client-13bd112a.log`，进入 `ENetClientConnectionInitializer` 后出现 `Connection timed out`。两实例均已通过 ownership marker 停止。
- Client 首次启动还停在原生“尚未确认 Mod 警告”弹窗；因此没有进入战斗，也没有产生可用于 MP-0B 的 Probe JSONL、DrawPile、敌人同步或生命周期证据。
- 对 `data_sts2_windows_x86_64\sts2.dll` 的只读 IL 检查确认：`CheckCommandLineArgs` 接受 `host`、`host_standard`、`host_daily`、`host_custom`、`load`、`join`；`fastmp=host` 的非 Steam 路径调用 `StartENetHost(33771, 4)`，`fastmp=join` 固定使用 `127.0.0.1:33771`。因此当前不是“参数值未知”。
- 再次启动 Host 并等待进入主菜单后，`Get-NetUDPEndpoint -LocalPort 33771`、`Get-NetTCPConnection -LocalPort 33771` 和 `netstat -ano` 均未观察到 33771 监听；Host 进程本身仍存活并已由 ownership marker 停止。下一步需确认原生 Host UI 的启动时序或游戏网络初始化失败原因，不能用端口缺失推断 Mod/Probe 兼容性结论。
- 在隔离的 `ClientCombatSolver` 快照中预置 `settings.save` 的 `mod_settings.player_agreed_to_mod_loading=true` 和空 `mod_list` 后重新启动，日志 `D:\yingye\CombatSolver\.local\multiplayer-lab\instances\mp-client-solver-20260919\logs\20260919-131817-client-00e5a4b0.log` 仍报告 `user has not yet seen the mods warning`；未进入战斗，也未产生 Probe JSONL。该尝试只改了私有快照，正式安装和正式用户数据未改动。

## 仍然阻塞 MP-0 PASS

以下证据当前均缺失，必须保持 `UNVERIFIED`：

- Vanilla Host 接受只安装 RitsuLib/CombatSolver 的 Client，且 Host 不需要 CombatSolver。
- Lobby、角色准备、战斗开始/结束、下一层、退出和重新加入的完整生命周期。
- Client 唯一识别本地玩家、真实 Hand/DrawPile 顺序、抽牌、洗牌、Discard/Exhaust、Enemy 状态和 MultiplayerScaling。
- 队友动作产生可观察的 world fingerprint，且 Probe 全程不修改真实 `CombatState` 或 RNG。
- Local PlayCard、Local EndTurn、连续快速动作和 wire/model compatibility 的 Host/Client 对照结果。

## 当前安全边界

- Runtime 默认继续使用 `MultiplayerProbe`：只读采集，不搜索、不部署、不自动选牌、不自动 EndTurn、不发送自定义网络包。
- 没有真实 Host/Client 证据前，不得切换 `MultiplayerAdvisor` 或 `MultiplayerSafeExecute`。
- 临时 Host 日志位于本机 `.local/multiplayer-lab/cli-probe-host/host.log`，未作为通过证据提交；如需复核，应重新运行并保存脱敏的成对 Host/Client 证据。

## 下一步

现在已有能准备隔离快照、启动可见 Host/Client、停止自有进程和收集证据的基础设施；仍需要解决 Host 监听/Join 超时，并人工完成 A/B/C 三组真实 lobby/角色/Ready/战斗流程，再将日志和 Probe JSONL 填入 MP-0A/MP-0B 矩阵。完成前，不能把 FastMP 入口探针或单进程启动结果升级为 MP-0 通过。
