# `tools/` 目录分类

本文是工具入口分类和维护边界，不是删除清单。所有目录先保留；归档或删除前必须先审计引用、调用方和证据价值。

## ACTIVE

当前任务链依赖的入口包括：

- `run-unattended-test.ps1/.sh`、`run-headless-matrix.ps1/.sh`、`headless-runtime.ps1/.sh`：无人测试、矩阵调度和实例生命周期。
- `run-visible-steam-benchmark.ps1/.sh`、`CompatibilitySmoke/`、`CoverageCatalog/`、`CheckpointTool/`、`GeneratedCombatScenarios/`：可见/兼容/覆盖/检查点和场景验证。
- `verify-refactor-boundaries.ps1/.sh`、`verify-target-version.ps1/.sh`、`build-local-stack.ps1/.sh`：结构、版本和本地构建入口。

## REGRESSION

需要随版本回归但不参与默认门禁的入口，包括 `multiplayer-lab/`、兼容 Smoke、覆盖目录和性能基准。它们必须保留可重跑说明和已知边界；回归结论不能自动改变当前矩阵。

## RESEARCH

仍被当前研究/回归任务使用的目录（例如 `BfwsResearchChecks/`、`PerformanceCandidateProbes/`）按研究工具处理，不属于生产运行时。已经给出“不采用”结论、固定到旧游戏/旧源码或只保留历史补丁的 Experimental/Prototype 工具从当前工作树移除，由 Git history 负责恢复；若其 helper 仍被合同测试使用，则移动到对应测试项目内。

## ARCHIVED

完成引用审计、确认无默认门禁和无当前证据依赖后，过期研究工具直接从当前工作树移除，不建立第二套 archive 目录。恢复路径为 Git history。
