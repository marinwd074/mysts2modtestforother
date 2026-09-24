# Codex 当前交接

> 只记录当前生产状态、当前风险和下一任务。已完成阶段过程从 Git history 恢复，不在本文件维护流水账。

## 基线

- CombatSolver `0.40.2`
- STS2 / RitsuLib pinned `0.107.1`
- .NET 9 / Godot 4.5.1 / `STS2_01071`
- 单人与多人共享生产搜索/模拟核心。
- 多人部署权限只属于本地玩家；不会部署队友动作。
- MultiplayerOnly 牌保留真实牌堆状态与抽牌距离，但不进入主动搜索/自动执行。

## 当前多人架构

1. **共同搜索核心**：单人完整路线与多人本地跨回合共用 Beam、Novelty、成长、遗物、药水和长期收益基础。
2. **团队目标/情景**：多人叠加公平 Scenario Matrix 和 Robust 目标；生产默认仍为 Robust。
3. **交错预测**：支持 local → forecast-only teammate → local 的 detached 模拟；队友节点没有 deployment authority。
4. **路线刷新**：轻量 HP/Block drift 可对少量保留候选做 bounded refresh；目标死亡、资源、Power、牌堆/RNG 等强变化走 fresh search。
5. **Safe Execute**：逐本地动作使用原生提交、稳定等待和 predicted/live semantic post-state 对照；旧 request/generation 不可复活。
6. **药水**：本地药水已接入多人 Safe Execute；槽位、PotionId、目标合法性和动作后资源变化均需重新验证。

U0–U6 的实现与 pinned/合同阶段均已完成。U5/U6 的部分真实 Host/Client observation → fresh replan 边界仍保留为运行时未验证项，不由 pinned replay 代替。

## Quality-first 当前状态

- **第一项：bounded multiplayer refresh**：已实现并通过 pinned/合同；真实多人正反例仍可继续补证据。
- **第二项：本地药水执行闭环**：已实现并通过 pinned/合同；真实多人自动喝药仍属于实机验证边界。
- **第三项：实际坏路线定位**：进行中。原则是只修复能由当前 HEAD 问题包证明的路线质量缺口，不凭感觉调整 Robust/Beam 权重。

已确认的第三项样本：

- 历史 ANGER 样本：未完成多人路线曾被 `AngerCopiesGenerated` 的最终排序过早惩罚；已有定向修复。
- 历史 X1 T3 空推荐：等价质量下 `ActionCount` 短路线曾压过当前回合实际出牌；已有定向修复。
- Beam retention A/B 诊断已接入：同一真实候选池同时观察 TeamObjective 与 legacy 单人排序，不改变生产选择。
- CEREMONIAL_BEAST T6：VICIOUS 的精确模拟原本存在，但战略估值低估未来抽牌收益；现按可达 Vulnerable 触发次数计入 `CardAccessPotential`，相关合同/pinned 已通过。
- Headbutt immediate Choice 共用 `NativeChoiceRuntime`；当前实机样本已有完整自动选牌链。另一个跨回合 TurnStart Choice 缺口已独立补上 planned choice replay。

最近与上述代码相关的 compatibility / pinned 0.107.1 验证均通过；仓库清理仅删除历史资料和生成输出，不改变生产代码。

## 当前未验证边界

- 当前 HEAD 的真实多人 Beam retention A/B：需要在“明显不如手打”的合法局面上确认更好路线究竟在 Beam、portfolio、U3/U4 还是执行层丢失。
- U5/U6 的真实远端插入后 WorldVersion advance → fresh search → old request dormant。
- 具体连续抽牌/Choice 链仍应以新问题包为准，不把旧日志当当前代码证明。

## 下一任务

继续 Quality-first 第三项：

1. 用当前 HEAD 复现一个明确不如合法手打前缀的多人局面，并保留正式问题包/Client journal。
2. 沿 `FINAL_CANDIDATE → MP_BEAM_RETENTION_AB → MP_QUALITY_SORTING → FINAL_SELECTION → Safe Execute` 定位候选丢失层。
3. 只有真实候选池证明 TeamObjective/Robust 排掉更优合法路线时才改主排序；若最终选择正确但动作未执行，则回到 Safe Execute/Choice/continuation。
4. 比较生存、累计战损、最终 HP、结束轮数、药水支出与响应时间，保持总搜索预算不变。

每次只推进一个小阶段，并在结束时更新本 handoff；不要重新创建阶段流水账文档。
