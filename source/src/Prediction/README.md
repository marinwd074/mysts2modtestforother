# Prediction

游戏语义预测与模拟补偿层。

这里实现卡牌、Power、遗物、药水、怪物行动、Choice、回合生命周期和第三方 Hook 的预测语义。未知或未验证语义应形成明确边界，不能猜测后继续。

新增机制时同时检查 fork/clone 状态归属、状态指纹、RNG/历史计数、actual/simulated 差分和多人目标 fan-out。
