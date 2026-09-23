# P3 多情景复评

P3 的目标是：**对少量当前本地动作，在不同队友行为压力情景下重新评价；推荐不能依赖单一乐观 Shadow 路线。**

P2 已通过 CI。P3 不扩大主 Beam，不提高搜索深度，不给队友动作任何部署权限。

## 情景集合

Shadow Team Top-K 在 exact future-state merge 后，固定优先保护四种非概率压力情景：

1. `Aggressive`：优先完整斩杀，其次敌方有效耐久最低；
2. `Defensive`：优先全员存活、最脆弱队员有效生命和团队有效生命；
3. `Conserve`：优先保留团队 Energy / Stars，并减少额外动作；
4. `NoAction`：队友本回合不再出牌。

这四类是 stress scenarios，不是经过实测校准的真实概率。通用 `BehaviorLogMass` 只用于相同情景质量下的稳定 tie-break，以及未来做经验校准；**当前推荐不做概率加权**。

如果 Shadow beam 还有剩余槽位，优先补不同 `ActionOrderKey`。Team Shadow 搜索本身按单 action 交错扩展，因此 A→B、B→A、易伤→攻击、攻击→易伤等顺序都是真正按该顺序推进 simulator/RNG；只有完整 future fingerprint 相同才允许 exact merge。

## 少量候选复评

只处理普通 P1/P2 最终排序前 **4 个不同当前本地动作组**。每个动作组最多补 4 个情景代表，因此 final-only coverage 上限是 16。

coverage 只从已经生成的候选中选代表：

- 不新增 simulator expansion；
- 不增加主 BeamWidth；
- 不延长 Shadow action depth；
- 同一情景只留一个 final-quality 最好的代表。

## 当前动作必须跨情景一致

分组键使用 `MultiplayerChanceDecisionIdentity.CurrentTurnDecisionKey`：

- 包含当前回合本地出牌、目标、药水和当前回合选择；
- 到当前本地 EndTurn 为止；
- 排除 Shadow forecast；
- 排除 EndTurn 之后才能观察到的 TurnStart choices。

因此复评比较的是**同一个当前可部署动作**在不同队友未来下的结果，不允许每个情景提前选择不同当前动作，避免 strategy fusion。

## 排序

只有前述少量动作组的 `ScenarioSetComplete=true`，并至少包含 `NoAction` + 另一个情景时，P3 rerank 才启用；否则 fail closed 回 P1/P2 排序。`ScenarioSetComplete` 只表示情景搜索没有被 unsupported Choice / 动作深度截断，与概率模型是否经过经验校准是两件不同的事。

情景之间不使用概率，排序为：

1. 所有情景全员存活；
2. 所有情景均斩杀；
3. 最坏 `LossEquivalent`；
4. 最坏队员战损；
5. 情景平均 `LossEquivalent`；
6. 最坏团队战损；
7. 最坏敌方有效耐久；
8. 情景覆盖数量；
9. 原 P1/P2 排名。

选定当前动作后，用该动作组里的保守情景作为显示/continuation 代表。真实状态若与该世界线不一致，已有 continuation revalidation 仍会 fresh-search。

## 概率代码

此前预备的 `MultiplayerChanceDecisionMath` 与 chance coverage 保留但继续禁用：

`EnableMultiplayerChanceAggregation = false`

`FinalChanceCoverageLimit = 0`

等后续有真实队友行为日志后，再考虑把压力情景替换或补充为经验概率模型。
