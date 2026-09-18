# Batch 8 runtime evidence

- 时间：2026-09-18（Asia/Shanghai）
- 游戏版本：0.107.1
- CombatSolver：0.40.2，运行时源码为父提交 `d2e5c64b8c457cd42f2752f77bb53a1764b512dc` 加上本批 Batch 8 工作树改动；最终提交包含同一份源码改动
- 兼容目标：`STS2_01071` / RitsuLib 0.107.1
- 模式：`COMPAT1071_PERFORMANCE_BASELINE`
- fixture：IRONCLAD + NIBBITS_NORMAL，`COMPAT1071`，act 0，start turn 1
- 搜索：Medium，beam 60，DOP1，固定 5000 ms，Smart potion，Beam portfolio 开启，Novelty 关闭
- 结果：`expanded=3528`，`transitions=10156`，`elapsedMs=2818.126`，`allocatedBytes=370673576`，`gen2=0`，`framesOver50Ms=0`，`framesOver100Ms=0`
- 对照：Batch 7 `batch7-candidate-03.json`；route identity 和 result identity 均 identical
- 文件：`compat-smoke.txt`、`launcher-result.json`、本文件
- 脱敏：未包含令牌、密钥、Cookie、账号标识或无关个人信息；未复制完整游戏目录、存档、缓存或转储
- 运行说明：专用 smoke 将 JSON 写入 `compat-smoke.txt`，不写普通 unattended result；外层启动器因此报告 `Game exited without writing a result ... exit_code=0`，该提示不是专用 smoke JSON 的失败判定。
