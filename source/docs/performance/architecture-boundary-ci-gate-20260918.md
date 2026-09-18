# GitHub 架构边界门禁

日期：2026-09-18
目标：CombatSolver `0.40.2` / Slay the Spire 2 `0.107.1`

## 范围

- 在 `.github/workflows/compatibility.yml` 的 Windows `static-consistency` Job 中加入 `source/tools/verify-refactor-boundaries.ps1`。
- 持续集成现在与目标版本门禁、manifest/project XML 校验和 `git diff --check` 一起阻止已登记的 Search、Runtime、Testing、UI、partial ownership、Retention 协调及兼容边界回退。
- 只改变 CI 覆盖范围，不改变生产程序集、搜索行为、运行时调度或测试输入。

## 验证

- 本地 PowerShell 架构门禁：通过，`REFACTOR_BOUNDARIES_OK search_files=122`。
- 目标版本门禁：通过，`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- Release 构建：通过，0 errors；保留 2 个既有 CS9113 warning。
- `git diff --check`：通过；仅报告 workflow 文件与 PowerShell 门禁脚本的既有 CRLF 提示。
- 最终 Release DLL：已复制到 `D:\yingye\MODDEV\ports\upstream-0.107.1\release-0.107.1\CombatSolver.dll`，大小 4,134,912 bytes，时间 2026-09-18 20:42:15。

GitHub Actions 远端执行结果需由后续 push 触发的 workflow 提供；本地门禁通过不替代远端 Job 结果。
