# Search

CombatSolver 的路线搜索与最终选择核心。

- `CombatBeamSolver.*`：展开、保留、终局、最终排序与 transposition。
- `CombatSearchCoordinator.*`：成员组合、预算与结果协调。
  - 主文件：请求生命周期、主搜索、portfolio 与工作账本。
  - `Audits`：成长/药水补充审计、Smart 梯度与审计内存边界。
  - `ResultPolicy`：结果排序、用药机会成本、成长目标与可证明质量下界。
  - 其他 partial 文件保留 Novelty、跨族与失败恢复职责。
- Beam/Novelty/portfolio：有限预算下的搜索组织。
- Multiplayer：队友预测、Scenario Matrix、Chance、团队目标和跨回合决策。
- Growth / Potion / Choice：搜索内策略与分支。

修改时优先固定输入比较“结果身份 + 工作量”。不得通过增加节点/时间/内存预算掩盖质量问题；启发式剪枝与严格等价必须明确区分。
