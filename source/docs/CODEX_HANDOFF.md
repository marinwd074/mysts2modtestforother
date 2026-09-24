# Codex 当前交接

> 只记录当前架构、已验证结果、未验证事项和下一任务。历史过程从 Git history 与阶段文档查。

## 基线

- CombatSolver `0.40.2`
- STS2 / RitsuLib pinned `0.107.1`
- .NET 9 / Godot 4.5.1 / `STS2_01071`
- 单人和多人共享生产搜索/模拟核心；多人额外叠加队友情景、团队目标与同步边界。
- `RootActionPlayers` 只允许本地玩家；不会部署队友动作。
- MultiplayerOnly 牌保留真实牌堆状态与抽牌距离，但不进入主动搜索/自动执行。

## 当前架构

1. **共同搜索核心**
   - `SinglePlayerFullRoute` 与 `MultiplayerLocalCrossTurn` 共用完整 Search/Beam/Novelty/Growth/Relic/长期收益基础。
   - 无队友事件、相同目标和固定预算下已有 pinned 退化等价证据。

2. **多人目标与情景**
   - U3 使用固定 ScenarioSpec 做公平复评，缺失覆盖记 `Unknown`，不把漏评当优势。
   - U4 生产默认仍为 Robust；NominalReference / BoundedRisk 只做同 Matrix、零额外 replay 的参考遥测。
   - `NoAction` 只表示当前 forecast window 内队友不行动，不代表整场战斗不行动。

3. **本地/队友交错**
   - U5 支持 `local A → forecast-only teammate B → local C` 的 detached 模拟。
   - B 只属于预测环境，不获得 deployment authority。
   - reverse-order probe 只有完整 `ShadowFutureStateFingerprint` 相同才允许 ExactEquivalent collapse。
   - 每本地回合最多一个 teammate forecast observation；不主动等待理想队友行为。

4. **Safe Execute**
   - 不再存在固定 1/2/6/32 张生产 action ceiling；session 容量来自当前有限 selected route。
   - 每个本地动作走原生提交 → Choice/队列结算 → predicted/live semantic post-state 对照。
   - forecast observation 前停止部署条件后缀并 fresh replan；旧 request/generation 不得复活。
   - capability 记录为 `action_limit=selected_route`。

## 已验证

- U0：搜索转移、候选、最终选择、实际执行四层诊断与 pinned production replay 已完成；历史 real correlation 已有通过记录。
- U1：predicted/live post-state 主链、取消/迟到回调边界、pinned production replay 已完成；具体卡牌 Host/Client 专项链仍见“未验证”。
- U2：**COMPLETE**。共享搜索核与 pinned degenerate equivalence PASS。
- U3：**COMPLETE**。公平 Scenario Matrix 与 TimeLimit fail-closed 已有真实双端 PASS。
- U4：**COMPLETE**。interim-risk 反例已修正；三种风险解释合同/回归 PASS，生产默认仍 Robust。
- U5：**COMPLETE（算法/合同/pinned）**。Vulnerable/attack、提前终局、共享生成 RNG、资源合法性、抽牌/牌堆顶五类顺序反例均由 pinned production replay 覆盖。
- U6：**COMPLETE（实现与清理）**。
  - 删除固定 Safe Execute action ceiling 与旧 MP-2A 单动作兼容入口/limit aliases。
  - 删除对应旧 validator 路径，保留历史 MP-2B/MP-2C 日志兼容解析。
  - Pinned workflow 已覆盖 Safe Execute policy/classifier/controller 关键文件。
  - 新增 `validate-u6-runtime-closure.ps1`，区分 PASS / FAIL / UNVERIFIED。
  - U6 synthetic 合同覆盖：完整 PASS、无二次部署 PASS、旧 request 复用 FAIL、stale 新授权 FAIL、缺 remote delta UNVERIFIED。
  - Pinned 0.107.1 Release run `35939029095` SUCCESS。
  - compatibility run `35939790075` SUCCESS：`32 PASS / 0 FAIL / 0 SKIP`。

## 未验证 / 已知风险

- **U6-C 真实 Host/Client runtime smoke 被用户于 2026-09-24 明确跳过。** 因此 U6 的实现阶段已关闭，但不能声称真实 forecast boundary → remote WorldVersion advance → fresh search → old request dormant 已在当前 HEAD 实机 PASS。
- U5 的真实远端插入 ownership / WorldVersion / observation→fresh-replan 证据与上项相同，继续记为 `UNVERIFIED`。
- U1 的具体重锤+Choice、连续 Offering/抽牌链等专项 Host/Client 行为不由 pinned replay 替代。
- 这些项是已知运行证据缺口；除非后续改动触及对应边界或准备发布，不作为下一算法阶段 blocker。

## 下一任务

当前主执行目标改为 [`CombatSolver_Quality_First_Next.md`](CombatSolver_Quality_First_Next.md)。原 U7 — Local Exact Lethal 暂停优先级，不直接进入实现。

按该文档依次推进：

1. **先完成第一项的最小可工作切片**：保留少量旧本地候选，在新根上有界重放，区分 Continue / Reselect / Full Restart，优先减少普通队友变化导致的无意义完整重搜。
2. **然后贯通本地药水执行闭环**：候选枚举 → 目标解析 → 原生 UsePotionAction → Choice → 稳定后态校验 → continuation；不把单纯打开 capability 当完成。
3. **再用真实坏路线做生产排序对照**：Robust、本地基线、合作名义路线、已知手打前缀使用相同初态/队友脚本/总预算，只修有证据的丢失层。
4. **最后再决定是否恢复 U7 精确斩杀**：只有证据表明候选阶段确实漏掉可行斩杀时，才把 bounded exact search / DFS 提回优先级。

执行要求：先刷新当前 HEAD；保留 U0–U6 已完成的共同模拟、后态校验、情景矩阵与顺序语义，不重复施工。每轮报告实际用户行为变化、旧新对照、修改函数、验证结果与未验证项；没有实机证据时不宣称体验已改善。
