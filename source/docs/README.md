# CombatSolver 文档导航

通用随机或指定战斗测试入口见[场景生成与批量重跑](GENERATED_COMBAT_SCENARIOS.md)。玩家安装、操作与兼容性说明见[项目 README](../README.md)，源码规则见[AGENTS.md](../AGENTS.md)。

## 当前入口

| 要查什么 | 入口 |
|---|---|
| 当前职责、依赖方向、状态所有权和禁止边界 | [架构与职责地图](ARCHITECTURE.md) |
| 逻辑、集成、原生测试层级与停止条件 | [测试分层与入口](TESTING_LAYERS.md) |
| 当前性能阈值、固定工作量和禁止的测试捷径 | [性能与质量护栏](PERFORMANCE_GUARDRAILS.md) |
| STS2 `0.107.1` 兼容证据与限制 | [兼容证据链](compat/0.107.1/README.md) |
| 第三方 Mod 登记点、状态合同和验收 | [第三方 Mod 适配手册](THIRD_PARTY_ADAPTERS.md) |
| 玩家问题包、检查点恢复和录制回放 | [检查点回放](CHECKPOINT_REPLAY.md) |
| 隔离游戏实例、请求协议和资源准入 | [无头测试](HEADLESS_TESTING.md) |
| 不启动 Godot 的搜索指标宿主 | [离线搜索宿主](OFFLINE_SEARCH_HARNESS.md) |
| 当前开发批次、未发布改动和已知限制 | [开发笔记](DEVELOPMENT_NOTES.md) |
| 当前测试目录、重跑方式和未验证范围 | [测试矩阵](TEST_MATRIX.md) |
| 工具生成的 Hook 覆盖固定入口 | [战斗 Hook 覆盖目录](COMBAT_HOOK_COVERAGE.md) |

## 专题目录

专题目录负责承接 dated 报告、历史证据和批次索引；总入口不再逐项列出这些报告。

| 目录 | 内容 |
|---|---|
| [releases/](releases/README.md) | 玩家更新日志与版本草案 |
| [issues/](issues/README.md) | 玩家问题包、分诊和修复交接 |
| [performance/](performance/README.md) | 性能实验、复现方法和历史数据 |
| [refactoring/](refactoring/README.md) | 重构路线和核验记录 |
| [pr/](pr/README.md) | PR 审查、集成修正和验证记录 |
| [strategy/](strategy/README.md) | 策略需求、搜索研究和优化记录 |
| [audits/](audits/README.md) | 历史仓库、架构和 UI 审计 |
| [history/](history/README.md) | 已切档的历史开发与测试资料 |

## 维护约定

- 当前源码、当前入口文档和可重跑证据优先于历史报告；历史报告不是当前任务指令或当前测试成绩。
- 新增当前专题入口时更新本页或对应专题 README；移动文件时同步 Markdown 链接、脚本和结构化证据中的路径。
- `DEVELOPMENT_NOTES.md` 只记录当前开发批次和重要结论；`TEST_MATRIX.md` 只维护当前测试目录与覆盖范围，不追加每次重复运行的流水账。
- 纯文件移动、partial 拆分、CI gate 和文档整理不自动新建 performance report，也不自动复制 DLL 或生成 runtime evidence。
- 工具生成的固定入口由对应工具维护；新增报告应先放入专题目录并从专题 README 建立索引。
