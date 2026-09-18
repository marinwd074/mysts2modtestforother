# 文档事实瘦身与构建产物清理

日期：2026-09-18
目标：CombatSolver `0.40.2` / Slay the Spire 2 `0.107.1`

## 变更

- 删除 `source/AGENTS.md` 顶部已完成批次的当前工作项，避免把历史批次当作下一项任务指令。
- 更新 `source/docs/ARCHITECTURE.md`：`Entry.cs` 只负责 Mod/生命周期入口，`PatchRegistration.cs` 负责 RitsuLib 补丁创建、注册、应用及顺序；明确 Harmony 原生参数形状的条件编译是当前兼容边界。
- 更新 `DEVELOPMENT_NOTES.md` 与 `TEST_MATRIX.md` 的顶部范围声明，移除测试矩阵重复的 Batch 8 标题；新增 `source/docs/history/README.md`，区分当前事实入口与历史证据。

## 验证

- Release：通过，0 errors；保留 2 个既有 `CS9113` warning。
- 结构门禁：`REFACTOR_BOUNDARIES_OK search_files=122`。
- 目标版本门禁：`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- `git diff --check`：通过。
- 本批次只改文档和清理可重建产物，不重复游戏行为 smoke。

## 清理

删除以下工具项目的 `bin/obj`：`CardHookReceiverChecks`、`ClientUpdateChecks`、`CombatSolver.MemoryCleaner`、`InspectGame`、`PredictionStateStoreChecks`、`StateFingerprintChecks`、`TurnPhaseMirrorChecks`，共 14 个目录，约 50.5 MB。它们都是可由下一次构建恢复的中间/工具输出。

保留游戏本体及 `MODS`、`.combatsolver-precombat`、`output_lines` 等活动运行数据；保留 `source/.godot`、`source/.local` 和被 `RAW_DATA_ARCHIVE.md` 或历史矩阵引用的性能 JSON。最终 DLL：

`D:\yingye\MODDEV\ports\upstream-0.107.1\release-0.107.1\CombatSolver.dll`
大小：4,134,912 bytes
