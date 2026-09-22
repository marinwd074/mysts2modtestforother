# 性能研究资料

本目录是**历史/按需上下文**，普通开发不要整目录读取。

当前性能规则见 [../PERFORMANCE_GUARDRAILS.md](../PERFORMANCE_GUARDRAILS.md)，当前状态见 [../CODEX_HANDOFF.md](../CODEX_HANDOFF.md)。

- Markdown 报告只描述当时版本与测量条件，不自动代表当前实现。
- 生成型 raw JSON / patch 不再作为当前树的长期真源；需要旧原始数据时使用 Git history 或既有外部归档。
- 新性能工作优先把可重跑合同写进工具/测试，把一次性测量留在 `.local/`。
- 只有长期有效的测量方法或性能护栏才应回写当前文档。
