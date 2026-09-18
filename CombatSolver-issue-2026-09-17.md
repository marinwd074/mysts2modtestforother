# CombatSolver 计算失败问题

记录日期：2026-09-17

## 现象

进入战斗后，战斗路线求解器显示红色错误：

> 计算失败：搜索动作回放失败：turn=1 action_count=0 kind=PlayCar...

截图中同时显示“全自动：运行中”，但路线列表为空，无法生成可执行路线。

## 截图中的战斗状态

- 游戏版本：`v0.107.1`
- 玩家生命：`80/80`
- 当前能量：`3/3`
- 敌人生命：`46/46`
- 敌人意图：`12` 点攻击
- 失败发生在第 `1` 回合、动作数 `0`，即第一张计划卡牌执行前。

## 复现步骤

1. 启动带有 CombatSolver 的游戏。
2. 进入单人战斗。
3. 等待求解器自动计算，或点击“重新计算”。
4. 观察求解器消息区域。

## 预期结果

求解器应针对当前战斗生成一条卡牌/药水/结束回合路线，并显示在路线列表中。

## 实际结果

求解器在回放第一步 `PlayCard` 动作时失败，未执行任何动作，也没有显示可执行路线。

## 初步定位

这是 `SearchTransitionException` 类型的搜索状态转移失败，不是界面绘制错误。`action_count=0` 表明失败发生在搜索分支的第一个动作；`kind=PlayCard` 表明失败动作是出牌，而不是药水或结束回合。

当前 DLL 是从公开 CombatSolver 项目适配并编译到本机《杀戮尖塔 2》`0.107.1` 的版本。上游源码面向较新的游戏 API，因此最可能的原因是模拟状态与本机游戏在第一张卡牌的身份、目标、费用或原生出牌结算接口上存在不一致。

## 相关安装文件

- `MODS/CombatSolver.dll`
- `MODS/CombatSolver.json`
- 依赖：`STS2-RitsuLib 0.6.2`

## 后续排查所需信息

需要保留游戏日志中同一时间点的完整异常信息，尤其是 `SearchTransitionException` 后面的 `InnerException`、卡牌 ID、目标 ID 和 `parent_state`，才能确定是卡牌回放、目标解析还是模拟器状态复制导致的失败。

## 修复记录（2026-09-17）

已从 godot.log 定位 InnerException：CorruptionPower does not override AbstractModel.AfterModifyingCardPlayResultPileOrPosition。不是卡牌身份或目标解析失败，而是新版回调登记不适用于本机 0.107.1，导致模拟器静态初始化失败。

已删除 MODDEV/candidates/v0.16.0 中该错误登记，保留严格覆盖检查。Release 编译成功，4 项定向兼容检查通过，并替换 MODS/CombatSolver.dll。旧 DLL 保存在 MODDEV/candidates/v0.16.0/.local/backup-before-result-pile-fix-20260917/CombatSolver.dll。

待游戏内验收：重新启动游戏进入战斗，确认路线列表生成且不再出现同一异常。尚未验证整场全自动战斗。

## 第二轮修复与实测（2026-09-17）

用户复测仍失败，最新 InnerException 为 JugglingPower 不覆盖 BeforeCardPlayed。旧版中该效果属于 AfterCardPlayed；并进一步修复出牌后、回合结束、破格挡的同类 API 不匹配。

全部 45 组回调登记初始化通过。已启动独立存档的原生游戏测试，铁甲战士对啃咬兽代表遭遇成功搜索到 35 个动作、跨 11 回合的路线，首动 BASH，增量回放验证通过。没有恢复截图中的原始战斗，也未验证整场自动部署。

生产 DLL 已重新构建并安装；第二轮前的 DLL 保存在 MODDEV/candidates/v0.16.0/.local/compat-smoke/CombatSolver-before-second-fix.dll。
