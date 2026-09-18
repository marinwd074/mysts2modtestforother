# BeamRetentionPolicy Potion partial 拆分——Batch 9

## 范围

本轮继续按架构优化计划 P2-3 做纯结构拆分：将嵌套 BeamRetentionPolicy 的药水职责移动到 CombatBeamSolver.BeamRetentionPolicy.Potion.cs。仍使用同一个 partial class CombatBeamSolver 和同一个嵌套 partial class BeamRetentionPolicy，不新增接口、策略服务、仓储或依赖注入层。

移动的完整职责块为：

- FinalPolicyQualificationFacts / FinalPolicyQualificationSignature 记录，以及对应的构建、资格比较辅助；
- ReservePotionQuotaLeaders、FeasiblePotionUseQuotas、EnforcePotionUseQuota；
- PotionUseLineageKey、FindBestPotionLineage、UsesPotion。

调用方、配额计算、候选遍历顺序、替换位置选择、谱系排序和其他 Beam/Pareto 逻辑均未改写。

## 等价性与构建

- 上述 Potion 专属记录和方法与 Batch 9 前 3894d2a66937ffec5e32c9af8c475a89d1fe7997 中的原文件逐段比对通过：POTION_SOURCE_MOVE_EQUIVALENT（记录 16 行、最终资格辅助 158 行、配额 58 行、谱系 36 行、UsesPotion 2 行）。
- verify-refactor-boundaries.ps1：REFACTOR_BOUNDARIES_OK search_files=119。
- 同步修正 verify-refactor-boundaries.sh 的 partial 文件清单与 Choice/Potion 检查；当前 Windows 环境没有 bash 可执行文件，因此 Bash 脚本本轮未实际运行，不能报告其运行通过。
- Release 和 CompatibilitySmoke 构建均为 0 errors；保留既有 2 条 CS9113 未使用参数警告。
- 本轮没有新增运行时行为或性能结论；用户将使用本批生产 DLL 进行可见 Steam 实机验证。

## 边界

本轮只改变物理文件归属，不改变搜索策略、候选排序、RNG、分支数量或终局裁决。Mutation、Cycle 和 CrossTurn 仍留在原文件/既有职责文件，后续拆分继续按单批次验证。
