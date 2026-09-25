# TestingBridge

生产 Runtime 与无人测试进程之间的最小协议桥。

这里只放测试进程必须在真实 Runtime 生命周期中访问的稳定协议/活动追踪。测试策略、fixture 与断言属于 `src/Testing/` 或 `tools/`。
