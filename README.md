# CombatSolver · STS2 0.107.1 fork

[![0.107.1 compatibility consistency](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/compatibility.yml/badge.svg?branch=main)](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/compatibility.yml)
[![Pinned 0.107.1 Release build](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/pinned-release-build.yml/badge.svg?branch=main)](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/pinned-release-build.yml)

面向《杀戮尖塔 2》的战斗路线求解器：在后台模拟出牌、选牌、药水、遗物、敌人行动和跨回合状态，展示推荐路线与预计战损。单人可以查看建议、执行本回合或连续全自动。

本仓库是 [Torch1230/CombatSolver](https://github.com/Torch1230/CombatSolver) 的维护分支，固定支持 **STS2 0.107.1**，包含版本适配、受控多人求解、路线复用、诊断与验证工具。

[安装与构建](#安装与构建) · [使用](#使用) · [能力与限制](#能力与限制) · [架构](#架构) · [开发与验证](#开发与验证) · [文档](#文档)

> [!IMPORTANT]
> 这是固定旧版游戏的 fork，不能按“最新游戏版”安装。游戏与 RitsuLib 的兼容目标均为 **0.107.1**；不要混用面向新游戏版本的 CombatSolver 或 RitsuLib DLL。兼容事实以 [build-target.json](source/build-target.json) 和 [0.107.1 兼容说明](source/docs/compat/0.107.1/README.md) 为准。

## 安装与构建

### 准备环境

- 已安装的 **Slay the Spire 2 0.107.1**
- [RitsuLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295) **0.6.2 或更高版本**，并提供 **0.107.1 兼容运行时/引用**
- 从源码构建时需要 **.NET 9 SDK**；项目使用 C# 13 / Godot.NET.Sdk 4.5.1。Windows 脚本使用 PowerShell 7，Linux 使用 Bash

### 从源码构建

以下命令均从**仓库根目录**执行。

Windows：

```powershell
pwsh -NoLogo -NoProfile -File source/tools/build-local-stack.ps1 -Configuration Release
```

Linux：

```bash
bash source/tools/build-local-stack.sh --configuration Release
```

构建会使用常见 Steam 安装路径。路径不匹配时，将 [source/local.props.example](source/local.props.example) 复制为 `source/local.props`，配置 `SteamRoot`、`Sts2DataDir` 和 `RitsuLibDir` 等本机路径；不要提交个人绝对路径。

默认产物写入仓库根的 `artifacts/CombatSolver/`，包含 DLL 和 Mod 清单；Windows 还会构建并复制 `CombatSolver.MemoryCleaner.exe`。脚本不会启动游戏，也不会自动安装到游戏目录。

### 安装到游戏

1. 在游戏的 `mods/CombatSolver/` 目录放入 `CombatSolver.dll` 和 `CombatSolver.json`；Windows 同时放入 `CombatSolver.MemoryCleaner.exe`。
2. 使用分发包时保留包内的 `LICENSE` 和 `THIRD_PARTY_NOTICES.md`；自行打包时分别取自仓库根和 `source/`。
3. 确认匹配的 RitsuLib 兼容运行时已安装，在游戏中启用 RitsuLib 与 CombatSolver。
4. 进入一场单人战斗，等待发牌与回合开始效果结算，路线面板会自动显示搜索进度和结果。

> [!NOTE]
> 只安装明确标注支持 0.107.1 的二进制包。[Releases](https://github.com/marinwd074/mysts2modtestforother/releases) 中的性能原始数据归档不是 Mod 安装包；没有适用二进制时，请从源码构建。`game-body/` 是 CI/兼容验证使用的 Git LFS 固定快照，不替代已安装的游戏。

## 使用

- **重新计算**：从当前实机状态开始新搜索，并恢复本场自动搜索
- **执行本回合**：执行当前回合计划，进入下一回合选牌页面后交还控制权
- **全自动**：单人连续搜索、执行与结束回合，直到战斗结束、玩家停止或安全复核阻止继续
- **停止计算**：搜索期间显示在执行按钮位置；停止后，本场后续回合不再主动搜索，直到点击重新计算

设置可调整逐瓶药水策略、成长目标、搜索预设、并行度、主题与诊断。界面跟随游戏语言，支持中文和英文；完整操作与预算说明见 [玩家指南](source/README.md)。

> [!WARNING]
> 搜索可能占用较多 CPU 和内存。NoGC 默认开启，区域请求预算为 16 GB，**这不是进程总内存上限**。提高预算或并行度会增加资源压力；请按机器条件调整，具体行为见 [搜索预算说明](source/README.md#搜索预算)。

## 能力与限制

- **有限预算搜索**：使用 Beam Search，在时间、节点和内存约束内寻找路线；展示当前找到的最佳结果，不保证全局最优。
- **预测与执行隔离**：后台仅操作捕获根派生的模拟分支；真实动作由 Runtime 通过游戏原生入口提交，状态偏离时重新校验、停止复用或重算。
- **多人有独立准入边界**：未显式选择多人模式时为只读 Probe，不搜索、不执行。`COMBATSOLVER_MULTIPLAYER_MODE=advisor` 提供建议；`safe-execute` 开启本地受控执行。多人不开放单人式 Full Auto，不能控制队友。操作与证据要求见 [多人说明](source/docs/multiplayer/README.md) 和 [Runbook](source/docs/multiplayer/RUNBOOK.md)。
- **多人默认算法**：启用多人搜索后，默认使用真实多人状态上的 local-single-core，与单人共享质量核心；“启用多人预测算法（实验）”才启用队友预测、团队目标与 Scenario/Robust 复评。
- **未支持的语义会停止**：多人专用牌保留牌堆位置与抽牌距离，但不主动推荐或自动执行。未知 Hook、第三方自定义效果和无法证明的执行后态形成明确边界。
- **战前预测 API**：供其他 Mod 使用，当前为 API v6；仅支持 Windows、单人且不在战斗中的状态，使用隔离 headless 游戏进程。接口与第三方适配入口见 [玩家与开发者指南](source/README.md) 和 [适配手册](source/docs/THIRD_PARTY_ADAPTERS.md)。

后台在线状态遥测、自动战绩上传、服务器更新检查和自动私有 Showcase 上传已移除；用户主动上传问题包是独立功能。

## 架构

现有实现的核心调用链：

```text
游戏生命周期 / 回合 Hook
  → SolverController（主线程编排）
  → CombatRootSnapshot + SearchPolicySnapshot（捕获根与冻结策略）
  → CombatSearchCoordinator → CombatBeamSolver（后台搜索）
  → Prediction / Engine（分支模拟）
  → SolverResult → 只读 UI 投影 / Runtime 原生执行
```

| 边界 | 主要职责 |
| --- | --- |
| [Runtime](source/src/Runtime/README.md) | 生命周期、根捕获、会话与取消、多人 WorldVersion、续用与 Safe Execute |
| [Search](source/src/Search/README.md) | Beam/Novelty、候选保留与排序、预算协调、药水与成长策略 |
| [Prediction](source/src/Prediction/README.md) / [Engine](source/src/Engine/README.md) | 卡牌、Power、怪物与 Hook 语义；分支状态、牌堆、RNG、Fork 与模拟 |
| [UI](source/src/UI/README.md) / [Api](source/src/Api/README.md) | 路线与设置展示；面向其他 Mod 的公开契约 |
| [Compatibility](source/src/Compatibility/README.md) / [Diagnostics](source/src/Diagnostics/README.md) | 固定版本 API/Hook 隔离；日志、性能与问题包 |

后台搜索不得反向读取持续变化的 live Runtime 状态。UI 不拥有搜索语义，模拟分支不能修改真实战斗；[源码目录索引](source/src/README.md) 和 [架构与职责地图](source/docs/ARCHITECTURE.md) 说明完整边界。

路线复用已实现同场缓存、新根重放及前台/后台分离。实验情景预热仅作 shadow 测量，SSD 冷存储延期；它们不属于默认 local-single-core 必需路径。已完成实现档案与尚未验证的实机边界分别见 [文档导航](source/docs/README.md) 和 [当前状态](source/docs/CODEX_HANDOFF.md)。

## 开发与验证

快速静态检查：

```powershell
pwsh -NoLogo -NoProfile -File source/tools/verify-target-version.ps1
pwsh -NoLogo -NoProfile -File source/tools/verify-refactor-boundaries.ps1
pwsh -NoLogo -NoProfile -File source/tools/verify-repository-hygiene.ps1
```

按改动选择合同、固定版本集成、离线回放或真实游戏验证，入口见 [工具任务表](source/tools/README.md) 和 [测试矩阵](source/docs/TEST_MATRIX.md)。不要用增加搜索预算掩盖语义错误，也不要将新版上游游戏假设整体合入固定版本分支；移植前阅读 [上游适配规则](UPSTREAM.md)。

> [!IMPORTANT]
> 静态检查、合同测试、集成编译、离线回放、原生 headless 和可见 Steam / Host+Client 分别证明不同范围。离线或 pinned PASS 不代表当前构建已通过实机执行、动画、输入、帧率或完整胜负验证。详见 [测试分层](source/docs/TESTING_LAYERS.md)、[兼容限制](source/docs/compat/0.107.1/KNOWN_LIMITATIONS.md) 和 [性能护栏](source/docs/PERFORMANCE_GUARDRAILS.md)。

项目里程碑记录在 [PROJECT_VERSION](source/PROJECT_VERSION)；`project-vX.XX` 标签用于开发记录，不自动生成安装包。`vMAJOR.MINOR.PATCH` 标签的发布工作流构建固定 0.107.1 包并准备草稿 Release，详见 [工作流说明](.github/workflows/README.md)。

## 问题反馈

错误路线、计划外重算、Choice 失败、多人执行问题或崩溃，请使用 [结构化问题模板](https://github.com/marinwd074/mysts2modtestforother/issues/new?template=bug-report.yml)。在设置的“反馈”页上传或导出问题包，并提供遭遇与回合、实际选中路线、你认为更好的合法路线。不要把问题 ZIP、运行日志或一次性性能结果提交进源码树。协议见 [问题包说明](source/docs/BUG_REPORT_PROTOCOL.md)。

## 文档

- [玩家指南与 API 概览](source/README.md)
- [文档总目录](source/docs/README.md) · [当前状态与待验证边界](source/docs/CODEX_HANDOFF.md)
- [完整架构](source/docs/ARCHITECTURE.md) · [多人架构](source/docs/multiplayer/README.md)
- [0.107.1 兼容证据](source/docs/compat/0.107.1/README.md) · [离线搜索](source/docs/OFFLINE_SEARCH_HARNESS.md) · [检查点回放](source/docs/CHECKPOINT_REPLAY.md)
- [构建与验证工具](source/tools/README.md) · [仓库维护](source/docs/REPOSITORY_MAINTENANCE.md)

模拟基础包含源自 Random Foreseer 的代码，已嵌入本项目，不要求安装其 Mod；来源与署名保留在 [THIRD_PARTY_NOTICES.md](source/THIRD_PARTY_NOTICES.md)。
