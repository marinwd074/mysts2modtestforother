# 0.107.1 Search baseline — Batch 6

本报告对应架构优化计划的 P2-1 / Batch 6。采集时没有修改 Search 算法、评分、Beam、并行实现或质量策略；只增加了兼容 smoke 的指标出口。

## 固定口径

| 项目 | 值 |
|---|---|
| Game / RitsuLib | `0.107.1` / `0.107.1` |
| Fixture / seed | `IRONCLAD` + `NIBBITS_NORMAL` / `COMPAT1071` |
| Profile / beam | effective Medium / `60` |
| Parallelism / budget | DOP `1` / fixed `5000 ms` |
| Potion / portfolio | Smart / Beam Width enabled, Novelty disabled |
| Source | `43c11c6` parent plus the Batch 6 instrumentation in this task commit |

每根都由独立 Windows headless 游戏进程运行；主线程帧间隔是在搜索任务运行期间由 smoke 逐帧采样。六根的 `expanded=3528`、`transitions=10156`、20 个动作路线、结果身份和 route identity 全部一致。

## 样本

| 样本 | expanded | transitions | elapsed ms | allocated B | B/transition | gen0/1/2 | GC pause ms | max frame ms | >50 / >100 ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| baseline-01 | 3528 | 10156 | 3097.818 | 370664416 | 36497.087 | 24/12/0 | 195.014 | 22.475 | 0 / 0 |
| candidate-control-01 | 3528 | 10156 | 3241.244 | 370659256 | 36496.583 | 24/11/0 | 202.367 | 20.427 | 0 / 0 |
| candidate-control-02 | 3528 | 10156 | 3111.162 | 370654640 | 36496.124 | 24/12/0 | 192.595 | 21.060 | 0 / 0 |
| baseline-02 | 3528 | 10156 | 3193.358 | 370654136 | 36496.075 | 24/11/0 | 201.938 | 20.484 | 0 / 0 |
| baseline-03 | 3528 | 10156 | 3186.570 | 370680656 | 36498.686 | 24/11/0 | 196.740 | 25.691 | 0 / 0 |
| candidate-control-03 | 3528 | 10156 | 3140.080 | 370659896 | 36496.643 | 24/12/0 | 200.860 | 23.736 | 0 / 0 |

组均值：baseline 为 `3159.250 ms / 370666403 B / 36497.283 B/transition`；candidate-control 为 `3164.162 ms / 370657931 B / 36496.448 B/transition`。这只是同二进制运行波动（耗时 `+0.156%`、分配 `-0.002%`），没有 candidate 算法，不能据此声称优化收益。

## 结论与边界

- 当前 fixture 已满足后续 Search 热路径优化所需的工作量、分配、GC、帧和结果身份基线。
- 该批次没有实现候选优化；Batch 7 必须先选定一个有 profiler / allocation 证据的热点，再用同一 fixture、seed、profile、beam、DOP、预算重新取得 candidate 组。
- 这是 headless 基线，不替代正常可见 Steam 会话的真实帧时间和完整 Mod 栈性能结论；可见游戏测试由用户使用本批次输出的 DLL 完成。

原始专用 smoke JSON：[`runtime-evidence/20260918-batch6-performance-baseline`](../../../runtime-evidence/20260918-batch6-performance-baseline/)。
