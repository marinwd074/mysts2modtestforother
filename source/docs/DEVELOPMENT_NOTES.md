# CombatSolver 当前开发笔记

> 本文件不记录已完成批次流水账；历史由 Git 保存。

## 当前目标

1. 保持 pinned 0.107.1 单人求解正确性和跨回合稳定性。
2. 继续多人适配，但只在真实证据通过后扩大 Safe Execute。
3. 降低运行时 / 构建 / 上下文开销，不用更高搜索预算掩盖问题。

## 当前优先级

- **P0 正确性**：新的问题包、search setup failure、actual/simulated mismatch。
- **P1 Multiplayer**：先验证 GM Console 多端同步，再补 MultiplayerOnly / Tag Team 实机证据。
- **P2 性能**：减少主线程 I/O、无用诊断、重复快照/反射和项目扫描；性能修改必须保持结果身份/工作量合同。
- **P3 结构**：Runtime / Search / Engine / Prediction / UI / Testing 保持单向职责；test-only 工具不得渗入正式程序集。

## 当前性能 / 仓库治理

- `src/Testing/**` 不进入正式 Release。
- `tools/**`、`docs/**`、`.local/**`、`outputs/**` 不参与主项目默认 SDK item discovery；需要的工具项目显式构建。
- Godot 游戏日志只在导出问题包时镜像，避免 Mod 初始化同步复制日志。
- 默认上下文只加载 handoff + 任务相关文件；dated performance/strategy/audit 文档按需读取。
- 一次性性能原始数据放 `.local/`；当前树不继续累积生成型 JSON/patch。

## 不做

- 不扩大 Beam/时间/内存来“修”错误路线。
- 不读取/控制队友私有状态或动作。
- 不把 test-only GM Console 编进 CombatSolver 正式包。
- 不把历史 PASS、旧 commit 或 dated 报告当当前事实。
