# Engine/InCombat/Extensions

战斗模拟内部使用的扩展方法。

扩展应保持轻量、无隐藏 live 状态读取，并服务于模拟模型本身。复杂机制语义不要藏在扩展方法中，应放到对应 Mirror、Simulation 或 Prediction 模块。
