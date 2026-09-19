# MP-0 阻碍与未验证项（2026-09-19）

本文件只记录当前多人适配的阻碍、失败和未验证事实；它不构成 PASS 证据，也不解除 `MultiplayerProbe` 门禁。

## 已确认

- 本机游戏版本为 `v0.107.1`，commit `59260271`，与当前 CombatSolver 目标版本一致。
- 直接启动 `SlayTheSpire2.exe --headless --force-steam off --clientId 2026091901 --fastmp=host nomods` 可以进入主菜单并持续运行；15 秒内日志没有出现 `ENetHost`、lobby 或可确认的监听成功记录，随后只停止了本次探针进程。
- 当前游戏程序集的错误提示显示 `fastmp` 期望值为 `host`、`load` 或 `join`；交接材料中使用的 `host_standard` 尚未被当前二进制证实，不能直接作为测试命令。
- 客户端 Mod 加载探针未执行：包含临时安装与递归清理的命令被执行策略拒绝，命令在启动前失败，没有修改游戏 `MODS`，没有启动客户端，也没有产生待清理副本。
- 本轮 Host 临时日志的显式清理命令也被执行策略拒绝；`D:\yingye\CombatSolver\.local\multiplayer-lab\cli-probe-host\host.log`（7,640 字节）仍在本机，未提交到 GitHub。

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

需要一个能实际完成 lobby/角色/Ready/战斗动作的双实例驱动器，并分别隔离 Host 无 Mod 与 Client 带 Mod 的安装目录；驱动器完成前，不能把 FastMP 入口探针升级为 MP-0 通过。
