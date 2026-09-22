# CombatSolver 文档导航

本页只列**当前真源**。历史研究和 dated 报告不属于默认上下文，需要时按具体问题定向读取或从 Git history 恢复。

## 默认入口

| 目的 | 文件 |
|---|---|
| 当前状态 / 下一步 | [CODEX_HANDOFF.md](CODEX_HANDOFF.md) |
| 本 fork 版本演进 | [PROJECT_VERSION_HISTORY.md](PROJECT_VERSION_HISTORY.md) |
| 架构、职责、状态所有权 | [ARCHITECTURE.md](ARCHITECTURE.md) |
| 当前验证入口 / 能力状态 | [TEST_MATRIX.md](TEST_MATRIX.md) |
| 当前开发方向 | [DEVELOPMENT_NOTES.md](DEVELOPMENT_NOTES.md) |
| 0.107.1 兼容边界 | [compat/0.107.1/README.md](compat/0.107.1/README.md) |
| 多人真实 Host/Client 操作 | [multiplayer/RUNBOOK.md](multiplayer/RUNBOOK.md) |
| 测试分层 | [TESTING_LAYERS.md](TESTING_LAYERS.md) |
| 性能护栏 | [PERFORMANCE_GUARDRAILS.md](PERFORMANCE_GUARDRAILS.md) |

## 按需资料

以下目录默认**不要整批加载进上下文**：`performance/`、`strategy/`、`audits/`、`issues/`、`releases/`、`history/`、`pr/`。它们用于特定回归、旧基线或历史审计，不覆盖当前源码和当前真源。

机器运行证据只在需要核验某个具体 PASS/FAIL 时读取对应文件；不要把 evidence 目录当项目说明书。

## 维护规则

- 当前状态只写进当前真源，不追加历史流水账。
- 已完成批次由 Git history 保存；不要复制到 handoff/test matrix。
- 新报告若只是一次性实验，优先留在 `.local/`；只有长期可重跑/可审计事实才进入仓库。
