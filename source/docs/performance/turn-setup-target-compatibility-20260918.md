# 回合准备补丁目标参数兼容边界

日期：2026-09-18
目标：CombatSolver `0.40.2` / Slay the Spire 2 `0.107.1`

## 范围

- `Sts2TurnSetupCompatibility` 集中 `SetupPlayerTurn` 与 `RunAutoPrePlayPhase` 在 0.107.1 与旧目标之间的原生目标参数数组。
- `PlayerTurnSetupPatches` 继续保留 Harmony Prefix 必须使用原生参数形状的条件编译，但补丁目标声明不再解析旧版 `CombatTurnState` 或重复版本分支。
- 反射方法和调用参数沿用既有兼容 helper；未改变补丁注册顺序、回合准备调用、选择重放、搜索启动或部署行为。

## 验证

- Release 构建：通过，0 errors；保留 2 个既有 CS9113 warning。
- CompatibilitySmoke 构建：通过，0 errors；保留相同 2 个既有 CS9113 warning。
- 结构门禁：`REFACTOR_BOUNDARIES_OK search_files=122`。
- 目标版本门禁：`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- `git diff --check`：通过；仅报告 PowerShell 门禁脚本的既有 CRLF 提示。
- 最终 Release DLL：已复制到 `D:\yingye\MODDEV\ports\upstream-0.107.1\release-0.107.1\CombatSolver.dll`，大小 4,134,912 bytes，时间 2026-09-18 20:37:45。

本批次只收敛补丁目标 API shape，不新增行为结论；可见游戏验收仍由用户使用输出 DLL 完成。
