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
- 早期仅完成 PowerShell 语法和差异检查时，上述脚本实现不构成 MP-0 证据；后续双实例实测结果见下节。

## 隔离启动实测（仍不是 MP-0 证据）

- `HostVanilla` 私有快照已准备到 `D:\yingye\CombatSolver\.local\multiplayer-lab\instances\mp-host-20260919`；`ClientVanilla`、`ClientRitsuOnly` 和 `ClientCombatSolver` 私有快照也已准备完成。正式 Steam 安装的 `MODS` 项数仍为 0。
- 首次 Host 快照准备暴露了共享 snapshot 函数对无 RitsuLib profile 无条件写 marker 的错误；已修正为仅在 plan 含 RitsuLib 文件时创建 marker。失败 staging 已由 ownership 清理，源游戏未变更。
- Host 私有实例可启动到主菜单并由 marker 安全停止；Client 私有实例能识别 RitsuLib/CombatSolver manifest，启动日志显示 Mod 排序为 RitsuLib → CombatSolver。
- 使用当前二进制已确认的 `--fastmp=host` 和 `--fastmp=join` 做了有界双实例启动探针：Host 日志为 `D:\yingye\CombatSolver\.local\multiplayer-lab\instances\mp-host-20260919\logs\20260919-130414-host-0d3b45eb.log`，只确认启动到主菜单，未出现已确认的 ENet listener/lobby；Client 日志为 `D:\yingye\CombatSolver\.local\multiplayer-lab\instances\mp-client-solver-20260919\logs\20260919-130431-client-13bd112a.log`，进入 `ENetClientConnectionInitializer` 后出现 `Connection timed out`。两实例均已通过 ownership marker 停止。
- Client 首次启动还停在原生“尚未确认 Mod 警告”弹窗；因此没有进入战斗，也没有产生可用于 MP-0B 的 Probe JSONL、DrawPile、敌人同步或生命周期证据。
- A 组复测（`Vanilla Host + Vanilla Client`）于 14:36:41/42 启动成功，两个私有进程持续响应到 14:42:18，但双方日志都只到主菜单启动阶段，没有 Lobby、Join、ENet、连接或战斗标记；归档为 `D:\yingye\CombatSolver\.local\multiplayer-lab\results\mp0-a-ui-blocked-20260919\20260919-144218-a0c1bf49`，状态保持 `UNVERIFIED`，没有 Probe。该结果是“等待手动多人 UI 操作”的阻碍，不是 Vanilla 兼容性 PASS/FAIL。
- A 组还发现游戏窗口标题不能可靠区分 Host/Client：启动 marker 的 PID 与可执行文件路径能正确对应实例，但两个窗口的内部标题出现交叉；后续证据身份应以实例 root、command line 和 marker 为准，不能以窗口标题判定角色。
- 对 `data_sts2_windows_x86_64\sts2.dll` 的只读 IL 检查确认：`CheckCommandLineArgs` 接受 `host`、`host_standard`、`host_daily`、`host_custom`、`load`、`join`；`fastmp=host` 的非 Steam 路径调用 `StartENetHost(33771, 4)`，`fastmp=join` 固定使用 `127.0.0.1:33771`。因此当前不是“参数值未知”。
- 再次启动 Host 并等待进入主菜单后，`Get-NetUDPEndpoint -LocalPort 33771`、`Get-NetTCPConnection -LocalPort 33771` 和 `netstat -ano` 均未观察到 33771 监听；Host 进程本身仍存活并已由 ownership marker 停止。下一步需确认原生 Host UI 的启动时序或游戏网络初始化失败原因，不能用端口缺失推断 Mod/Probe 兼容性结论。
- 在隔离的 `ClientCombatSolver` 快照中预置 `settings.save` 的 `mod_settings.player_agreed_to_mod_loading=true` 和空 `mod_list` 后重新启动，日志 `D:\yingye\CombatSolver\.local\multiplayer-lab\instances\mp-client-solver-20260919\logs\20260919-131817-client-00e5a4b0.log` 仍报告 `user has not yet seen the mods warning`；未进入战斗，也未产生 Probe JSONL。该尝试只改了私有快照，正式安装和正式用户数据未改动。

## 交接后实现与复测（2026-09-19）

- 通过只读 IL 检查确认 `ModSettings.PlayerAgreedToModLoading` 的存档键是 `mods_enabled`；使用该键预置私有 Client 快照后，RitsuLib 与 CombatSolver 均加载，日志报告 CombatSolver `63` 个补丁应用、`0` 个 ignored、`0` 个 failed。正式安装未改动。
- 原生多人 UI 已成功创建 Standard Host；Host 监听 UDP `33771`，Client 完成握手并取得本地 `netId=1000`。Client 收到 `Version: v0.107.1 Hash: 3954186980 Type: Standard State: InLobby`；仅报告非 Gameplay Mod mismatch，允许继续。
- 两端完成角色选择和 Ready，Host/Client 进入同一局 Seed `1SCQBUB9V1`；双方日志均进入 `EVENT.NEOW` 开局奖励页。该轮自动化输入在奖励页未能推进，因此没有把地图/战斗结果误记为新的 MP-0 证据。
- 已确认现有 `SolverController.MonitorCombatPresence` 已在主线程、战斗进行中调用只读 `MultiplayerClientProbe.Observe`，`BeginCombat/Reset` 已负责生命周期重置；本轮真正修复的是 `AppendOnlyEventLog` 每条 JSONL 写入后的 `Flush`，使运行中的证据可观察。
- 又修正了实机证据路径：普通安装仍写桌面 `CombatSolver-BugReports`，隔离 Lab 通过 `COMBATSOLVER_MULTIPLAYER_INSTANCE` 写入实例自有的 `diagnostics/CombatSolver-BugReports`，`collect-results.ps1` 已按该路径收集，避免跨运行误收集桌面文件。新 DLL 启动探针已确认实例诊断目录创建；该次未进入战斗，所以只产生进程日志，没有新的 Probe JSONL。
- 修复前一轮曾真实进入 `SLIMES_WEAK` 战斗并创建 `C:\Users\WUHU\Desktop\CombatSolver-BugReports\logs\CombatSolver\multiplayer-probe-14036-a2dd7ca66c974469a3eafc5b3a3e446f.jsonl`，但文件为 `0` 字节；这是调用链存在的线索，不是 MP-0B 通过证据。本轮已取得 `Flush` 修复后的非零 Probe，详情见下方 C 组结果。
- 上述输入阻碍只适用于此前奖励页的异步 `PostMessage` 尝试；本轮通过手动/同步 UI 输入推进到地图并进入战斗，不能再作为当前进入战斗的阻碍，但仍不构成完整生命周期证据。
- 旧战斗复测还出现 RitsuLib 生命周期警告 `Sequence contains more than one element`，但未阻止进入战斗；需与 Probe 证据分开跟踪。

## C 组非空 Probe 实机结果（2026-09-19）

- 本轮为 `Vanilla Host + CombatSolver Client`，Host 未加载 CombatSolver；两端进入同一局 Seed `NCP5BET83Z`，日志均记录 `SLIMES_WEAK` 和 `Combat started`。对应日志为 `.local/multiplayer-lab/instances/mp-host-20260919/logs/20260919-142646-host-90c5bbe6.log` 与 `.local/multiplayer-lab/instances/mp-client-solver-20260919/logs/20260919-142647-client-36cfd07c.log`。
- Client 产生第一份非空 Probe：`D:\yingye\CombatSolver\.local\multiplayer-lab\results\mp0-c-evidence-20260919\20260919-143255-4399f135\clientcombatsolver\probe\multiplayer-probe-21080-f04ebba86c30455a9902f323f3dd31a1.jsonl`，原文件 `43,300` 字节、`8` 条记录，`sequence/worldVersion` 均为 `1..8`。每条记录均为 `networkType=Client`、`playerCount=2`、本地 `netId=1000`、`IRONCLAD`，并包含 Hand/DrawPile/Discard/Exhaust、3 个敌人、9 组 RNG、远端玩家摘要、`multiplayerScalingHooks=true` 和 `cardMultiplayerConstraint=MultiplayerOnly`。
- 8 条记录的只读契约均为 `readOnly=true`、`searchStarted=false`、`actionsEnqueued=false`、`customNetworkPacketSent=false`；本轮只观察了开局抽牌变化，没有执行 PlayCard、EndTurn、洗牌、Discard/Exhaust 或制造队友动作。因此这证明的是 C 组连接后的只读观察部分，不是 MP-0 PASS。
- 校验器对该归档结果给出 `probeJsonlContract=PASS`，整体仍为 `UNVERIFIED`；归档目录为 `.local/multiplayer-lab/results/mp0-c-evidence-20260919/20260919-143255-4399f135`。当前保留的独立警告是 Client 的 RitsuLib `CombatStartingEvent: Sequence contains more than one element`，它没有阻止本轮进入战斗。

## A/B 组连接实机结果（2026-09-19）

- A 组 `Vanilla Host + Vanilla Client` 已完成 Lobby、双方 Ready、同图投票与首战同步。两端进入 Seed `1D3F5N525F` 的 `NIBBITS_WEAK`，日志均记录 `MoveToMapCoordAction`、`Creating NCombatRoom` 和 `Combat started`；归档目录为 `.local/multiplayer-lab/results/mp0-a2-evidence-20260919/20260919-155859-b3f851f0`。
- B 组 `Vanilla Host + RitsuLib Client` 已完成同样的连接路径。Host 允许 Client 独有的非 gameplay mod `STS2-RitsuLib-0.6.2`，双方进入 Seed `MMWBMK5APR` 的 `NIBBITS_WEAK`；Client 的 Ready、Host 的双方投票、`MoveToMapCoordAction` 与两端 `Combat started` 均有成对日志。归档目录为 `.local/multiplayer-lab/results/mp0-b-evidence-20260919/20260919-162533-695eb7b2`。
- B 组首次加载 RitsuLib 时，原生 Mod 确认会主动退出 Client 以重启加载；当 Host 保留旧 peer 时，重启 Client 会超时，Host 记录 `Peer not connected`。停止两个自有实例并以已确认 Mod 的私有设置重新启动后连接成功。这是实验启动时序要求，不是 RitsuLib/CombatSolver wire compatibility 失败。
- 初始地图的可用节点是底部 `_startingPointNode` `(3,0)`；`VisitedMapCoords` 为空时，直接点击上方第一层敌人节点不会产生投票。A/B 组均改为双方先投票 `(3,0)` 后正常进入首战。
- A/B 组本轮只验证到首战连接，不含 CombatSolver Probe；收集器按设计输出 `UNVERIFIED`，没有自动推断 PASS。进入战斗后未出牌、未结束回合、未作选择，也未发送自定义网络包。
- `validate-phase0-results.ps1` 原先在单独校验 MP-0B 时会把空的 profile 要求展开成 `$null`，严格模式下访问 `.Count` 失败；现已用数组包装修复。矩阵实测结果为 MP-0A `PASS`、MP-0B `UNVERIFIED`，C 组 8 条 Probe 的 JSONL 契约为 `PASS`。
- 单人回归已补齐最小证据：Release 编译 `0` 错误（保留 2 条既有 `CS9113`）、目标版本门禁 `0.107.1/0.107.1/STS2_01071`、合同测试 `5/5 PASS`；隔离 `FIRST_TURN` smoke 写出 `PASS: native 0.107.1 first-turn search; actions=20; incremental verification enabled`。专用 smoke 按既有设计不写常规 result，外层启动器以 `exit_code=0` 报收尾异常，不影响专用结果；普通 Release 已在测试后恢复。

## C 组手动战斗扩展快照（2026-09-19）

- 在用户手动操作两个可见实例后，收集器生成了 `D:\yingye\CombatSolver\.local\multiplayer-lab\results\mp0-c-manual-20260919\20260919-190721-e2a8cea1`。该快照覆盖第一场战斗结束、随机事件 `EVENT.THE_LEGENDS_WERE_TRUE`、地图移动和第二场 `SHRINKER_BEETLE_WEAK` 开始；两个自有进程在收集时仍存活，源实例和正式游戏安装未修改。
- Client Probe JSONL 有 `212` 条记录、两个战斗段（第二场开始时 sequence/worldVersion 重置），全部满足 `readOnly=true`、`searchStarted=false`、`actionsEnqueued=false`、`customNetworkPacketSent=false`；校验器对该文件报告 `probeJsonlContract=PASS`，整体 MP-0B 仍为 `UNVERIFIED`。
- 当前快照观察到 `61` 次 DrawPile 递减、`60` 次可用牌 token 前缀与 Hand 增量一致、`4` 次 DrawPile 清空后 Discard→Draw 且 Shuffle RNG counter 增长、`51` 次 Discard 变化、`4` 次 Exhaust 变化、`28` 次敌人状态变化和 `59` 次远端玩家摘要变化。
- 仍不能把牌序相关检查直接记为 PASS：旧 Probe token 只有牌名/升级/附着状态，记录 `57→58` 的同名 `SLIMED` 移动无法区分具体实例；因此当前矩阵没有被自动升级。源码已增加每场战斗内基于对象引用的只读 `instance` token，下一次新 DLL 实机需重新验证该边界。
- 本快照来自新实例 ID 改动之前启动的 DLL；新 DLL 已完成 Release 编译、5/5 合同测试和目标版本门禁，但尚未进入游戏实测。下一次启动新实例前需先通知用户并由用户手动推进。

## 新 DLL 隔离实例准备阻碍（2026-09-19）

- 为验证卡牌实例 token，尝试准备新的 `HostVanilla` 与 `ClientCombatSolver` 隔离快照时，默认 `C:` 盘在 Client 快照复制到 `SlayTheSpire2.exe` 时报告“磁盘空间不足”。失败 staging 已由脚本移除，并报告 `source_game_preserved=true`；没有启动新游戏，也没有修改正式 Steam 安装。
- 失败根目录是本轮创建的精确临时路径：`C:\Users\WUHU\AppData\Local\CombatSolver\multiplayer-lab\mp-host-card-instance-20260919`（约 242 字节）和 `C:\Users\WUHU\AppData\Local\CombatSolver\multiplayer-lab\mp-client-card-instance-20260919`（约 246 字节），目前只含 `instance.json`。按清理规则尝试删除这两个目录时被当前执行策略拒绝，因此它们仍可恢复，未再尝试替代删除方法。
- 检查时 `C:` 可用约 `2.14 GB`，`D:` 可用约 `68.5 GB`。下一次新 DLL 实测需要在用户确认后改用显式 `-RuntimeRoot D:\...`，或由用户先释放 C: 空间；启动游戏前仍必须先通知用户。

## 仍然阻塞 MP-0 PASS

以下证据当前仍缺失，必须保持 `UNVERIFIED`：

- A/B/C 三组连接证据已填入 `../evidence/phase0-matrix-2026-09-19.json`，MP-0A 可独立校验；MP-0B 仍含未完成项，整体 MP-0 保持 `UNVERIFIED`。
- Lobby、角色准备、战斗开始/结束、下一层、退出和重新加入的完整生命周期。
- C 组中抽牌后的真实顺序、洗牌、Discard/Exhaust、敌人持续同步、队友动作导致的 world fingerprint 变化，以及跨生命周期保持只读边界。
- Local PlayCard、Local EndTurn 和连续快速动作属于后续 MP-2 Safe Execute，不在本轮启用。

## 当前安全边界

- Runtime 默认继续使用 `MultiplayerProbe`：只读采集，不搜索、不部署、不自动选牌、不自动 EndTurn、不发送自定义网络包。
- 没有真实 Host/Client 证据前，不得切换 `MultiplayerAdvisor` 或 `MultiplayerSafeExecute`。
- 临时 Host 日志位于本机 `.local/multiplayer-lab/cli-probe-host/host.log`，未作为通过证据提交；如需复核，应重新运行并保存脱敏的成对 Host/Client 证据。

## 下一步

现在 A/B/C 三组均已连接到首战并形成 MP-0A 矩阵；下一步是补齐 C 组剩余的只读状态变化与生命周期场景，再完成 MP-0B。完成前，不能把当前部分结果升级为完整 MP-0 通过。
