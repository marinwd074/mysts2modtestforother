# Runtime

真实游戏生命周期与编排层。

主要职责：Mod 入口与 Patch、根快照、搜索会话/取消/刷新、Safe Execute、原生 Choice、多人 world tracking、本地玩家权限、GC/内存和诊断生命周期。

硬边界：后台搜索不得直接读写持续变化的 live 战斗状态；真实动作只能由 Runtime 通过游戏原生入口提交。
