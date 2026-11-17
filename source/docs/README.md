# CombatSolver 文档导航

## 当前资料

| 目的 | 入口 |
|---|---|
| 接手项目、当前状态与下一步 | [当前交接](CODEX_HANDOFF.md) |
| 架构、职责与状态所有权 | [架构](ARCHITECTURE.md) · [搜索代码索引](../src/Search/) |
| 验证范围与工具 | [测试矩阵](TEST_MATRIX.md) · [证据分层](TESTING_LAYERS.md) · [工具索引](../tools/) |
| 搜索性能 | [质量与性能护栏](PERFORMANCE_GUARDRAILS.md) · [离线搜索宿主](OFFLINE_SEARCH_HARNESS.md) |
| 多人开发和验收 | [多人架构](multiplayer/README.md) · [能力边界](multiplayer/LIMITATIONS.md) · [实机清单](multiplayer/LIVE_ACCEPTANCE.md) · [操作手册](multiplayer/RUNBOOK.md) |
| 问题包与可重复用例 | [问题包协议](BUG_REPORT_PROTOCOL.md) · [检查点回放](CHECKPOINT_REPLAY.md) · [隔离测试](HEADLESS_TESTING.md) · [场景生成](GENERATED_COMBAT_SCENARIOS.md) |
| 游戏兼容与扩展 | [固定版本语义](compat/0.107.1/README.md) · [Hook 覆盖](COMBAT_HOOK_COVERAGE.md) · [第三方适配](THIRD_PARTY_ADAPTERS.md) · [遗物计数](relic-counters.md) |
| 仓库与版本 | [维护规则](REPOSITORY_MAINTENANCE.md) · [项目版本](../PROJECT_VERSION) · [更新记录](PROJECT_VERSION_HISTORY.md) · [Workshop 文案](workshop/README.md) |

## 历史资料

按需从整理前的提交 `bf63bf54506230f7dea041a9673dd29db93e3b10` 查阅。以下路径相对于 `source/docs/`。

| 主题 | 历史文件 |
|---|---|
| 单人 S0–S5 设计和性能证据 | `SOLO_SEARCH_OPTIMIZATION_PLAN.md`、旧版 `CODEX_HANDOFF.md` |
| 滚动复用、R0/R1 调用链与修复记录 | `Rolling_Horizon_Reuse_Architecture.md`、`Rolling_Horizon_Reuse_Code_Review.md` |
| 多人本地搜索、深度实验和路线质量 | `Multiplayer_LocalCore_Search_Optimization.md`、`CombatSolver_Quality_First_Next.md`、`multiplayer/LOCAL_SEARCH_PLAN.md` |
| U0–U6、战前预测和热点优化历史 | `CombatSolver_GPT_Architecture_Plan.md` |
| 2026-11-07 联机实机对照 | `multiplayer/LIVE_ACCEPTANCE_2026-11-07.md` |

在仓库根运行以下命令可查看旧交接。

```powershell
git show bf63bf54:source/docs/CODEX_HANDOFF.md
```
