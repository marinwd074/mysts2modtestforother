# AfterBlockBroken Hook 兼容边界

日期：2026-09-18
目标：CombatSolver `0.40.2` / Slay the Spire 2 `0.107.1`

## 范围

- 新增静态 `Sts2HookCompatibility`，集中 `AfterBlockBroken` 在 0.107.1 与旧目标之间的原生参数列表。
- `AfterBlockBrokenMirrors` 只保留稳定的镜像注册和预测语义，消费兼容 helper；不改变注册顺序、模型处理、预测效果或 per-node 路径。
- 未引入接口、服务、运行时反射或额外分配路径。

## 验证

- Release 构建：通过，0 errors；保留 2 个既有 CS9113 warning。
- CompatibilitySmoke 构建：通过，0 errors；保留相同 2 个既有 CS9113 warning。
- 结构门禁：`REFACTOR_BOUNDARIES_OK search_files=122`。
- 目标版本门禁：`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- `git diff --check`：通过；仅报告 PowerShell 门禁脚本的既有 CRLF 提示。
- 最终 Release DLL：已复制到 `D:\yingye\MODDEV\ports\upstream-0.107.1\release-0.107.1\CombatSolver.dll`，大小 4,134,912 bytes，时间 2026-09-18 20:30:26。

本批次只整理版本 API shape，不新增行为结论；可见游戏验收仍由用户使用输出 DLL 完成。
