# CombatSolver 2026-09-18 交接

## 接手入口

下一次对话先读取本文件、仓库根目录 `AGENTS.md` 和 `source/AGENTS.md`。当前基线为 CombatSolver `0.40.2`、游戏 `0.107.1`、兼容符号 `STS2_01071`，分支为 `main`。

本轮处理的问题包：

- 文件：`D:\yingye\CombatSolver-BugReports\CombatSolver-0.40.2-KNIGHTS_ELITE-8d38ac26eec24905a802cb92a1556c75.zip`
- ID：`8d38ac26eec24905a802cb92a1556c75`
- 场景：`KNIGHTS_ELITE` 骑士团伙，DEFECT，Ascension 7，Act 3，Floor 45
- 首个差异：第 5 回合结束进入第 6 回合时，预测 `E0.hp=49`，实机 `E0.hp=47`；触发一次状态重算

## 已确认根因与修复

第 5 回合路线为 `Hotfix → Dualcast → Tempest → 结束回合`。`Hotfix` 使临时集中力从 1 变为 3；原生 0.107.1 会在普通 `AfterSideTurnEnd` 钩子（包括 `ConsumingShadowPower` 的末尾 Orb 唤起）执行完后才回收临时集中力。模拟器此前在普通回合结束钩子之前回收，导致末次 Lightning 唤起少 2 点伤害。

已修改：

- `source/src/Prediction/CorePowerSupport.cs`
- 将 `RestoreTemporaryFocus()` 从 `EndTurnPowerSupport.TriggerRegular(...)` 前移除，改为普通回合结束钩子成功后、`ResolvePowerAmountChanges(...)` 前执行
- 原生 `OrbQueue` 入队/驱逐顺序已核对，不是本问题根因

## 构建与发布状态

- Release 构建命令：`dotnet build source/CombatSolver.csproj -c Release --verbosity minimal`
- 构建结果：成功；仅有仓库已有的 2 条未读参数警告
- 已输出 DLL：`D:\yingye\MODDEV\ports\upstream-0.107.1\release-0.107.1\CombatSolver.dll`
- DLL 大小：`4,134,912` 字节
- Git 提交：`41aa472 fix: preserve temporary focus through end-turn hooks`
- 已推送：`origin/main`

## 未完成验证

- 尚未完成本问题包修复后的完整无头重放；此前无头搜索在资源受限环境中超时，不应写成“自动回放通过”
- 下一步由用户将目标 DLL 放入游戏环境并实机测试，重点观察骑士团伙第 5→6 回合是否仍出现状态重算，以及 `E0.hp` 是否与预测一致
- 若仍失败，保留新的问题包、完整 `InnerException`/状态差异和首个分叉回合；不要重复提交等价旧证据

## 清理记录

本轮已永久删除约 3.27 GB 的诊断解包目录、超时无头实例和 `InspectGame`/`MemoryCleaner` 临时 `bin/obj`。原始问题包、源码、Release 构建输出和目标 DLL均保留；删除的临时产物不可恢复，但工具缓存可重新构建。
