# 策略快照关键词与能耗查询合同

运行 `python3 tools/StrategicKeywordChecks/run.py`，需要 .NET 9。脚本提取当前生产 `StrategicEffectContext`，仅反向替换关键字按需读取改动以得到原算法，在相同输入上比较整个返回记录。

134,932 个案例覆盖全部 65,536 个需求位组合、技能消耗与牌自身消耗的区别、可重复/一次性小刀、原生和第三方生成牌、空牌组，以及两次 Build 之间修改同一批牌。还断言每张牌每次评估至多读取一次关键字，纯攻击次数需求及已由技能消耗确定的 Exhaust 不读取关键字，小刀复用仍读取牌自身的 Exhaust。

模型替身提供可控的只读属性和关键字读取计数；`CardMechanismFacts` 与 `SolverWeights` 直接链接生产文件。此合同证明标量公式和缓存作用域，不能替代原生关键字来源、Hook 或实际搜索验证，也不提供性能数字。实际无头 A/B 和增量回放见性能报告。

原生入口为 `python tools/StrategicKeywordChecks/run.py --native`，需要先构建 pinned Release DLL；项目未配置 SteamRoot 时追加 `--steam-root <Steam库目录>`，结果目录可用 `--out .local/...` 指定。脚本在 `.local/` 生成上下文 oracle，将单关键词查询替换为 `GetKeywords` 完整集合、将模拟器费用查询替换为原生费用 oracle，显式编译到既有 U0U1PinnedHarness；普通 harness 构建不包含 oracle，生产不依赖它。标量构建只编译自身文件，模型替身拒绝模拟查询，避免混淆两个验证层级。

原生合同比较 462 个完整上下文及抽牌时机、840 次关键词查询、32 个 DOP=4 分支，覆盖 Hex 全局修改与移除、Corruption、卡牌 COW、NoDraw/DarkEmbrace 顺序和父/真实状态隔离。Hex 正向分支暴露旧原生 getter 读真实 Owner 的状态偏差；新路径与捕获分支的原生 Hook 一致。oracle 共享标量公式，只验证查询路径和状态传递；不能替代真实多人、第三方 Hook 或部署验证，也不提供性能数字。

同一 `--native` 入口还运行 `strategic-energy`：180 次原生费用、792 个完整上下文以及 48 个 DOP=4 分支，覆盖 Corruption、免费 Skill/Power/Attack、Veilpiercer、VoidForm、局部费用修改、X/负费用、牌堆移动、Power 移除和 shadow 消耗。核对 Power/history 指纹不因查询改变、卡牌 COW 与父分支隔离，以及真实 Owner 变化后根估值不变。

费用 oracle 仅在无头夹具中串行临时挂载克隆牌，使原生晚期 Hook 读取正确牌堆；局部费用使用原生 `GetWithModifiers(Local)`，全局费用使用捕获分支的原生 Hook，最后在 `finally` 移除克隆牌并核对真实夹具状态恢复。并发测试只查询分叉模拟器，比较串行 oracle 已算出的值，不并发修改原生夹具。普通游戏和生产 DLL 不包含此 oracle。

完整请求另用既有 `request-hydration-benchmark-off --fixture strategic-repeat --measure`：计时前独立重放所选完整路线，核对最终 ContinuationStamp、胜利回合、累计战损和真实夹具隔离；计时内仍验证完整结果/工作与阶段守恒。状态来源修正可改变评分输入，发生路线/工作变化时不得用新旧墙钟宣称固定工作提速。
