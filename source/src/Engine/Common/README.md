# Engine/Common

模拟引擎的通用状态、fork、状态存储、预测风险和基础数据结构。

这里的类型影响状态隔离与去重正确性。避免依赖 UI/Runtime，也不要引入 live 战斗可变引用。
