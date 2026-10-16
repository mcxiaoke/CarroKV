# CKV 阶段二：SyncMode 与 AppendOnlyLog (WAL) 实施总结报告

> **编制日期**: 2026-10-16 12:22:55  
> **实施分支**: `dev`  
> **实施范围**: Rust、Go、.NET 全语言同构支持  
> **质量保证**: 全测试套件 100% 通过（Rust 66 tests, Go 49 tests, .NET 67 tests）

---

## 一、实施背景与目标回顾

CKV 阶段一采用了确定性全量快照原子替换（`SnapshotAtomic`）架构，在轻量场景下具备天然免坏尾的优雅特性。但在持续高频写场景下，存在以下系统级瓶颈：
1. **写放大灾难 (Write Amplification)**：随着数据量增大，单次写入的 WAF 呈 $O(N)$ 线性飙升至 50x~100x+，严重磨损 SSD 存储介质；
2. **缺乏分级持久化语义**：单次写入必须等待昂贵的磁盘物理刷盘（`FlushFileBuffers` / `fsync`），吞吐量被限制在 200~300 Ops/s；
3. **内存与磁盘状态分叉隐患**：若写前校验与落盘锁未统一收敛，极易在异常路径引发内存与磁盘不一致。

本次实施的目标是：在**保持跨语言完全同构与单文件自包含格式**的前提下，在 Rust、Go、.NET 完整落地 **`SyncMode` 多级同步写合并** 与 **`AppendOnlyLog` (WAL) 增量追加写引擎**，并建立客观严密的基准评测体系与可靠性验证。

---

## 二、架构演进与核心实现清单

### 1. 多级同步模式 (`SyncMode`)
- **`Immediate`（强物理落盘）**：每次写操作构造帧后立即调用物理刷盘，提供介质级零丢失保障。
- **`Deferred`（后台写合并）**：写操作写入内存及 OS Page Cache，由后台周期线程（默认 50ms）定时刷盘与合并写；数据库关闭或析构时安全自动回刷脏数据。
- **`Manual`（显式控制）**：写操作写入 OS Page Cache，完全由调用方显式调用 `db.Flush()` 控制刷盘节奏，提供百万级离线导入吞吐。

### 2. 增量追加日志引擎 (`AppendOnlyLog` / WAL)
- **增量帧格式与追加写**：复用 CKV 规范的 32B 文件头 + 28B 帧头的 BatchFrame 布局，单次写入仅向物理文件末尾追加一个增量帧，实现严格 $O(1)$ 常数时间写入与 1.28x 恒定写放大。
- **断电坏尾自愈 (Tail Truncation)**：启动加载时严格校验 Magic、TxID 单调性与双 CRC32。若物理断电导致末尾遗留残缺坏帧，自动物理截断至最后一个有效帧边界（`set_len` / `Truncate`），触发 `onTailTruncated` 告警，历史数据 100% 完好恢复。
- **Compaction 紧凑压实与两代原子轮转**：提供 `Compact()` API，按确定性 UTF-8 字节序重写全量紧凑快照，旧日志原子轮转为 `.db.bak`；支持配置 `CompactionThresholdBytes` 阈值自动压实。

### 3. 写路径收敛 (`ApplyMutation`)
三语言统一抽象出 `ApplyMutation` / `apply_mutation` 事务模板：
- 进程写锁与跨进程排他文件锁外层获取；
- 磁盘状态一致性校验前置（大小比对快路径优化，彻底消除从头扫描磁盘的 $O(N^2)$ 开销）；
- 磁盘落盘/追加成功后，才在内存中原子应用 Lambda 变更，杜绝并发死锁与状态分叉。

---

## 三、代码量开销与复杂度综合评估

### 1. 代码净增统计
三语言累计净增生产代码仅约 **1,070 行**（平均单语言仅 **~350 行**），测试套件新增 **464 行**：
- **Rust**: 生产净增 367 行，测试 124 行；
- **Go**: 生产净增 302 行，测试 186 行；
- **.NET**: 生产净增 401 行，测试 154 行。

### 2. 系统复杂度评估
- **单文件自闭环架构**：与 RocksDB / SQLite WAL 将主库与 `.wal` 分开的复杂多文件体系不同，CKV 保持了单一 `.db` 文件的极简设计，没有跨文件生命周期维护与文件缺失风险。
- **无需复杂 LSM 级联合并**：由于 CKV 专注于轻量嵌入式 KV 场景，内存维护全量数据，Compaction 仅为一次性的内存快照写出，完全没有多层 SSTable 分层合并（Leveled Compaction）的读写放大与锁争用复杂度。

---

## 四、基准性能与可靠性实测数据总结

### 1. 性能矩阵对比（Windows NVMe SSD 实测）

| 指标维度 | Snapshot + Immediate (原基线) | AppendOnlyLog + Immediate (WAL 强一致) | Snapshot + Deferred (写合并极速) | AppendOnlyLog + Deferred (OS PageCache) |
| :--- | :---: | :---: | :---: | :---: |
| **写入吞吐量** | 253 ~ 315 Ops/s | **439 ~ 464 Ops/s (+47%~76%)** | **25万 ~ 61万 Ops/s** | 513 ~ 607 Ops/s |
| **平均写入延迟** | 3.17 ~ 3.95 ms | **2.15 ~ 2.28 ms (-35%)** | **0.26 ~ 0.99 μs** | 1.64 ~ 1.95 ms |
| **P99 长尾延迟** | 4.60 ~ 10.82 ms | **2.76 ~ 3.01 ms (消除毛刺)** | **0.00 ~ 5.00 μs** | 2.36 ~ 2.95 ms |
| **写放大倍数 (WAF)** | **54x ~ 107x+ (严重恶化)** | **1.28x (恒定稳定，降低 98.8%)** | 1.07x (批量快照) | **1.28x (恒定稳定)** |
| **Compaction 效果** | 始终紧凑 | 110 KB 压实至 142 B (**缩减 99.9%**，耗时 3.4ms) | 始终紧凑 | 同左 |

### 2. 可靠性全方位裁决
- **进程级崩溃 (SIGSEGV/OOM/kill -9)**：WAL 在 `Deferred` / `Manual` 下也同步进入 OS Page Cache，**进程崩溃 100% 零丢失**；而 Snapshot 的 Deferred 模式会丢失最近 50ms 内的纯内存变更。
- **整机断电 / 内核蓝屏**：`Immediate` 模式双方均做到物理介质零丢失；`Deferred` 模式双方均受限于 50ms 的同步窗口。WAL 的末尾写撕裂由 `Tail Truncation` 物理截断自愈，保证数据自洽。
- **SSD 介质寿命保护**：WAL 将写放大从 54x~100x+ 压低至 1.28x，写入磨损降低 98.8%，硬件长期可靠性最佳。

---

## 五、分批提交记录清单

在 `dev` 分支完成的 5 批次本地提交清单：
1. `60d5bcb` - `feat: add SyncMode and StorageEngineMode options across Rust, Go and .NET`
2. `c17e054` - `feat: implement SyncMode (Immediate, Deferred, Manual) and write coalescing across Rust, Go and .NET`
3. `cbed7a6` - `feat: implement AppendOnlyLog (WAL) engine with tail truncation and compaction across Rust, Go and .NET`
4. `b6a4816` - `bench: add benchmark matrix suite and optimize precheck fast-path across Rust, Go and .NET`
5. `329a9f3` - `docs: add comprehensive SyncMode and WAL performance evaluation report and update CHANGES`

---

## 六、下一阶段计划：Dart 与 Kotlin 移植

随着 Rust、Go、.NET 三端引擎与 API v1.0 全面冻结与 WAL 落地，下一步将正式启动移动端/跨平台双主力语言的移植工作：
1. **Dart 移植**：面向 Flutter 桌面与移动端开发，纯 Dart 实现跨平台嵌入式轻量 KV；
2. **Kotlin 移植**：面向 Android 现代原生与 Kotlin Multiplatform (KMP) 开发，提供符合 Kotlin 习惯的协程与响应式 API。
