# v0.107.1 至 v0.113.1 战斗差异

对照 Git LFS 中的 v0.107.1 和本机 Steam 测试分支 v0.113.1（Build 26660232）。两份 `sts2.dll` 的 SHA-256 分别为 `a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52` 与 `62ecbe91093e1fa862de4329aac865209dcb297804caf0f0520d92d8c0b4a9c3`。候选来自程序集对照、官方更新说明和 [上游 78e4545e](https://github.com/Torch1230/CombatSolver/commit/78e4545e)。结算按本机 v0.113.1 实现复核。

模型清单中卡牌类型从 578 增至 717，新增 140 个、移除 1 个。敌人模型从 121 增至 123。清单包含调试模型及非战斗类型。现有卡牌 525 个、敌人 73 个反编译文本不同，包含签名、动画和工具生成差异。原始类型清单位于忽略目录 `source/.local/game-mechanics-01131/model-inventory.json`。

## 已同步

| 内容 | v0.113.1 规则 |
|---|---|
| `Fuel`、`Haze`、`Tracking` | 燃料只回能量。烟霾先施毒再施加虚弱。追踪每层加 50 个百分点，对虚弱目标按百分比增伤。 |
| `GuidingStar`、`ExpectAFight` | 引路星在攻击后施加下回合抽牌。期待战斗先获得格挡，再将手中非 X 费攻击牌减费至其被打出。 |
| `WellLaidPlans`、`Expertise` | 精心计划持续保留持有者的手牌。Expertise 按牌面固定抽牌，再保留整手牌一回合。 |
| `Eidolon`、`Sacrifice`、`Scrape` | 幻灵依次自动打出消耗堆中可打出的虚无牌。牺牲按奥斯提最大生命的三倍给格挡。Scrape 按含全部费用修正的费用弃牌，X 费仍弃。 |
| `FeralPower`、`HistoryCourse`、`Regalite` | Feral 排除复制牌。History Course 只记录上一回合最后一张非复制攻击。Regalite 仅在持有者每回合首次生成卡牌时给格挡，状态参与分叉、指纹及续用。 |
| `PhantomBlades` | 小刀从幻影刀刃能力获得的保留随能力移除。牌本身已有的保留继续生效。 |
| `MementoMori` | 一张诡计牌，固定攻击当前生命最低的可攻击敌人，平手取原生目标顺序中的首位。 |
| `InfestedPrism`、`ThievingHopper` | 晶体怪第二次刺击替代旧动作，脉冲在伤害后施加易伤并叠加生命火花。飞贼跳过任务牌，偷牌改用 `CombatCardSelection`。 |
| `Aeonglass`、`LouseProgenitor`、`Axebot`、`DevotedSculptor` | 永世沙漏的衰竭随机插入弃牌堆并消耗 Shuffle RNG。Louse 采用新版升阶力量。Axebot 从原生重生次数读取启动强化，重生生命使用原生怪物模型。Devoted Sculptor 的仪式为 9。 |
| 其他敌人 | Rocket 采用新版动作 ID。典礼兽仅有 Plow 时获得该动作的力量。瀑布巨人、Vantom、Lagavulin Matriarch 和 `TheInsatiable` 的相关数量由原生模型在根捕获时冻结。MechaKnight 喷火新增的伤害随原生意图读取。 |

本机热修版的 `ForgottenRitual` 仍要求本回合已有消耗，因此保留条件。Hyperbeam 的临时集中下降在上一轮移植中已走新分支。Doom 仍在玩家手牌处理及充能球之后结算，已有阶段顺序适用。

## 验证范围

`CompatibilitySmoke=true` 提供 `MECHANICS`、`HOPPER_MECHANICS` 和 `AEONGLASS_MECHANICS`。它们在独立存档的游戏进程中对照原生动作与预测状态。标准单人整场自动执行使用 `STANDARD_FULLAUTO`。结果范围见[当前测试矩阵](../../TEST_MATRIX.md)。

本轮集中于现有标准战斗中确认的差异。新增卡牌清单中的每种效果需要单独验证。愚者、箭矢、极速和恐惧仍在现有支持边界之外。新版多人未实测，飞贼遇到未捕获的远端牌堆会停止预测，因为未知的偷牌资格会改变共享 RNG。
