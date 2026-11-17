# Codex 当前交接

更新于 2026-11-17。接手先读仓库根和 `source/` 下的 `AGENTS.md`，再看 `git status --short --branch`、`git log -8 --oneline` 和实际安装的 DLL。仓库位于 `D:/Project/yingye/CombatSolver`。

## 当前状态

- 项目记录版本 **4.42**，Mod **0.40.2**，游戏 **v0.113.1**（Steam `public-beta`，Build `26660232`），RitsuLib API **0.113.0**，目标 `STS2_01130`。本机 Steam 活动目录仍装有原来的 `0.40.2+8a58bbda`。新构建和测试均在项目的忽略目录。
- 工作区已有根 `AGENTS.md` 的本地修改、`source/tools/multiplayer-lab/prepare-instances.ps1` 的修改和 `game-body/` 的 222 个本地删除。固定游戏快照仍在 Git/LFS 索引中。验证用外部 Steam 安装，避免顺手还原、暂存或提交这些变化。
- 单人与多人共用搜索和模拟核心。单人预测整场战斗，默认多人 local-single-core 搜索当前回合及之后两回合，仅允许部署本地玩家动作。六回合、选择性深入、有限承伤和队友预测属于实验。围攻、夹击的实验建议使用已结算的队友动作，带 `AdviceOnly` 的路线不具备部署权限。详见[架构](ARCHITECTURE.md)与[多人能力边界](multiplayer/LIMITATIONS.md)。
- 用户要求**不以路线质量换速度**。以相同根和预算比较首条审核胜利、最终耗时、完整路线、末态 RNG、战损和药水成本。单人整场预测、Smart 药水策略、成长/遗物规则均适用。[性能护栏](PERFORMANCE_GUARDRAILS.md)

## v0.113.1 接入验证

- 参考上游 `78e4545e`，接入新版 RNG、`CardPlay`、选牌栈、RunModel 和实际发生变化的卡牌、药水、遗物行为。新版破旧匕首的名称按升级次数格式化，避免首场搜索前无限枚举升级。
- 使用 RitsuLib **0.6.14** 的 `0.113.0` 变体在真实 Godot 进程验证。必需补丁 66 项全部成功。首回合搜索返回 25 个计划动作并通过增量验证。铁甲战士 A0 初始牌组打 `NIBBITS_NORMAL`，全自动第 6 回合击败两名敌人，生命 80 → 59。一次搜索，续用五回合，日志中状态不匹配与部署偏离均为 0。战斗结束清理搜索、部署与 GC 状态。
- 愚者与箭矢状态、极速和恐惧 RunModel 尚未接入推演，多人能力未在该版本重验。历史 0.107.1 快照及用它构建的固定测试需配套历史源码。当前本地结果在 `source/.local/sts2-v01131-acceptance/`。

## 标准战斗机制同步

v0.107.1 与 v0.113.1 的程序集对照、已同步的卡牌和敌人以及边界见 [v0.113.1 战斗差异](compat/0.113.1/README.md)。原生差分和全自动结果见[测试矩阵](TEST_MATRIX.md)。

## 最近修复与下一步

### 骑士团伙搜索失败

`KNIGHTS_ELITE` 问题包 `4126c78ba1ea4b40af924ea89afdd63b`，桌面 `CombatSolver-BugReports/`。第一回合自动搜索曾在 `MaterializeSelectedRoute` 读取模拟器时报 `CloneValueOnlyForTransitionMemo:0`。格挡药插入重放命中了 R0 纯值终局缓存，最终结果却仍需读取模拟器的药水账本。提交 `bf63bf54` 在[格挡药插入重放](../src/Search/CombatBeamSolver.BlockPotionInsertion.cs)禁用纯值命中，正常 Beam 仍使用 R0。

- 原包首个可操作检查点 `1b649aac8f9549d4a1f6dea3ec8b83f5:1` 的完整 `ContinuationStamp` 匹配。旧安装 DLL 给离线宿主接入生产 R0 memo 后复现相同异常。修复版 DOP 16 仍有 813 次普通缓存命中，预测第 4 回合获胜、战损 5、使用 1 瓶格挡药。完整动作和末态与旧版关闭 memo 的对照一致，独立重放通过。
- Release、`U0U1PinnedHarness rolling-review` 的 9 个场景和 Windows 结构门禁通过。核对过离线宿主实际加载的修复 DLL SHA-256。取证在 `source/.local/knights-4126c78b/`，入口为 `memo-baseline.trace.txt`、`memo-fixed-verified.json` 和 `fixed-release/CombatSolver.dll`。
- **待原生执行验收**。两次隔离游戏 `DeploySolver` 均在主机资源预约阶段超时。下次用当前源码及原包同一检查点跑 `run-unattended-test.ps1 -ReplayMode DeploySolver`，核对格挡药原生消耗、续用、实际战损和胜利，并记录恢复验签范围。[检查点回放](CHECKPOINT_REPLAY.md) · [隔离进程与资源预约](HEADLESS_TESTING.md)
- `5051c720` 将出牌、药水及结束回合统一接入[原生动作生效确认](../src/Runtime/NativeDeploymentAction.cs)。原包既可验搜索收尾，也可检查该确认路径，原生结果应与离线预测分列。

### 继续搜索提速时

- S0–S5 已交付固定输入采样、Beam 临时缓冲复用、可证回复下界、转置查表简化以及单人 Novelty 审核胜利的提前提交。旧离线样本首胜回调由数百毫秒至约 1 秒缩到 28–80 ms，完整请求改善较小。单人后续已有两轮 Beam 复核和 Smart 补充审计，最近的审计截止修复见 `8a58bbda`。已提交的合格终局仍经过正式物化与审核。
- 15 次本机离线设置对照见 `source/.local/settings-performance-20261109/`。安装 DLL 下，钻地虫和雕刻者每场的完整动作、战损、药水与末态一致。DOP 8 相比 16 未稳定提速。NoGC 开关对照中开启组也未实际建立区域。雕刻者有两次因复核触及截止而进入后续搜索，耗时约 120 秒。保留 DOP 16、NoGC 开启、零战损早停作为本机对照设置。
- 优先用钻地虫、雕刻者或卷轴的同根记录，量出 Beam 复核、Smart 审计和 GC 的实际耗时。另用 `SEARCH_REQUEST_TIMELINE` 区分首战浮窗挂载到 worker 启动的等待。通用非终局转移 hydration 仍默认关闭，已有对照中构键、校验和 Fork 抵消了命中收益。历史样本不用于证明当前构建的净提速。

## 当前未验证边界

- 多人按[实机验收清单](multiplayer/LIVE_ACCEPTANCE.md)推进，从 [Host/Client Runbook](multiplayer/RUNBOOK.md)启动。优先核对同稳定窗口合并与搜索中取消、连续失效后安静回合恢复、回合开始选牌自动接管、连续共享 RNG 失效时的显式采用。Headbutt 旧构建已通过一次自动选牌，Issue #8 和 Vicious 估值 Issue #9 仍需当前构建的对应场景。
- NoGC 自动恢复的状态机已有合同。真实 `recovery-lifecycle` 在可用恢复余量低于门槛的旧环境中未通过实机验收。详见[测试矩阵](TEST_MATRIX.md)。
- 远端 `fix/test-subject-phase-transition-guard` 的 tip 为 `c8a47799`，仍有未并入 `main` 的 ContinuationLifecycleGuard 改动。应核对现有生命周期处理后再决定是否采纳。

## 验证入口

在 `source/` 运行 `dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false`，使用本机 v0.113.1 游戏 DLL 和 RitsuLib API 0.113.0。定向验证按[测试矩阵](TEST_MATRIX.md)选择。历史设计、实机报告和旧交接的找回方式见[文档导航](README.md#历史资料)。
