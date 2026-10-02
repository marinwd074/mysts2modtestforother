# 工具与验证入口

命令默认从仓库根执行；工具项目中的 `tools/...` 示例则从 `source/` 执行。先按改动边界选择入口，完整合同、pinned 矩阵和实机验证按需运行。

## 日常维护

| 目的 | 入口 | 范围 |
|---|---|---|
| 固定版本一致性 | [verify-target-version.ps1](verify-target-version.ps1) / [Linux](verify-target-version.sh) | 游戏、RitsuLib、条件编译与构建目标 |
| 架构职责 | [verify-refactor-boundaries.ps1](verify-refactor-boundaries.ps1) / [Linux](verify-refactor-boundaries.sh) | Search / Runtime / UI / Testing 边界 |
| 仓库与文档 | [verify-repository-hygiene.ps1](verify-repository-hygiene.ps1) | 跟踪文件、LFS、文档本地链接、项目版本日志 |
| 本地构建 | [build-local-stack.ps1](build-local-stack.ps1) / [Linux](build-local-stack.sh) | Release 与工具构建；不自动启动游戏 |
| 项目版本 | [new-project-version.ps1](new-project-version.ps1) | `-Check` 校验；`-Kind Minor/Major` 记录功能更新 |
| 已编排的 L1 合同 | [run-contract-tests.ps1](run-contract-tests.ps1) | 纯合同与证据验证器，不替代 native/Host/Client |
| 本地完整门禁 | [run-ci-gates.ps1](run-ci-gates.ps1) | 三项快速检查、L1 合同与 diff 检查 |

快速检查示例：

```powershell
pwsh -NoProfile -File source/tools/verify-repository-hygiene.ps1
```

## 按边界验证

| 目的 | 项目或入口 | 使用说明 |
|---|---|---|
| 固定游戏语义与搜索 | [E0PinnedHarness/](E0PinnedHarness/)、[U0U1PinnedHarness/](U0U1PinnedHarness/)、[U2DegenerateHarness/](U2DegenerateHarness/)、[P0P1PinnedHarness/](P0P1PinnedHarness/) | 选择相关模式；[测试矩阵](../docs/TEST_MATRIX.md) 是模式与证据入口 |
| GC / 释放 / 并发合同 | [CombatSolver.GcPolicyChecks/](CombatSolver.GcPolicyChecks/README.md)、[CombatSolver.WavePolicyChecks/](CombatSolver.WavePolicyChecks/) | 纯状态机与真实 CLR 模式分开，真实 NoGC 需要足够内存 |
| 原生兼容与覆盖 | [CompatibilitySmoke/](CompatibilitySmoke/)、[CoverageCatalog/](CoverageCatalog/)、[Sts2LocalInspector/](Sts2LocalInspector/README.md) | 固定 0.107.1 依赖；IL 审计按需运行 |
| 离线 / 检查点 / 生成场景 | [OfflineSearchHarness/](OfflineSearchHarness/)、[CheckpointTool/](CheckpointTool/)、[GeneratedCombatScenarios/](GeneratedCombatScenarios/) | [离线说明](../docs/OFFLINE_SEARCH_HARNESS.md)、[检查点说明](../docs/CHECKPOINT_REPLAY.md)、[场景说明](../docs/GENERATED_COMBAT_SCENARIOS.md) |
| 多人实机与证据 | [multiplayer-lab/](multiplayer-lab/README.md) | 遵循 [Runbook](../docs/multiplayer/RUNBOOK.md)，只管理隔离实例 |
| 无人 / 可见测试 | [run-unattended-test.ps1](run-unattended-test.ps1)、[run-headless-matrix.ps1](run-headless-matrix.ps1)、[run-visible-steam-benchmark.ps1](run-visible-steam-benchmark.ps1) | `.sh` 为对应 Linux 入口；实例生命周期由 `headless-runtime.ps1/.sh` 管理 |

其余 `*Checks/`、`*Tests/` 为对应生产边界的定向合同，直接运行项目；已编排的部分以 `run-contract-tests.ps1` 为准，新增合同不会自动进入默认 CI。

## 诊断与研究

- [InspectGame/](InspectGame/)、[ChoiceSourceAudit/](ChoiceSourceAudit/README.md)：按需检查游戏结构与选牌来源。
- [GcTraceAnalysis/](GcTraceAnalysis/README.md)、[PerformanceBenchmarks/](PerformanceBenchmarks/)、[watch-performance.ps1](watch-performance.ps1)、[export-performance.ps1](export-performance.ps1)：局部基准与用户开启的性能采集；输出放 `.local/`。
- [BfwsResearchChecks/](BfwsResearchChecks/README.md)：已有实验搜索辅助结构的合同，已被 L1 调用；合同覆盖不表示研究算法默认启用。
- [ForkableListLayoutChecks/](ForkableListLayoutChecks/README.md)：当前生产集合的合同，已撤回候选的生成器不再保留。

默认 GitHub push/PR 只执行快速结构、版本和仓库检查。较重入口见 [工作流说明](../../.github/workflows/README.md)；正式包只由 `v*` 发布工作流产生。

## 退役规则

先审计源码、项目引用、脚本、工作流和文档，再删除无调用方的实验。采用或撤回结论进入当前规范；旧原型和过程报告从 Git history 恢复，不再建立 archive 目录。具体规则与参考项目见 [仓库维护](../docs/REPOSITORY_MAINTENANCE.md)。
