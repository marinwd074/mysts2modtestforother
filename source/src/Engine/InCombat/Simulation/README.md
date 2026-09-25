# Engine/InCombat/Simulation

战斗模拟器的核心状态推进与动作执行。

这里负责在独立模拟分支中推进玩家/敌人状态、牌堆、资源与战斗流程。状态必须可 fork、可释放且不泄漏真实游戏引用；Runtime 才拥有 live 战斗写权限。
