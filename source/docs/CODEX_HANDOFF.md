# Codex 当前交接

## 当前技术状态

- CombatSolver `0.40.2`；目标游戏 / RitsuLib `0.107.1`；兼容符号 `STS2_01071`。
- MP-0 Core / lifecycle：PASS。
- MP-1 Advisor：受控 Smoke PASS。
- MP-2 正式 Safe Execute：显式 `safe-execute` token 的一动作边界 Host/Client Smoke 已 PASS；默认多人仍保持 Probe，MP-2B 及更宽能力继续关闭。
- MP-2A：静态/合同 PASS；2026-09-20 的正式 Host/Client `safe-execute-lab` Smoke 已 PASS。该 Lab 证据不等于开放正式入口。
- MP-2B / Multiplayer Instant：BLOCKED。
- 最近已验证实现基线：`18accc1`；GitHub Actions `35485393314` 为 `9 PASS / 0 FAIL / 0 SKIP`。随后文档收尾 `62f51ea` 的 CI `35485507892` 也全绿。

## 项目规则已放宽

当前按“项目主持人 / Tech Lead”方式工作：

- 仓库内、可逆、非发布性决策默认由 Agent 自主执行。
- 可以修复相关相邻问题、重构、补测试/诊断、清理死文件和读取必要历史，不需要逐项审批。
- 失败后可以自行切换安全替代方案。
- 不再强制一个请求一个 commit、固定测试数量、固定 120 秒上限、每轮 handoff、每次 Markdown 输出或禁止读取历史目录。
- 只在产品方向、不可逆删除、外部账号/费用、正式发布、公开协议/数据格式或正式多人能力范围变化时需要额外确认。
- 保留安全与正确性硬边界：不泄密、不破坏用户数据、不强推/改写历史、不擅自发布、不用性能预算掩盖语义错误、多人实验能力不在缺少实机证据时直接转正式。

## 多人实机固定操作（新对话不要重新试错）

- 任何 Multiplayer Lab 运行前先读 `docs/multiplayer/RUNBOOK.md`。
- 同机 Host/Client Lab 必须关闭 Steam transport。启动脚本现已默认 `--force-steam=off`；命令仍建议显式写 `-ForceSteamOff`。只有专门测试 Steam transport 才用 `-AllowSteam`。
- `ClientRitsuOnly` / `ClientCombatSolver` 的第一次启动是 Mod 加载 warm-up：让游戏加载 Mod 并重启一次；**重启后的第二次启动才是正式 Host/Join/Smoke 运行**。warm-up 不能作为 multiplayer evidence。
- Client snapshot 被重新准备/重建、Mod payload 被替换，或游戏再次要求重启时，重新做 warm-up。
- 正式测试结束继续使用 Graceful stop；不要用强杀运行当完整 journal 证据。

## 当前实验职责

- 游戏内所有 GUI 操作交给用户完成；Codex/Agent 不再尝试代操作游戏。
- Codex/Agent 负责把环境准备到可点击状态，并在用户每完成一步后读取日志/结果继续判断。
- 后续 MP-2A Smoke 应按 RUNBOOK 分成短步骤交给用户执行，而不是由 Codex 长时间尝试自动完成。

## 本轮 MP-2A 实机证据（2026-09-20）

- 已按远程 `d48065b` 的 Runbook 构建当前 Release，并使用 `HostVanilla + ClientCombatSolver`、Steam transport off、Mod warm-up 后第二次正式 Client 运行。
- Host/Client 进入同一房间和战斗：MapCoord `(3,0)`、Encounter `NIBBITS_WEAK`、Seed `EJEBT4G7Y6`。用户确认点击前没有自动出牌。
- 用户只点击一次“执行本回合”：原生 `PlayCardAction` 打出 `STRIKE_IRONCLAD`；能量 `3 -> 2`、手牌 `5 -> 4`、弃牌堆增加 1 张，敌方生命 `96 -> 90`，UI 进入“等待下一回合”。
- `validate-mp2a-results.ps1` 返回 `MULTIPLAYER_MP-2A_PASS`，7 项检查全部 PASS：Safe Execute capability、单动作部署、原生 PlayCardAction、无自动 EndTurn/药水、动作后 WorldVersion 失效和新的 debounce search。审计摘要位于 `.local/multiplayer-lab/results/mp2a-summary-20260920-user.json`；原始 CombatSolver journal 位于本轮 Client 的 `diagnostics/CombatSolver-BugReports/logs/CombatSolver/` 下。
- 两个隔离实例均使用默认 `Graceful` 停止。以上 Lab 证据与本轮正式 token 证据均只收口单张本地普通牌边界；正式 token 运行的机器摘要见 [`mp2-safe-execute-formal-2026-09-20.json`](multiplayer/evidence/mp2-safe-execute-formal-2026-09-20.json)。
- 正式 token 运行使用 Seed `QPMEQDJ5AQ`、Encounter `NIBBITS_WEAK`；一次 `DEFEND_IRONCLAD` 原生 `PlayCardAction` 完成后，Probe 观察到能量 `3 -> 2`、手牌 `5 -> 4`、弃牌堆增加该牌。验证器返回 `MULTIPLAYER_MP-2A_PASS`，7 项检查全部 PASS。

## 当前下一步

- MP-2 正式 Safe Execute 的显式一动作边界已收口为 PASS；下一阶段如继续，应另立 MP-2B 多动作/回合边界目标，不扩大本轮能力范围。

继续保持默认 Probe，以及 MP-2B、Multiplayer Instant、自动 EndTurn、Potion、Choice 和 Full Auto 关闭。
