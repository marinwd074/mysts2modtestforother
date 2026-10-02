# GC 与并发决策检查

独立 .NET 9 工具，直接编译生产 GC policy、Recovery、scope/暂停计数、内存压力信号和 Smart 预测源码；日志与请求活动 tracker 使用最小替身，不需要游戏依赖。实验性并发控制器的检查仍可通过 `parallelism` 单独运行。

从 `source/` 执行，省略模式为基础检查：

```bash
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- scopes
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- checkpoint
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- manual-release
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery-lifecycle
```

基础与 `scopes` 覆盖预测、暂停归属、准入、重叠、取消和重复 Dispose；`recovery` 覆盖完成证据只消费一次、观察/退避、物理余量、默认回退、结构性终止和信号断开。可恢复的意外退出不再设每 scope 三次上限，退避最多 60 秒；最小 512 MiB 预留及原内存准入仍保留。

`manual-release` 使用真实 CLR 验证等待活动搜索退出、重复请求只合并为一次释放、存活对象保持有效，以及 Aggressive 收集使空闲托管堆 decommit。该模式不修剪进程 working set，也不证明游戏帧时间或系统级释放效果。

`checkpoint` 与 `recovery-lifecycle` 会执行真实CLR收集；后者实际建立1GB NoGC，以测试主动GC制造意外退出，再穿过生产检查点和恢复入口，断言恢复自身一次预留、零额外强制收集，并验证取消、退出请求和Dispose不能复活旧区域。需有足够可用内存，不适合与性能采样同时运行。状态机检查不等于真实游戏或Windows的性能证明。

项目 4.30 的 `recovery-lifecycle` 在恢复时因可用预算约 461 MB 低于 512 MiB 门槛而保持 `UNVERIFIED`；纯状态机通过不能替代该模式。当前风险见 [handoff](../../docs/CODEX_HANDOFF.md)，性能口径见 [性能护栏](../../docs/PERFORMANCE_GUARDRAILS.md)；旧过程报告从 Git history 恢复。
