# CombatSolver 源码工作规则

> 当前目标基线：CombatSolver `0.40.2`，游戏 / RitsuLib `0.107.1`，`STS2_01071`，.NET 9 / Godot 4.5.1。实际分支与提交以当前仓库为准。

本文件只保留真正影响正确性、兼容性和产品边界的技术规则。实现方式和验证规模由 Agent 按风险自主决定。

## 1. 技术硬边界

- 后台搜索不得读取会随真实战斗推进而变化的 live 可变值，也不得修改真实战斗。搜索分支的可变状态必须来自根快照、影子状态、克隆 Model 或 `PredictionStateStore`。
- 真实 Player / Creature / Card / Power / Relic / Monster 的 HP、格挡、能量、牌堆、RNG、行动和其他分支可变状态不得被搜索线程当作分支状态直接读取。
- 未知语义应 fail closed、形成明确搜索边界或明确报错；禁止吞异常后继续、伪造默认值或假装状态等价。
- Fork 相关可变引用必须正确 remap；同一分支使用一致的 `PredictionForkContext`，需要写入的 Preview 先取得可写副本。
- 正确性优先于搜索质量和性能；不得通过单纯扩大搜索预算掩盖语义偏差。

## 2. 架构与自主修改

职责默认保持：

- `src/Runtime`：真实战斗生命周期和编排。
- `src/Search`：搜索策略与搜索会话。
- `src/Engine`：通用模拟。
- `src/Prediction`：领域预测与状态补偿。
- `src/UI`：展示和交互，不拥有搜索语义。
- `src/Testing`：游戏内测试夹具；Release 按设计不依赖它。

Agent 可以在当前目标需要时自主：

- 拆分或合并文件、移动职责、删除死代码；
- 修复同一根因下的相邻缺陷；
- 增加内部诊断、合同测试、辅助脚本和临时迁移代码；
- 扩大到相关目录或历史证据进行检索；
- 修改架构文档和测试入口以保持事实一致。

只有明显改变产品方向、公开 API/协议、用户数据格式、正式多人能力范围或发布行为时才需要先请求用户决定。

## 3. 状态所有权要求

只有当改动真正涉及模拟状态、Fork、跨回合续用、live/runtime 生命周期或并发所有权时，才需要核对以下问题；普通 UI、文档、脚本和局部纯函数无需机械填写清单：

1. 根状态何时、由谁捕获；
2. 可变状态属于哪一层；
3. Fork/COW/remap 是否隔离；
4. 是否影响合法性、状态键或 continuation；
5. actual/simulated 差分如何暴露；
6. 生命周期和清理边界是否完整。

运行时为保护玩家状态拦截异常时，应停止受影响的搜索/部署并留下可诊断事件，而不是继续执行不可信动作。

## 4. 当前事实与检索

默认真源只读最小集合：

- `docs/CombatSolver_Quality_First_Next.md`：当前多人求解器主执行目标与优先级；未被用户明确替换或完成前，相关实现按它推进。
- `docs/CODEX_HANDOFF.md`：当前状态、未完成项和下一步；若仍残留与质量优先文档冲突的旧阶段描述，以质量优先文档为准并修正 handoff。
- `docs/ARCHITECTURE.md`：只有涉及职责/状态所有权/依赖边界时读取。
- `docs/TEST_MATRIX.md`：只有需要决定验证层级或入口时读取。
- `docs/compat/0.107.1/README.md`：只有版本语义/兼容问题时读取。
- `docs/multiplayer/RUNBOOK.md`：只有真实 Host/Client Lab 操作前读取。

`docs/performance/`、`strategy/`、`audits/`、`issues/`、`releases/`、`history/`、`runtime-evidence/` 和 `tools/multiplayer-lab/MultiplayerTestTools/` 都是**按需上下文**，不得在普通任务开始时整目录加载。历史结论需要追溯时优先 Git history + 精确文件，而不是把旧报告当当前规则。

## 5. 验证

- 按改动风险选择最小充分验证；没有固定测试数量限制，也没有一刀切的 120 秒上限。
- 文档/规则改动通常做链接、格式、静态门禁即可；CI 自动运行的合同测试无需为了形式在本地重复。
- 纯重构优先结构门禁和编译；行为变化再增加代表性合同/运行测试。
- 战斗语义优先 actual/simulated 差分和最窄复现场景；跨回合、Fork、部署、多人同步等高风险修改应提高验证层级。
- Search / Beam / 排序修改应固定输入比较结果身份与工作量，必要时再做 benchmark。
- 失败可以直接诊断、修复并重跑；不要因为首个方案失败就停工。
- Release DLL、静态检查、合同测试、headless 和可见 Steam 是不同证据层，报告时明确区分。

## 6. Multiplayer

- 任何多人实机操作前先读 `docs/multiplayer/RUNBOOK.md`，不要重新试错已记录的 Steam、Mod 重启、ClientId、停止和证据流程。
- 必须实机验证的多人任务由 Codex/Agent 驱动技术全流程：构建、prepare、启动/重启、warm-up、Graceful stop、日志定位和 validator；用户只负责游戏窗口内的 GUI 点击与观察。
- 当前正式产品仍以单人能力为稳定基线；多人能力通过 Probe / Advisor / Lab gate 分阶段推进。
- 可以自主开发、重构和验证多人实验能力，但在对应真实 Host/Client 证据通过前，不把 Lab-only 能力改成正式默认或普通玩家入口。
- 不发送 CombatSolver 自定义网络包、不控制其他玩家，除非未来项目方向明确改变并单独设计协议与验证。
- MP-2B 等连续多动作能力必须解决本地预期变化与远端并发变化的归因，不能用简单 WorldVersion reset/rebase 规避。

## 7. 文档、Git 与发布

- `DEVELOPMENT_NOTES.md`、`TEST_MATRIX.md`、`ARCHITECTURE.md` 只在对应事实真的改变时更新，不写流水账。
- 普通开发可按需要使用一个或多个逻辑 commit，完成后推送当前分支。
- 临时输出、完整日志、Profiler、`.local/`、`bin/`、`obj/` 和 `.godot/` 不进入源码树；确有长期价值的关键证据例外。
- Agent 可以清理无引用的旧文档、测试产物和一次性工具，但不得删除用户数据或正式游戏安装。
- 只有用户明确要求准备发版、发布、打标签或上传创意工坊时才进入对应发布动作；普通开发不要自动发布。

## 8. 完成汇报

只报告当前任务真正改变的行为、实际验证结果和仍存在的风险。无需为每个任务强制生成 handoff 或 Markdown；项目状态发生明显变化时再更新 `docs/CODEX_HANDOFF.md`。
