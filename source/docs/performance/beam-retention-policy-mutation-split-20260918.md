# BeamRetentionPolicy Mutation partial 拆分报告

日期：2026-09-18
基线：CombatSolver `0.40.2`，目标游戏/RitsuLib `0.107.1`，兼容符号 `STS2_01071`

## 范围

本批次只做文件职责归属调整，仍使用同一程序集、同一嵌套 `BeamRetentionPolicy` partial，不引入接口、服务、仓储或新字段。

- `CombatBeamSolver.BeamRetentionPolicy.Mutation.cs`：有序变异类型、碰撞/激活/代表选择、continuation packet、admission/observation、公平调度、lease transition、key-policy 校验。
- `CombatBeamSolver.BeamRetentionPolicy.cs`：构造器、共享排名与通用策略、`RankBest` 等协调器。
- `CombatBeamSolver.OrderedMutationRetention.cs`：预算常量、lineage/lease ledger、原子 pair、普通回退和最终提交。

类型块 152 行、首段协调方法 1,753 行、后续 Mutation 方法图 2,634 行均从原文件逐字移动；原文件剩余内容逐字保持。

## 验证

- Release 编译：通过，0 errors；保留既有 2 条 `CS9113` 警告。
- CompatibilitySmoke 编译：通过，0 errors；保留同样 2 条警告。
- 结构门禁：`REFACTOR_BOUNDARIES_OK search_files=120`。
- 目标版本门禁：`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- 本批次额外私有 0.107.1 `FIRST_TURN` 兼容运行尝试在 180 秒内超时，因此不记为通过证据，也不宣称性能或行为收益。用户仍需使用输出 DLL 做可见游戏验收。

## 结论

本批次确认的是职责边界和源码等价移动，不是算法优化或运行时收益结论。后续按交接计划继续 CrossTurn，再处理 Cycle。
