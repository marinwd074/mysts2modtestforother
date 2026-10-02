# Repository maintenance

目标：让当前工作树只包含**生产源码、长期配置、可重跑测试、必要 fixture、当前真源文档**。历史过程由 Git history 保存，不把仓库当日志归档。

## 参考项目与本仓库布局

| 参考项目 | 可借鉴做法 | 本仓库落点 |
|---|---|---|
| [STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib) | `src/`、`docs/`、`scripts/`、`tools/` 分工，贡献说明作为稳定入口 | 保持 `source/src/`、`source/docs/`、`source/tools/` 分区，README 按任务导航 |
| [ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2) | Godot 模板要求 solution 与项目同目录 | 保持 `source/` 内项目和 Godot 文件的关系，目录整理不迁移构建根 |
| [sts2-mod-template](https://github.com/sethmcleod/sts2-mod-template) | README 明确源码、资源、脚本用途，并提供文档地图和环境检查入口 | 补齐工具任务表，复用现有检查与版本工具，生成产物继续隔离 |

根 README 面向安装与项目概览；`source/README.md` 面向玩家与构建；`docs/README.md` 索引当前规范；`tools/README.md` 选择最窄验证入口。采用这些组织方法不引入参考项目的依赖或游戏版本。

## 永久保留

- `source/src/`、构建文件、测试/验证工具和仍在使用的 fixture。
- `source/docs/compat/0.107.1/` 等长期版本事实。
- 当前架构、测试矩阵、多人 Runbook/Limitations、当前计划和单一 handoff。
- LICENSE、THIRD_PARTY_NOTICES、必要署名。
- 根目录 `.editorconfig`、`CONTRIBUTING.md`、`UPSTREAM.md` 与 `.github/` 协作/发布配置。
- `game-body/`：固定 `0.107.1` 兼容快照。它是本仓库明确的 LFS 例外，用于 CI/本地 pinned 验证，不参与普通“生成文件”清理。

## 不提交

- `runtime-evidence/`、原始游戏/Mod 日志、Probe、bug-report ZIP。
- `.local/`、`artifacts/`、`bin/`、`obj/`、coverage、Godot 本地缓存。
- benchmark/prototype 生成的 `results*.json`。
- 一次性 audit、PR review、issue investigation、performance/strategy 流水账。
- 已完成的 U/P 阶段过程文档和被新计划替代的 NEXT 文档。

需要保存一次性证据时，优先放外部问题包或本地实验目录；若结论需要长期约束生产行为，应转成最小 fixture + 可重跑测试。

## 文档规则

- 根目录、`.github/`、`game-body/`、`source/src/` 及其一级架构模块必须有就地 `README.md`，说明职责、关键入口与禁止事项。
- 目录 README 只描述当前职责，不写阶段流水账、旧提交号或一次性实验结果。
- `docs/README.md` 是文档入口。
- `CODEX_HANDOFF.md` 只记录当前状态、当前风险、下一任务。
- 同一事实不要同时复制进 handoff、计划、README 和阶段报告。
- 已完成过程不追加“归档文档”；Git history 已经承担归档职责。
- 新 Markdown 若既不属于长期规范，也未被稳定索引引用，应留在本地而不是提交。
- 当前 Markdown 的本地链接必须指向 Git 跟踪的文件或非空目录，不能依赖只在本机存在的输出；外部 URL、页内锚点和代码示例不作文件链接校验。
- 历史证据退出当前树后保留文件名与验证范围，用 Git history 定位，不保留失效的本地链接；历史 PASS 不代表当前 HEAD 已重跑。

## 清理检查

进行较大版本或架构变更后检查：

1. 是否有旧计划被当前计划完全替代；
2. 是否有 dated 日志/报告进入 Git；
3. 是否有生成结果可由脚本重新得到；
4. README/hand-off 是否描述当前生产能力；
5. `.gitignore` 是否能阻止同类文件重新进入；
6. 删除项是否只存在于历史，不影响当前 build/test 输入。

删除历史资料时不重写 Git 历史；需要时按 commit 恢复。

`tools/verify-repository-hygiene.ps1` 执行跟踪文件、文档链接、版本日志及固定快照检查；它已接入快速 push/PR CI 和本地 `run-ci-gates.ps1`。链接检查不访问网络，也不下载 LFS 内容。

已退役的旧开发笔记、完成的滚动重构路线、旧性能候选原型和被撤回的集合生成器由 Git history 恢复。当前状态由 handoff 管理，架构边界由 `ARCHITECTURE.md` 管理，性能候选取舍归入 [性能护栏](PERFORMANCE_GUARDRAILS.md)；生产源码与可重跑合同继续保留。

## 网络功能边界

- 已移除后台在线状态 heartbeat、跑局统计上传、服务器版本检查和自动 Showcase 上传。
- `CombatShowcaseApi/Runtime` 仅保留本地导入/回放能力，不负责后台采集或网络上传。
- 问题包上传属于用户主动触发的反馈动作，与后台 telemetry 分离。
- 未经明确产品目标，不重新引入常驻 telemetry、后台上传 worker、私有 endpoint/token 元数据或隐式网络请求。

## GitHub Release

- GitHub Release 由 `vMAJOR.MINOR.PATCH` tag 触发 `.github/workflows/release.yml`。
- tag、`CombatSolver.json` 与 `CombatSolver.csproj` 的版本必须一致。
- Release 使用固定 0.107.1 game-body 与固定 RitsuLib 兼容包构建，并生成 ZIP + SHA-256。
- 自动创建的是 draft Release；最终公开仍由维护者确认。
- Steam Workshop 文案保留在 `docs/workshop/`，GitHub CI 不保存 Workshop/Quark 私有发布凭据或本机路径。
