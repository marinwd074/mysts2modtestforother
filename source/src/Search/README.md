# Search

CombatSolver 的路线搜索与最终选择核心。

- `CombatBeamSolver.*`：展开、保留、终局、最终排序与 transposition。
- `CombatSearchCoordinator.*`：成员组合、预算与结果协调。
- Beam/Novelty/portfolio：有限预算下的搜索组织。
- Multiplayer：队友预测、Scenario Matrix、Chance、团队目标和跨回合决策。
- Growth / Potion / Choice：搜索内策略与分支。

修改时优先固定输入比较“结果身份 + 工作量”。不得通过增加节点/时间/内存预算掩盖质量问题；启发式剪枝与严格等价必须明确区分。
