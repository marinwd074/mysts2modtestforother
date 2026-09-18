# BeamRetentionPolicy Choice partial 拆分——Batch 8

## 范围

本轮按架构优化计划 P2-3 做纯结构拆分：将 `CombatBeamSolver.BeamRetentionPolicy` 中与路由／回合开始选择相关的辅助逻辑移动到 `CombatBeamSolver.BeamRetentionPolicy.Choice.cs`。保留 `CombatBeamSolver` 与嵌套 `BeamRetentionPolicy` 的 `partial` 结构，不新增接口、策略服务、仓储或依赖注入层。

移动的职责包括：

- 根动作血缘、Pocketwatch cadence 和路由候选上下文签名；
- 路由效果识别、评分、保留等级、家族排序与上下文交错；
- 直接路由选择的极值、基数、配额与牌堆压缩血缘；
- 回合开始／保留路由选择的读取、构建、保留排名和通用小工具。

药水配额、变异、循环、跨回合和 Pareto 逻辑仍保留在原文件，留给后续独立批次。

## 等价性与构建

- 从 Batch 8 前的 `d2e5c64` 源文件逐段比对，移动块分别为 290 行和 171 行，`CHOICE_SOURCE_MOVE_EQUIVALENT`；没有在移动块内改写算法或文本。
- `verify-refactor-boundaries.ps1`：`REFACTOR_BOUNDARIES_OK search_files=118`。
- Release 构建和 `CompatibilitySmoke` 构建均为 0 errors；保留既有 2 条 `CS9113` 未使用参数警告。

## 固定 0.107.1 运行证据

在实际 0.107.1 游戏进程中，用 IRONCLAD + NIBBITS_NORMAL、`COMPAT1071`、Medium/beam 60、DOP1、固定 5000 ms、Smart 药水和 Beam portfolio 开启的 fixture 运行 `COMPAT1071_PERFORMANCE_BASELINE`。本轮结果与 Batch 7 candidate-03 逐项比较：

| 项目 | Batch 8 |
|---|---:|
| expanded / transitions | 3528 / 10156 |
| elapsed | 2818.126 ms |
| allocated | 370,673,576 B |
| bytes / transition | 36,497.98897 |
| GC Gen0 / Gen1 / Gen2 | 24 / 11 / 0 |
| GC pause / max frame | 182.185 ms / 20.5004 ms |
| >50 ms / >100 ms frames | 0 / 0 |
| route identity | identical |
| result identity | identical |

这证明本轮纯拆分没有改变该固定 fixture 的搜索工作量、路线或结果身份；单个运行时耗时不作为性能收益结论。专用 smoke 写出自己的 JSON，不写普通 unattended result，因此外层启动器的 `launcher_failed` 收尾提示不改变专用 JSON 的通过判定。

原始文件见 [`runtime-evidence/20260918-batch8-beam-choice-split`](../../../runtime-evidence/20260918-batch8-beam-choice-split/)。可见 Steam 游戏测试仍由用户实际验证。
