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
    - MP-2A 历史基线只在这次运行里点击一次“执行本回合”；当前 MP-2B Smoke 使用
      显式 `-MultiplayerMode safe-execute`，Lab 证据 Smoke 仍使用 `safe-execute-lab`。
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

用户仍只做一次 Host/Join/Ready、进入战斗、确认未自动出牌后点击“执行本回合”。这段
只用于复核 MP-2A 历史一动作基线；验证器会检查 `FORMAL_CAPABILITY`、单个原生
`PlayCardAction`、动作后 `WorldVersion` 失效和新的 debounce search。它不代表当前
MP-2B 两动作实机已通过。

## MP-2B 两动作 Smoke（当前待实机）

正式 Client 第二次启动继续使用上面的 `HostVanilla + ClientCombatSolver`、Steam
transport off 和 Mod warm-up 后的隔离实例，但模式必须是：

~~~powershell
pwsh -NoLogo -NoProfile -File .\start-client.ps1 `
  -InstanceRoot "$labRoot\runtime-mp-client-solver" `
  -ClientId 1000 `
  -ForceSteamOff `
  -MultiplayerMode safe-execute
~~~

用户手动完成 Host/Join/Ready 并进入本地玩家回合；确认点击前没有自动出牌后，只点击
一次“执行本回合”。MP2B 一次 deployment 最多执行两张通过安全分类的本地普通牌，
每张都必须通过原生 `PlayCardAction`。观察费用/能量减少、手牌减少和动作完成后的
稳定世界；不会自动 EndTurn，完成后 UI 应回到“等待下一回合”或可重新计算的安全边界。
保留 Client 日志直到动作后的新 debounce search 出现，再用默认 `Graceful` 停止。

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -OutputPath '.\.local\multiplayer-lab\results\mp2b-summary.json'
~~~

正常两动作 Smoke 只有在验证器返回 `MULTIPLAYER_MP-2B_PASS` 且人工确认 Host/Client
身份与 UI 行为后才能记为 PASS。另做一次远端干扰 Smoke：第一张牌完成、第二张牌尚未
执行时，由另一 Client 进行一次公开动作；预期当前 Client 记录
`MP2B_REMOTE_DELTA_ABORT`，不再捕获第二个原生动作，并启动新的搜索。该场景只证明
安全中止与重搜，不计入正常两动作 PASS。远端干扰日志使用独立验证器：

~~~powershell
pwsh -NoLogo -NoProfile -File .\validate-mp2b-interference-results.ps1 `
  -LogPath '<post-restart-client-log>' `
  -OutputPath '.\.local\multiplayer-lab\results\mp2b-interference-summary.json'
~~~

只有返回 `MULTIPLAYER_MP-2B_REMOTE_ABORT_PASS` 才能记为安全中止证据；正常两动作
验证器不能替代这个场景。

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

默认安装仍不因本手册自动进入 Safe Execute；Multiplayer Instant、Potion、Choice、
自动 EndTurn、Full Auto、跨回合和队友目标也保持关闭。MP2B 在两组实机证据完成前仍
是 `UNVERIFIED/BLOCKED`。
