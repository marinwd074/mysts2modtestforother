# 适配验证记录（历史入口）

> 本文件不再承载当前版本的长篇验证日志；它只保留兼容性证据入口。

当前 `0.107.1` 事实请以以下文档为准：

- [0.107.1 兼容证据链](compat/0.107.1/README.md)
- [战斗 Hook 覆盖报告](COMBAT_HOOK_COVERAGE.md)
- [当前测试矩阵](TEST_MATRIX.md)
- [多人 Phase 0 机器矩阵](multiplayer/evidence/phase0-matrix-2026-09-19.json)

原文件中的 `0.111.0` / `0.13.x` 适配日志已从当前入口退役。需要逐项追溯时，可在收口前提交 `37e7256` 上执行：

```powershell
git show 37e7256:source/docs/ADAPTATION_VERIFICATION.md
```

后续新增验证应写入对应版本或专题文档，并在 `TEST_MATRIX.md` 登记重跑入口和已知缺口；不要把历史日志复制回本文件。
