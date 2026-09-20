# Multiplayer Lab 实机运行手册

本文件记录已经在真实 Host/Client 流程中确认、且新对话不应重新试错的操作事实。
任何 Multiplayer Lab 运行前先读本页。

## 职责分工

- **用户负责游戏内操作**：Host/Join、角色选择、Ready、进入战斗、等待路线稳定、点击“执行本回合”、观察卡牌/能量/格挡/UI 是否变化。
- **Codex/Agent 不负责代操作游戏 GUI**。它只负责准备环境、启动命令、代码修改、日志定位、证据收集与验证。
- 实验进行时一次只给用户一个短步骤，并说明“完成后告诉我看到什么/是否出现某状态”；避免让 Codex 自己绕远路操作游戏。

## 固定前置条件

1. **同一台电脑上的 Lab 默认关闭 Steam transport。**
   - `start-host.ps1` / `start-client.ps1` 现在默认等价于 `-ForceSteamOff`，内部传入 `--force-steam=off`。
   - 命令仍建议显式写 `-ForceSteamOff`，便于日志和人工复核。
   - 只有专门验证 Steam transport 时才使用 `-AllowSteam`；普通 FastMP / Lab 不要开 Steam。

2. **Modded Client 第一次启动不是测试运行。**
   - `ClientRitsuOnly` / `ClientCombatSolver` 第一次启动会先加载 Mod，并需要重启一次游戏。
   - 完成 Mod 加载和这次重启后，再启动同一个 Client instance；**第二次启动才用于 Host/Join/Ready/Smoke**。
   - 第一次 warm-up 的日志不能作为 multiplayer runtime evidence。
   - 重新准备/重建 Client snapshot、替换 Mod payload，或游戏再次提示需要重启时，重新执行 warm-up。

3. **ClientId 必须唯一。**
   - 单 Client 通常用 `1000`。
   - 同一 Host 上第二个本地 Client 使用其他非零值，例如 `1001`。

4. **保持实例隔离。**
   - Host 使用 `HostVanilla`。
   - Solver Client 使用 `ClientCombatSolver`。
   - 不混用 Host/Client 的 `APPDATA`、`LOCALAPPDATA`、logs 或 instance root。

5. **正式证据只取重启后的运行。**
   - 记录第二次 Client 启动返回的 `logPath`。
   - MP-2A 只在这次运行里点击一次“执行本回合”；正式 token Smoke 使用显式
     `-MultiplayerMode safe-execute`，Lab 证据 Smoke 使用 `safe-execute-lab`。
   - 等待动作完成、WorldVersion 更新和新 debounce search 后再停止。

6. **停止默认 Graceful。**
   - 使用 `stop-owned-instances.ps1` 默认模式。
   - `-Mode Force` 只用于清理卡死实例；强杀可能截断 CombatSolver journal，不能把该运行当完整证据。

## 推荐启动顺序

~~~powershell
$labRoot = 'D:\yingye\CombatSolver\.local\multiplayer-lab'

pwsh -NoLogo -NoProfile -File .\start-host.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-host" `
  -ForceSteamOff

# Client 第一次：只做 Mod 加载 warm-up
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff
~~~

让 Client 完成 Mod 加载并重启。不要把这次当正式测试。

~~~powershell
# Client 第二次：正式运行
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute-lab
~~~

然后再进行 Host/Join/Ready 和对应 Smoke。

## MP-2 Safe Execute 正式 token Smoke

正式 token 只接受明确的 `COMBATSOLVER_MULTIPLAYER_MODE=safe-execute` opt-in，
默认多人仍保持 Probe。为保持实例隔离，第一轮正式 token Smoke 仍使用本目录
准备的 `HostVanilla + ClientCombatSolver`，但 Client 第二次启动改为：

~~~powershell
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute
~~~

用户仍只做一次 Host/Join/Ready、进入战斗、确认未自动出牌后点击“执行本回合”。
验证器会检查 `FORMAL_CAPABILITY`、单个原生 `PlayCardAction`、动作后
`WorldVersion` 失效和新的 debounce search；本轮 Smoke 通过不打开 MP-2B、Potion、
Choice、自动 EndTurn、Full Auto 或 Instant。

## MP-2A 收尾

~~~powershell
pwsh -NoLogo -NoProfile -File .\stop-owned-instances.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver"
~~~

随后只验证**第二次正式 Client 运行**的日志：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2a-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -OutputPath '.\.local\multiplayer-lab\results\mp2a-summary.json'
~~~

默认安装仍不因本手册自动进入 Safe Execute；MP-2B、Multiplayer Instant、Potion、
Choice、自动 EndTurn 和 Full Auto 也保持关闭。
