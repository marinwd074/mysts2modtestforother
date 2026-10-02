# CombatSolver 文档导航

当前工作树把**当前任务**与**已完成实现说明**分开：未来工作只进 handoff；旧计划文件只解释现有代码。

## 默认入口

| 目的 | 文件 |
|---|---|
| 当前状态 / 未完成风险 / 下一任务 | [CODEX_HANDOFF.md](CODEX_HANDOFF.md) |
| 架构、职责、状态所有权 | [ARCHITECTURE.md](ARCHITECTURE.md) |
| 当前验证入口 | [TEST_MATRIX.md](TEST_MATRIX.md) |
| 项目版本与功能更新 | [PROJECT_VERSION](../PROJECT_VERSION) · [PROJECT_VERSION_HISTORY.md](PROJECT_VERSION_HISTORY.md) |
| 多人 Host/Client 操作 | [multiplayer/RUNBOOK.md](multiplayer/RUNBOOK.md) |

## 已完成实现档案

| 主题 | 文件 | 现在记录什么 |
|---|---|---|
| Rolling Horizon / R0 / R1 / 新根 replay / foreground | [Rolling_Horizon_Reuse_Architecture.md](Rolling_Horizon_Reuse_Architecture.md) | 做了什么、函数位置、Runtime/Search 调用链、复用边界 |
| local-single-core 搜索效率 P0–P4 | [Multiplayer_LocalCore_Search_Optimization.md](Multiplayer_LocalCore_Search_Optimization.md) | 候选保留、bounded refresh、seed、incumbent、Smart 调度、热点优化 |
| 多人出牌质量修复 | [CombatSolver_Quality_First_Next.md](CombatSolver_Quality_First_Next.md) | 药水、current-turn、排序、斩杀窗口、Safe Execute 等已落地修复 |
| 单多人总体架构 / U0–U6 / 搜索加速状态 | [CombatSolver_GPT_Architecture_Plan.md](CombatSolver_GPT_Architecture_Plan.md) | 已实现架构和专项当前落点；不再保存执行卡 |
| Rolling Horizon 代码审查 | [Rolling_Horizon_Reuse_Code_Review.md](Rolling_Horizon_Reuse_Code_Review.md) | 已修复问题与可重跑审查入口 |

## 稳定专题

- [multiplayer/README.md](multiplayer/README.md)：多人当前架构与能力边界。
- [PERFORMANCE_GUARDRAILS.md](PERFORMANCE_GUARDRAILS.md)：性能优化不可越过的质量护栏。
- [TESTING_LAYERS.md](TESTING_LAYERS.md)：测试证据分层。
- [OFFLINE_SEARCH_HARNESS.md](OFFLINE_SEARCH_HARNESS.md)、[CHECKPOINT_REPLAY.md](CHECKPOINT_REPLAY.md)：可重跑离线/恢复入口。
- [HEADLESS_TESTING.md](HEADLESS_TESTING.md)、[GENERATED_COMBAT_SCENARIOS.md](GENERATED_COMBAT_SCENARIOS.md)：隔离无人测试与可重复场景。
- [BUG_REPORT_PROTOCOL.md](BUG_REPORT_PROTOCOL.md)、[COMBAT_HOOK_COVERAGE.md](COMBAT_HOOK_COVERAGE.md)：问题包协议与 Hook 覆盖。
- [compat/0.107.1/README.md](compat/0.107.1/README.md)：固定版本语义。
- [THIRD_PARTY_ADAPTERS.md](THIRD_PARTY_ADAPTERS.md)：第三方适配入口；专题为 [模型状态](third-party-model-state.md)、[OnPlay](third-party-onplay-patches.md)、[回合阶段](third-party-turn-phase-mirrors.md)、[策略效果](third-party-strategic-effects.md)。
- [REPOSITORY_MAINTENANCE.md](REPOSITORY_MAINTENANCE.md)：仓库保留/清理规则。
- [工具任务表](../tools/README.md)、[Workshop 文案](workshop/README.md)：构建/验证入口与发布素材。

## 维护规则

- `CODEX_HANDOFF.md` 可以写“还要做什么”；实现档案不写下一阶段任务。
- 实现档案统一回答四件事：**做了什么、有什么用、代码在哪里、调用链怎么走**。
- 阶段名 P/U/A–H 只作为 Git 历史索引，不再代表自动执行顺序。
- 未进入生产的旧设想只记录“未实现/未启用”这一事实，不保留施工步骤。
- dated audit、原始日志、问题包和一次性 benchmark 由 Git history 或 `.local/` 保存，不重新堆进默认文档。

<!-- repository-maintenance-marker: 2026-10-02 -->
