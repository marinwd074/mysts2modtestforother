# Entry 补丁注册职责拆分

日期：2026-09-18
目标：CombatSolver `0.40.2` / Slay the Spire 2 `0.107.1`

## 范围

- 将 `Entry.Initialize` 中的 RitsuLib patcher 创建、完整补丁注册清单和 `ApplyRequiredPatcher` 调用移至 `src/Runtime/PatchRegistration.cs`。
- `Entry` 保留原有初始化、生命周期订阅、启动日志和 `DisableMod` 失败回调；新文件保持原补丁注册顺序及 0.107.1/旧目标条件编译。
- 未引入 BootstrapService、接口、DI、Service Locator、补丁重排或运行时行为变化。

## 验证

- Release 构建：通过，0 errors；保留 2 个既有 CS9113 warning。
- CompatibilitySmoke 构建：通过，0 errors；保留相同 2 个既有 CS9113 warning。
- 结构门禁：`REFACTOR_BOUNDARIES_OK search_files=122`。
- 目标版本门禁：`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- `git diff --check`：通过；仅报告 PowerShell 门禁脚本的既有 CRLF 提示。
- 最终 Release DLL：已复制到 `D:\yingye\MODDEV\ports\upstream-0.107.1\release-0.107.1\CombatSolver.dll`，大小 4,134,912 bytes，时间 2026-09-18 20:46:33。

本批次只整理启动入口职责；可见游戏验收仍由用户使用输出 DLL 完成。
