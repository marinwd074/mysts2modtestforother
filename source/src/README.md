# src

CombatSolver 生产源码与游戏内测试代码的架构入口。

| 目录 | 职责 |
|---|---|
| `Api/` | 面向其他 Mod 的稳定公开入口 |
| `Compatibility/` | 固定游戏版本 API/Hook 形状隔离 |
| `Diagnostics/` | 性能、日志、问题包与可观测性 |
| `Engine/` | 独立战斗模拟基础 |
| `Prediction/` | 卡牌、Power、遗物、怪物等语义预测 |
| `Replay/` | 检查点、回放与 Showcase 本地能力 |
| `Runtime/` | 真实战斗生命周期、Patch、控制器和 Safe Execute |
| `Search/` | Beam/Novelty、候选排序、多人情景与搜索协调 |
| `Strategy/` | 跨模块使用的静态战略事实 |
| `Testing/` | 游戏内无人/回归测试夹具 |
| `UI/` | 覆盖层、设置和文本展示 |

UI/Runtime 可以调用 Search；Search 通过快照和模拟调用 Prediction/Engine。后台搜索不得反向读取持续变化的 live Runtime 状态。详细边界见 `../docs/ARCHITECTURE.md` 和 `../AGENTS.md`。
