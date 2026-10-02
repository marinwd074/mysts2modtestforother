# ForkableList 存储合并合同

**研究候选已撤回，生产列表保持上游实现。** 直接编译生产 `ForkableCollections.cs`，不依赖游戏或 Compact 后端。

```bash
DOTNET_TieredCompilation=0 dotnet run --project tools/ForkableListLayoutChecks -c Release
```

PowerShell 先设置 `$env:DOTNET_TieredCompilation = '0'`，再运行相同 dotnet 命令。

覆盖全部列表入口、10,000 次随机操作与 64 个独立分支、共享前枚举器、独占修改的枚举失效、非泛型枚举，以及 8 个独占 worker 的首次写入复制。随机序列使用固定种子，与独立 `List<int>` 深复制模型对照。

每种形状预热 1,024 次，测 5 块、每块 10,000 次；对象写入静态引用，使用线程累计分配计数。输出的 B/操作是局部分配，不是存活堆、RSS 或完整搜索性能。

若确需与旧实现对照，可用 `-p:CollectionsSource=...` 指定本地保存的源码，并先确认同一合同适用于两份实现。旧候选生成器已经退役，恢复历史实验使用 Git history。

采用/撤回结论与完整搜索口径见 [性能护栏](../../docs/PERFORMANCE_GUARDRAILS.md)。
