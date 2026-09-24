# 测试分层与入口

当前项目把版本一致性、纯代码合同和真实游戏运行分开记录。每一层只声明自己实际覆盖的范围；通过无头运行不等于通过可见 Steam 排版或帧时间验收。

| 层级 | 目标 | 入口与证据 |
|---|---|---|
| L0 静态一致性 | 目标版本、项目 XML、文档链接和工作树格式 | `source/tools/verify-target-version.ps1`、`git diff --check`、`.github/workflows/compatibility.yml` |
| L1 纯逻辑合同 | StateKey、Beam 排序、Retention、快照/回放数据结构、缓存和策略边界 | `source/tools/*Checks` 中不需要 STS2 进程的独立工具；输出的 `*_OK` 与退出码是证据 |
| L2 0.107.1 集成编译 | 真实 STS2/RitsuLib 引用、Hook、Mirror 和兼容 API 形状 | `dotnet build source/CombatSolver.csproj -c Release -p:CompatibilitySmoke=true --no-restore`；需本机安装的游戏和 RitsuLib 引用 |
| L3 原生/完整游戏 | Hook 生命周期、预测差分、回合准备、全自动部署和 Headless 行为 | `source/tools/CompatibilitySmoke` 的 `FIRST_TURN`、`TURN_SETUP`、`FULLAUTO` 模式及本地/外部保存的脱敏运行证据 |

L3 的运行证据必须记录源码提交、游戏/RitsuLib 版本、场景和结果；失败启动、资源不足或未进入断言的尝试不能写成 Passed。可见 Steam 性能、FPS 和完整胜负不由当前无头 Smoke 自动声明，性能比较仍遵循 [性能护栏](PERFORMANCE_GUARDRAILS.md)。

版本相关的源代码边界见 [`src/Compatibility/README.md`](../src/Compatibility/README.md)，目标事实见 [0.107.1 兼容证据](compat/0.107.1/README.md)。
