# `tools/` 目录分类

本文是工具入口分类和维护边界，不是删除清单。所有目录先保留；归档或删除前必须先审计引用、调用方和证据价值。

## ACTIVE

当前任务链依赖的入口包括：

- `run-unattended-test.ps1/.sh`、`run-headless-matrix.ps1/.sh`、`headless-runtime.ps1/.sh`：无人测试、矩阵调度和实例生命周期。
- `run-visible-steam-benchmark.ps1/.sh`、`CompatibilitySmoke/`、`CoverageCatalog/`、`CheckpointTool/`、`GeneratedCombatScenarios/`：可见/兼容/覆盖/检查点和场景验证。
- `verify-refactor-boundaries.ps1/.sh`、`verify-target-version.ps1/.sh`、`build-local-stack.ps1/.sh`：结构、版本和本地构建入口。

## EXPERIMENTAL

名称带有 `Experimental`、`Prototype`、`Research` 或 `Candidate` 的目录（例如 `ExperimentalAdaptiveGc/`、`ChoiceContinuationPrototype/`、`BfwsResearchChecks/`、`PerformanceCandidateProbes/`）按实验工具处理。它们可以被专题任务显式调用，但不属于默认生产门禁或运行时依赖。

## ARCHIVED

当前没有完成引用审计、可以安全移出的目录。旧工具即使暂时没有调用方，也先留在原路径，待单独任务确认归档位置、恢复方式和证据保留期限后再处理。
