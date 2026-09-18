# BeamRetentionPolicy Cycle partial 拆分

日期：2026-09-18  
目标：CombatSolver `0.40.2` / Slay the Spire 2 `0.107.1`

## 范围

本批次只整理循环 retention 的物理归属：

- `CombatBeamSolver.BeamRetentionPolicy.Cycle.cs` 承担 startup/exit portfolio、风险桶代表、探测族比较、票据租约和有界保留选择。
- `CombatBeamSolver.Retention.cs` 保留剪枝调用边界、跨文件共享风险/键桥接与最终出口票据结算。
- `CombatBeamSolver.CyclePlanning.cs` 保留周期状态推断、族/回合账本、观察预算、生命周期证据与动作扩展。

未引入接口、服务、策略替换、额外并发路径或新缓存；候选遍历、风险桶、比较顺序、六条 portfolio 上限、票据租约和清理时序保持不变。

## 验证

- Release 构建：通过，0 errors；保留既有 2 条 `CS9113` 未使用参数警告。
- CompatibilitySmoke 构建：通过，0 errors；同样只有既有 2 条 `CS9113` 警告。
- `verify-refactor-boundaries.ps1`：`REFACTOR_BOUNDARIES_OK search_files=122`。
- `verify-target-version.ps1`：`TARGET_VERSION_PASS game=0.107.1 ritsu=0.107.1 symbol=STS2_01071`。
- `git diff --check`：通过。
- 最终 Release DLL 已复制到 `D:\yingye\MODDEV\ports\upstream-0.107.1\release-0.107.1\CombatSolver.dll`，大小 `4,134,400` bytes，时间 `2026-09-18 20:23:14`。

本批次不重复上一批已超时的私有 `FIRST_TURN` 尝试；此前 issue-fix 的 0.107.1 首回合 smoke 只作为启动/加载基线，最终 DLL 交由用户可见游戏实测。
