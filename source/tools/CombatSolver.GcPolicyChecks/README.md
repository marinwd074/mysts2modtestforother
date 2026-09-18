# GC 与并发决策检查

独立 .NET 9 工具，直接编译生产 GC policy、Recovery、scope/暂停计数、内存压力信号和 Smart 预测源码；日志与请求活动 tracker 使用最小替身，不需要游戏依赖。实验性并发控制器的检查仍可通过 `parallelism` 单独运行。

从仓库根执行，省略模式为基础检查：

```bash
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- scopes
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- checkpoint
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery-lifecycle
```

2026-09-13：基础20项、scope8项、检查点1项、恢复状态机6项、恢复生命周期2项通过。基础与scope覆盖预测、暂停归属、准入、重叠、取消和重复Dispose；恢复检查覆盖完成证据只消费一次、观察/退避、每scope三次上限、物理余量、默认回退和信号断开。

`checkpoint` 与 `recovery-lifecycle` 会执行真实CLR收集；后者实际建立1GB NoGC，以测试主动GC制造意外退出，再穿过生产检查点和恢复入口，断言恢复自身一次预留、零额外强制收集，并验证取消、退出请求和Dispose不能复活旧区域。需有足够可用内存，不适合与性能采样同时运行。状态机检查不等于真实游戏或Windows的性能证明。

本轮游戏对照及失败夹具见[NoGC回退恢复报告](../../docs/performance/queen-gc-recovery-20260913.md)；旧研究见[GC与并发调查](../../docs/performance/gc-issue36-implementation.md)。
