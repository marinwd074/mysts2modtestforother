# 文档历史索引

本目录用于放置不定义当前实现的历史审计、重构过程和归档说明。当前文档入口只保留导航、现行约束和最近批次摘要，完整时间线按主题归档。

当前事实只从以下入口读取：

- [`source/AGENTS.md`](../../AGENTS.md)：仓库工作规则、版本基线和长期硬约束。
- [`ARCHITECTURE.md`](../ARCHITECTURE.md)：当前职责、依赖方向、状态所有权和关键不变量。
- [`DEVELOPMENT_NOTES.md`](../DEVELOPMENT_NOTES.md)：最近开发批次与维护边界。
- [`TEST_MATRIX.md`](../TEST_MATRIX.md)：当前测试入口、覆盖范围和重跑约定。

2026-09-19 收口时，旧版完整架构、开发笔记和测试矩阵快照已从当前树移除，不再作为活跃文档入口。它们仍可从收口前提交恢复，例如：

```powershell
git show 37e7256:source/docs/history/architecture/ARCHITECTURE-2026-09-19.md
git show 37e7256:source/docs/history/development/DEVELOPMENT_NOTES-2026-09-19.md
git show 37e7256:source/docs/history/testing/TEST_MATRIX-2026-09-19.md
```

后续历史材料应按专题放入本目录，并在这里登记用途、版本和恢复方式；当前结论只能来自上面的活跃文档入口。

`refactoring/`、`performance/`、`compat/`、`issues/` 和本目录中的报告不自动升级为当前结论。引用旧版本、旧分支、旧 PR 或旧运行环境的条目只用于追溯当时证据；若与当前源码冲突，以当前源码和上述入口为准。
