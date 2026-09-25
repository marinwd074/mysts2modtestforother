# Engine/InCombat

战斗内模拟执行层。

- `Simulation/`：战斗状态推进与动作执行。
- `Mirrors/`：游戏语义镜像。
- `Extensions/`：战斗模拟辅助扩展。

该层只操作模拟分支；任何写入真实游戏状态的行为都属于 Runtime。
