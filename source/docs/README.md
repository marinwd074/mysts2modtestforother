# CombatSolver 文档导航

当前工作树只保留**长期真源、当前计划和可重跑合同**。历史实验、运行日志、旧阶段报告和问题包由 Git history 或本地实验目录保存，不进入默认上下文。

## 默认入口

| 目的 | 文件 |
|---|---|
| 当前状态 / 下一任务 | [CODEX_HANDOFF.md](CODEX_HANDOFF.md) |
| 当前质量优先计划 | [CombatSolver_Quality_First_Next.md](CombatSolver_Quality_First_Next.md) |
| 多人本地核心搜索效率任务书 | [Multiplayer_LocalCore_Search_Optimization.md](Multiplayer_LocalCore_Search_Optimization.md) |
| 多人总体架构计划 | [CombatSolver_GPT_Architecture_Plan.md](CombatSolver_GPT_Architecture_Plan.md) |
| 架构、职责、状态所有权 | [ARCHITECTURE.md](ARCHITECTURE.md) |
| 当前验证入口 | [TEST_MATRIX.md](TEST_MATRIX.md) |
| 0.107.1 兼容边界 | [compat/0.107.1/README.md](compat/0.107.1/README.md) |
| 多人架构与当前边界 | [multiplayer/README.md](multiplayer/README.md) |
| 多人 Host/Client 操作 | [multiplayer/RUNBOOK.md](multiplayer/RUNBOOK.md) |
| 多人已知限制 | [multiplayer/LIMITATIONS.md](multiplayer/LIMITATIONS.md) |
| 测试分层 | [TESTING_LAYERS.md](TESTING_LAYERS.md) |
| 性能护栏 | [PERFORMANCE_GUARDRAILS.md](PERFORMANCE_GUARDRAILS.md) |
| 仓库保留/清理规则 | [REPOSITORY_MAINTENANCE.md](REPOSITORY_MAINTENANCE.md) |
| 本 fork 版本演进 | [PROJECT_VERSION_HISTORY.md](PROJECT_VERSION_HISTORY.md) |
| 上游同步策略 | [../../UPSTREAM.md](../../UPSTREAM.md) |
| 贡献与 PR 规则 | [../../CONTRIBUTING.md](../../CONTRIBUTING.md) |

## 专题资料

按具体任务定向读取，不要整批加载：

- `compat/0.107.1/`：固定版本机制、卡牌、Hook 与怪物目标审计。
- `refactoring/`：仍有效的重构路线。
- `workshop/`：发布文案。
- `third-party-*.md` / `THIRD_PARTY_ADAPTERS.md`：第三方适配长期规范。
- `HEADLESS_TESTING.md`、`OFFLINE_SEARCH_HARNESS.md`、`CHECKPOINT_REPLAY.md`：可重跑测试入口。

## 不进入当前树

以下内容只保存在 Git history、`.local/`、外部问题包或用户明确提供的附件中：

- dated audit / issue / PR review；
- U0–U6、P0–P3 等已完成阶段过程文档；
- runtime evidence、原始日志、bug-report ZIP；
- 一次性 performance / strategy 报告和机器结果；
- prototype 生成的 `results*.json`；
- 被当前计划替代的 NEXT 文档。

需要历史事实时按 commit 或问题包定向恢复，不把整段历史重新复制进 handoff。

## 维护原则

- `CODEX_HANDOFF.md` 只保留当前状态、当前风险和下一任务。
- 当前计划只保留仍指导生产决策的计划文件。
- 新文档必须有长期用途，或被本索引/稳定专题索引引用。
- 可重跑事实优先固化为测试/fixture，不用日志证明长期正确性。
- 清理规则见 [REPOSITORY_MAINTENANCE.md](REPOSITORY_MAINTENANCE.md)。
