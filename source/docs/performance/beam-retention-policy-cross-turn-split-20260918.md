# BeamRetentionPolicy CrossTurn partial 拆分

日期：2026-09-18
目标：CombatSolver `0.40.2` / Slay the Spire 2 `0.107.1`

## 范围

本批次只整理跨回合 retention 的物理归属：

- `CombatBeamSolver.BeamRetentionPolicy.CrossTurn.cs` 承担跨回合候选族键、风险分带、在途/新族代表选择、回退候选、比较器、探测启动和准入判定。
- `CombatBeamSolver.Retention.cs` 只负责在剪枝阶段按原顺序调用 CrossTurn retention，并继续拥有 Cycle/Mutation/最终协调边界。
- `CombatBeamSolver.CrossTurnPlanning.cs` 保留跨回合调度证据、stand-pat 基线和语义状态附着；它不再拥有 retention 候选排序成员。

未引入接口、服务、策略替换、额外并发路径或新缓存；候选遍历、风险分带、比较顺序、探测预算、排名清空和状态清理保持不变。

## 验证

- Release 构建：通过，0 errors；保留既有 2 条 `CS9113` 未使用参数警告。
- CompatibilitySmoke 构建：通过，0 errors；同样只有既有 2 条 `CS9113` 警告。
- `verify-refactor-boundaries.ps1`：`REFACTOR_BOUNDARIES_OK search_files=121`。
- `verify-target-version.ps1`：`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- `git diff --check`：通过。
- 最终 Release DLL 已复制到 `D:\yingye\MODDEV\ports\upstream-0.107.1\release-0.107.1\CombatSolver.dll`，文件大小 `4,133,888` bytes，时间 `2026-09-18 20:13:29`。

本批次不重复上一批已超时的私有 `FIRST_TURN` 尝试；此前 issue-fix 的 0.107.1 首回合 smoke 只作为启动/加载基线，最终 DLL 交由用户可见游戏实测。
