# Repository maintenance

目标：让当前工作树只包含**生产源码、长期配置、可重跑测试、必要 fixture、当前真源文档**。历史过程由 Git history 保存，不把仓库当日志归档。

## 永久保留

- `source/src/`、构建文件、测试/验证工具和仍在使用的 fixture。
- `source/docs/compat/0.107.1/` 等长期版本事实。
- 当前架构、测试矩阵、多人 Runbook/Limitations、当前计划和单一 handoff。
- LICENSE、THIRD_PARTY_NOTICES、必要署名。
- `game-body/`：固定 `0.107.1` 兼容快照。它是本仓库明确的 LFS 例外，用于 CI/本地 pinned 验证，不参与普通“生成文件”清理。

## 不提交

- `runtime-evidence/`、原始游戏/Mod 日志、Probe、bug-report ZIP。
- `.local/`、`artifacts/`、`bin/`、`obj/`、coverage、Godot 本地缓存。
- benchmark/prototype 生成的 `results*.json`。
- 一次性 audit、PR review、issue investigation、performance/strategy 流水账。
- 已完成的 U/P 阶段过程文档和被新计划替代的 NEXT 文档。

需要保存一次性证据时，优先放外部问题包或本地实验目录；若结论需要长期约束生产行为，应转成最小 fixture + 可重跑测试。

## 文档规则

- `docs/README.md` 是文档入口。
- `CODEX_HANDOFF.md` 只记录当前状态、当前风险、下一任务。
- 同一事实不要同时复制进 handoff、计划、README 和阶段报告。
- 已完成过程不追加“归档文档”；Git history 已经承担归档职责。
- 新 Markdown 若既不属于长期规范，也未被稳定索引引用，应留在本地而不是提交。

## 清理检查

进行较大版本或架构变更后检查：

1. 是否有旧计划被当前计划完全替代；
2. 是否有 dated 日志/报告进入 Git；
3. 是否有生成结果可由脚本重新得到；
4. README/hand-off 是否描述当前生产能力；
5. `.gitignore` 是否能阻止同类文件重新进入；
6. 删除项是否只存在于历史，不影响当前 build/test 输入。

删除历史资料时不重写 Git 历史；需要时按 commit 恢复。

## 网络功能边界

- 已移除后台在线状态 heartbeat、跑局统计上传、服务器版本检查和自动 Showcase 上传。
- `CombatShowcaseApi/Runtime` 仅保留本地导入/回放能力，不负责后台采集或网络上传。
- 问题包上传属于用户主动触发的反馈动作，与后台 telemetry 分离。
- 未经明确产品目标，不重新引入常驻 telemetry、后台上传 worker、私有 endpoint/token 元数据或隐式网络请求。
