# CombatSolver glossary

- **候选展示 / Candidate presentation**：同一搜索候选的当前回合预览、完整路线预览及各自可物化 seed 组成的整体。版本与完整动作序列共同确定 seed 是否匹配；评分与候选提升仍由 Search 决定。
- **显示批准 / Display approval**：候选已通过既有正式排序，可作为稳定前台方案显示。它可以先于物化完成，不能据此推导可执行性。
- **可采用 seed / Adoption seed**：独占只读动作列表快照，并延迟、单次物化已选路线的对象。采用请求冻结该 seed，后台后来出现的候选不会替换请求。
- **已显示候选 / Rendered candidate**：主线程显示 adapter 确认投影过的候选展示。普通 Play 与 TurnSetup 共享该概念，但保留各自既有当前回合接管规则。
- **动作授权 / Action authority**：Runtime 在当前会话、真实状态与能力门禁下给予本地动作的部署许可。显示批准或候选采用都不能替代它；队友观察不产生该许可。

- **Worker 会话 / Worker session**：拥有一个精确启动的隔离进程及其请求协议。只有同一 requestId 的 result、非 Held ready 与请求归档清理尝试完成后才可复用（归档删除失败仍按原策略仅记警告）；退出未确认时保留所有权并停止复用，不能宣称进程已释放。

候选交互 module、interface、depth、seam、adapter、leverage 与 locality 的具体职责见 [当前架构](source/docs/ARCHITECTURE.md)。
