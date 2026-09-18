# 0.107.1 Hook 监听索引候选——Batch 7

本轮实现架构优化计划 P2-2 的一个已确认 Search 热点：监听布局已经知道每个运行时类型参与哪些 Hook，但旧枚举器每次都从头检查全部槽位。候选在不可变 `MirroredHookListenerLayout` 中按 mask 惰性保存有序位置数组，仍从当前分支的完整 `MirroredHookListenerSnapshot` 读取模型。

## 固定条件

| 项目 | 值 |
|---|---|
| 游戏 / RitsuLib | `0.107.1` / `0.107.1` |
| fixture / seed | `IRONCLAD` + `NIBBITS_NORMAL` / `COMPAT1071` |
| profile / beam | effective Medium / `60` |
| 并行度 / 预算 | DOP `1` / fixed `5000 ms` |
| 药水 / portfolio | Smart / Beam Width enabled, Novelty disabled |
| baseline | `e2fefb3`（Batch 6 基线） |
| candidate | `e2fefb3` 加本轮 Hook 索引改动 |

历史审计已记录约 1.69 亿次过滤槽位检查、约 361 万次成员交出；这支持把该遍历作为候选热点，但不预先保证收益。

## 样本

顺序为 A-B-B-A-A-B，每个样本使用独立 Windows headless 进程。

| 样本 | 组别 | expanded | transitions | elapsed ms | allocated B | B/transition | gen0/1/2 | GC pause ms | max frame ms | >50 / >100 ms |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| baseline-01 | baseline | 3528 | 10156 | 3203.720 | 370530000 | 36483.852 | 24/12/0 | 209.765 | 21.668 | 0 / 0 |
| candidate-01 | candidate | 3528 | 10156 | 3172.755 | 370679136 | 36498.536 | 24/12/0 | 205.835 | 20.436 | 0 / 0 |
| candidate-02 | candidate | 3528 | 10156 | 3137.351 | 370673680 | 36497.999 | 24/11/0 | 213.161 | 24.800 | 0 / 0 |
| baseline-02 | baseline | 3528 | 10156 | 3116.793 | 370654232 | 36496.084 | 24/12/0 | 184.761 | 21.745 | 0 / 0 |
| baseline-03 | baseline | 3528 | 10156 | 3187.096 | 370659568 | 36496.610 | 24/10/0 | 200.350 | 22.433 | 0 / 0 |
| candidate-03 | candidate | 3528 | 10156 | 3125.089 | 370679184 | 36498.541 | 24/11/0 | 199.196 | 22.056 | 0 / 0 |

| 组别 | 平均 elapsed | 平均 allocated | 平均 B/transition | 平均 GC pause | 最大 frame gap |
|---|---:|---:|---:|---:|---:|
| baseline | 3169.203 ms | 370614600 B | 36492.182 | 198.292 ms | 22.433 ms |
| candidate | 3145.065 ms | 370677333 B | 36498.359 | 206.064 ms | 24.800 ms |

候选相对基线耗时约 `-0.762%`，分配约 `+0.017%`（增加 `62,733 B`）；分配变化很小，不能称为分配收益。GC Gen2、>50 ms 帧和 >100 ms 帧均为 `0`。在这个固定 fixture 上，候选没有形成可见的质量或帧尾部回退，因此保留。

## 等价性

六个搜索 JSON 的 `expanded`、`transitions`、`boundary`、完整 `routeIdentity` 和 `resultIdentity` 逐项一致：20 个动作路线，最终分数 `10000679980`、预计战损 `27`、第 `6` 回合结束、敌方全灭、玩家存活。候选 DLL 另通过现有 0.107.1 首回合 smoke：20 个动作且启用增量核验。

## 边界

- 这是固定 headless fixture 的候选证据，不替代可见 Steam 游戏的真实帧时间，也不外推所有遗物、第三方 Mod 或大型战斗的倍率。
- 外层 `run-unattended-test.ps1` 对专用兼容 smoke 仍会报告“Game exited without writing a result ... exit_code=0”；以专用 JSON 的内容为本轮判定依据。
- 原始专用 JSON 与首回合 smoke 位于[`runtime-evidence/20260918-batch7-hook-index`](../../../runtime-evidence/20260918-batch7-hook-index/)。
