# 性能原始数据归档

主 Git 保留性能摘要、Markdown 报告、小型复现夹具和基线数字；达到 100 KB 的机器可读性能原始 JSON 改存 GitHub Release，避免继续膨胀源码仓库。此次迁移没有改写 Git 历史，旧提交仍可用于回退或取回原文件。

## 归档包

- Release：[performance-archive-20260918](https://github.com/xwr20070408-cloud/CombatSolver/releases/tag/performance-archive-20260918)
- 下载：[CombatSolver-performance-raw-20260918.zip](https://github.com/xwr20070408-cloud/CombatSolver/releases/download/performance-archive-20260918/CombatSolver-performance-raw-20260918.zip)
- 归档生成提交：`9222427`
- 原始文件：20 个，共 16,599,953 字节
- 压缩包：1,760,971 字节
- SHA-256：`70D519C9506C1B26A4FC583321F6B65C6FA979F4E5467EE703A5C40162A0DA54`

归档副本已将已知的绝对游戏目录和用户数据目录替换为占位符；没有把凭据、令牌或 Cookie 作为归档内容。需要本地恢复时，可下载 ZIP 并解压到临时目录，不要把归档文件重新加入源码跟踪。

## 文件清单

- <a id="admitted-expansion-jobs-20260908-json"></a>`admitted-expansion-jobs-20260908.json`（115,841 字节）
- <a id="backend-cpu-hotspots-20260908-json"></a>`backend-cpu-hotspots-20260908.json`（185,220 字节）
- <a id="bowlbugs-wave-admission-20260912-json"></a>`bowlbugs-wave-admission-20260912.json`（190,962 字节）
- <a id="choice-continuation-expansion-implementation-20260914-json"></a>`choice-continuation-expansion-implementation-20260914.json`（343,410 字节）
- <a id="choice-continuation-search-20260914-json"></a>`choice-continuation-search-20260914.json`（246,234 字节）
- <a id="choice-source-inventory-20260912-json"></a>`choice-source-inventory-20260912.json`（169,755 字节）
- <a id="crab-latency-20260914-json"></a>`crab-latency-20260914.json`（1,089,856 字节）
- <a id="derived-work-reuse-20260915-json"></a>`derived-work-reuse-20260915.json`（319,456 字节）
- <a id="five-candidates-20260913-json"></a>`five-candidates-20260913.json`（248,873 字节）
- <a id="gc-issue36-results-json"></a>`gc-issue36-results.json`（317,163 字节）
- <a id="gc-issue36-round2-results-json"></a>`gc-issue36-round2-results.json`（136,006 字节）
- <a id="general-allocation-20260914-json"></a>`general-allocation-20260914.json`（10,778,002 字节）
- <a id="hotspot-exploration-20260913-json"></a>`hotspot-exploration-20260913.json`（311,636 字节）
- <a id="memory-tenfold-20260913-json"></a>`memory-tenfold-20260913.json`（231,279 字节）
- <a id="perf2-integration-20260909-json"></a>`perf2-integration-20260909.json`（466,010 字节）
- <a id="performance-pr-20260915-json"></a>`performance-pr-20260915.json`（314,385 字节）
- <a id="queen-replay-optimization-20260913-json"></a>`queen-replay-optimization-20260913.json`（148,732 字节）
- <a id="six-directions-20260913-json"></a>`six-directions-20260913.json`（272,316 字节）
- <a id="six-memory-20260913-json"></a>`six-memory-20260913.json`（570,380 字节）
- <a id="snapshot-replay-followup-20260909-json"></a>`snapshot-replay-followup-20260909.json`（144,437 字节）
