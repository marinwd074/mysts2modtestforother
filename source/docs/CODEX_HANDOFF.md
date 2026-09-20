# Codex 当前交接

## 当前技术状态

- CombatSolver `0.40.2`；目标游戏 / RitsuLib `0.107.1`；兼容符号 `STS2_01071`。
- MP-0 Core / lifecycle：PASS。
- MP-1 Advisor：受控 Smoke PASS。
- MP-2 正式 Safe Execute：BLOCKED。
- MP-2A：静态/合同 PASS，真实行为已有支持证据，但正式 Host/Client 事件链仍为 UNVERIFIED。
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

## 当前下一步

重新做一轮新的 MP-2A Host + Client Smoke：

1. 当前源码 Release build。
2. Vanilla Host + ClientCombatSolver。
3. Client 使用 `safe-execute-lab`。
4. 未点击时确认不会自动出牌。
5. 只点击一次“执行本回合”。
6. 等待 PlayCardAction、WorldVersion 更新和新搜索。
7. 使用默认 Graceful 停止，不使用 `-Mode Force`。
8. 用 `validate-mp2a-results.ps1` 验证完整 journal。
9. 只有全部 PASS 后才讨论正式 Safe Execute 入口。

不要在这一步之前开放 MP-2B、Multiplayer Instant、自动 EndTurn、Potion、Choice 或 Full Auto。
