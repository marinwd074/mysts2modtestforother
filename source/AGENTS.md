# CombatSolver 仓库工作指令

> 当前基线：CombatSolver `0.40.2`，目标游戏与 RitsuLib `0.107.1`，兼容符号 `STS2_01071`，运行时为 .NET 9 / Godot 4.5.1。分支和提交以当前 `main` HEAD 为准。

本文件只保留项目硬边界和任务路由；详细架构、测试协议和发布流程以当前文档或对应 skill 为准。历史记录是证据，不是当前任务指令。

## 1. 不可违反的边界

- 生产功能默认只支持单人战斗；允许在独立的 Multiplayer Probe / 显式能力门禁下开发客户端本地玩家适配。在多人能力通过对应实机验证前，不解除默认 inert gate、不自动部署、不改变网络协议、不发送自定义网络包、不控制其他玩家。使用仓库内嵌模拟引擎，不重新引入 RandomForeseer 运行时依赖。
- 后台搜索不得读取会随实机推进而变化的 live 值，也不得修改真实战斗。分支可变值必须属于根快照、影子状态、克隆 Model 或 `PredictionStateStore`。
- 未知语义必须显式失败或形成明确搜索边界；禁止宽泛异常捕获后继续、返回默认值、跳过候选或伪造相等。
- 正确性优先于搜索质量和性能；不得用扩大 Beam、节点、时间或 No-GC 预算掩盖模拟偏差。
- 源码、测试和文档改动直接提交当前任务分支；完成相称验证后推送已配置的 GitHub 远端。保留不属于当前任务的用户改动。

当前职责地图见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。源码入口按职责归属：`src/Runtime` 编排 live 生命周期，`src/Search` 拥有搜索策略，`src/Engine` 拥有通用模拟，`src/Prediction` 提供领域补偿，`src/UI` 只渲染只读快照，`src/Testing` 只承载游戏内测试夹具。Release 构建按设计排除 `src/Testing`；生产 DLL 不能单独证明无人测试协议通过。

## 2. 任务路由与当前事实

先读与任务直接相关的源码和当前文档，不为“完整了解仓库”扫描历史目录。

- 玩家问题包、日志和复现包：`.agents/skills/issue-bundle-triage/SKILL.md`。
- 策略回放迭代：`.agents/skills/strategy-replay-iteration/SKILL.md`。
- 战斗语义、Power、卡牌、遗物、药水、球、RNG、Fork 和跨回合：`.agents/skills/combat-semantic-change/SKILL.md`。
- Beam、评分、剪枝、预算、GC 和卡顿：`.agents/skills/search-performance-optimization/SKILL.md`。
- Runtime/Search/UI/Testing/registry 职责迁移：`.agents/skills/architecture-boundary-refactor/SKILL.md`。
- 玩家可见文案和本地化：`.agents/skills/ui-localization/SKILL.md`。
- 版本、ZIP、标签、创意工坊或完整发布门禁：`.agents/skills/release-gate/SKILL.md`。

当前事实入口：

- [docs/README.md](docs/README.md)：当前入口和专题目录。
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)：职责、依赖方向和状态所有权。
- [docs/TESTING_LAYERS.md](docs/TESTING_LAYERS.md)：测试层级。
- [docs/TEST_MATRIX.md](docs/TEST_MATRIX.md)：当前测试目录和重跑方式。
- [docs/DEVELOPMENT_NOTES.md](docs/DEVELOPMENT_NOTES.md)：当前开发状态与未发布行为变化。
- [docs/CODEX_HANDOFF.md](docs/CODEX_HANDOFF.md)：当前对话结束时覆盖更新的 Codex 接手入口；不是历史记录。
- [docs/compat/0.107.1/README.md](docs/compat/0.107.1/README.md)：目标版本兼容资料。
- [docs/CHECKPOINT_REPLAY.md](docs/CHECKPOINT_REPLAY.md)、[docs/HEADLESS_TESTING.md](docs/HEADLESS_TESTING.md)：回放和隔离测试边界。

默认不得扫描以下目录：

```text
docs/performance/  docs/issues/  docs/releases/  docs/audits/
docs/pr/  docs/history/  runtime-evidence/  .local/decompiled/
```

只有用户明确要求、任务直接引用、当前 Bug/commit/测试输入指向，或必须取得历史 A/B/回归证据时，才读取其中的具体文件。默认顺序是：`AGENTS.md` → 目标源码 → 目标测试 → `ARCHITECTURE.md` 对应章节 → 当前 compat 文档。

## 3. 状态所有权与错误处理

新增或修改分支状态时，必须说明：

1. 主线程根从哪里、何时捕获；
2. 状态属于哪一层，Fork 是深拷贝、COW 还是不可变共享；
3. 对象引用如何通过同一个 `PredictionForkContext` 重映射；
4. 是否影响动作合法性、状态键或跨回合 `ContinuationStamp`；
5. actual/simulated 差分如何捕获；
6. 创建、叠加、移除、清空和事务边界。

真实 Player、Creature、CardModel、PowerModel、RelicModel、MonsterModel 的 HP、格挡、能量、牌堆、Power、RNG、行动、召唤/死亡、遗物和药水等分支可变值不得从 live 对象读取。Fork 子结构共享同一个上下文；可变引用优先 `RequireRemap`。`PredictedCard.Preview` 写入前必须取得 `MutablePreview`。

推断式 mirror 构建失败可以归类为未支持；执行中失败必须中止当前搜索或部署，保留动作、事务和状态上下文。运行时为保护玩家状态拦截异常时，应停止会话、输出稳定失败事件，并让无人测试得到 Failed。

## 4. 最小验证选择

每个阶段只取一次直接证据。输入和产物未变化时，不重复同一测试、构建、复制、打包、上传、fetch/status 或解包检查。

- **L0 文档/规则/静态结构**：定向路径、链接、格式检查与 `git diff --check`。不得自动 Release build、启动游戏、复制 DLL、完整 smoke、性能 benchmark、发布打包或生成 runtime evidence。
- **CI/静态脚本**：只运行修改的脚本及对应 gate；不自动构建 CombatSolver，不复制 DLL。
- **纯 refactor / 文件移动 / 同一职责内 partial 拆分**：必要时只做 Release 构建和结构门禁；只有实际跨 Runtime 边界或改变可观察行为才加一个代表 smoke，不生成历史报告或 runtime evidence。
- **普通战斗语义**：L1 最小 actual/simulated 差分；确实新增跨回合、Fork、续用或部署状态才升到 L2。不要因普通 Power、牌堆或历史修复自动跑完整战斗。
- **Search / Beam / 排序**：固定短 benchmark、结果身份和工作量一致性；完整场景仅限最终候选或用户明确要求。
- **发布**：只有用户明确说“准备发版”“发版/发布”“上传/更新创意工坊”或“完整发布门禁”才进入发布流程。

单个 unattended 请求默认不超过 `120` 秒；达到上限应记录未验证，不把超时扩大到 `180/360` 秒等待。生产 Release DLL 不包含 `src/Testing` 时，不得把通用无人测试启动器的超时或无响应写成语义结论。

## 5. 文档、规则与署名

- `DEVELOPMENT_NOTES.md` 只在玩家可见行为、运行时语义、能力、重要兼容变化或重要性能结论改变时更新。
- `TEST_MATRIX.md` 只在测试入口、fixture 行为或覆盖范围改变时更新；普通修复的本轮通过结果不持续追加到巨型历史表。
- 纯文件移动、partial 拆分、CI gate 和文档整理不自动新建 performance report、开发记录或测试矩阵历史条目。
- 只有模块职责、依赖方向、状态所有权或公开入口变化时，才同步 `ARCHITECTURE.md`、相关 skill、结构门禁或重构路线。
- 面向玩家的更新日志使用当前游戏官方译名；不要把类名、runId、内部算法、GC/内存细节或测试流水账写进玩家更新说明。
- 当前有效文件中的旧 CombatSolver 作者署名、旧 Maintainer/Contact、旧仓库归属和不再适用的历史约束应删除或改成匿名技术描述。明确属于第三方依赖的许可证、来源和版权文本保留；不为此全量扫描历史目录或改写 Git 历史。

## 6. Git、发布与临时文件

- 一个用户请求对应一个逻辑 commit；只读审计或无文件改动不创建空提交。
- 普通开发完成后，显式暂存当前任务文件并推送当前分支；不得清空工作区或覆盖用户改动。
- 活动发布批次内只记录到指定“开发中”文档，不逐项升版本、构建、打包、打标签或上传。
- `准备发版` 完成版本同步、更新日志、提交、一次 Release 构建和最小 ZIP，但不创建标签、不上传、不推送；`发版/发布` 才执行正式发布；`上传/更新创意工坊` 只上传已经定版版本。
- 发布包、完整日志、问题包、Profiler、`.local/`、`bin/`、`obj/` 和 `.godot/` 不进入源码目录；只有新失败模式、新验证结论或关键证据才整理进 `runtime-evidence/`。
- 只清理由当前任务创建的临时文件、副本和测试产物。额外清理须由用户明确要求、由本任务创建大型副本，或由临时产物阻碍当前工作触发。清理前列出精确绝对路径、用途和占用；不得递归删除工作区、用户数据、凭据、活动运行数据或不明缓存。删除后报告回收空间和可恢复性。

## 7. 完成汇报

汇报功能层面的变化、所属职责层、实际执行的验证和未执行项。必须区分本轮证据、静态阅读、旧测试记录和未验证结果；不能把编译或聚合 HP 比较写成语义等价，也不能把旧报告写成本轮通过。

任务结束前覆盖更新 `docs/CODEX_HANDOFF.md`，并在最终回复输出同用途的 Markdown 交接。交接只写当前状态和下一步，不复制完整历史。
