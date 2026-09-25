# Api

供其他 Mod 调用的公开 API 边界。

- `PreCombatForecastApi.cs`：战前确定预测、假设样本与规划快照。
- `CombatShowcaseApi.cs`：本地 Showcase/回放调用面。
- `PreCombatForecastContracts.cs`：公开请求/结果契约。

公开契约变化应保持兼容或明确提升 API 版本；不要把内部 Search/Runtime 可变对象直接暴露给调用方。
