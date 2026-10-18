# CarroKV (Cross-language Key-Value Database)

**CarroKV** 是一个轻量级、跨语言、纯单文件嵌入式 Key-Value 数据库引擎，严格遵循 [CKV v3.2 二进制设计规范](docs/KV-DB-DESIGN-SPEC.md) 与 [跨语言共有接口统一规范](docs/API-SPEC.md)。

它专为桌面客户端、移动端、边缘计算以及轻量微服务打造，面向**小型配置/状态数据集**（单进程、通常 < 2MB、读多写少）。

> **状态：pre-release（0.2.0）**。磁盘格式已冻结为 v1.0，各语言模块已就绪并配备完整自动化回归测试矩阵。
>
> **明确不做**：多进程并发读写同一份库（第二个打开者会快速失败并返回 `Locked`）、超过约 10MB 的数据集规模保证。

---

## 核心设计理念

1. **零 Native 动态库依赖（Zero Native Dependency）**
   - 不依赖 RocksDB、SQLite、LMDB 等厚重的 C/C++ 共享库（无需携带 `.dll` / `.so` / `.dylib`）。
   - 各语言均为 100% 纯托管或标准工具链原生构建，彻底杜绝 P/Invoke、CGO 或 FFI 导致的崩溃与跨平台分发难题。
2. **纳秒级内存读取性能**
   - 数据常驻内存有序结构（Rust `BTreeMap`；Go / Kotlin / Dart 有序数组 + 二分查找；.NET 有序字典）。
   - 只读操作无系统调用、无磁盘 I/O、无数据反序列化，单次点查仅需数十纳秒。
3. **字节级跨语言确定性与互通性**
   - 统一内联查表 IEEE 802.3 CRC32 算法，统一 0..255 无符号 UTF-8 字节序。
   - 同一份 `.db` 文件可在 **Rust**、**.NET**、**Go**、**Kotlin**、**Dart** 之间自由热插拔无损互读互写。
4. **两代原子轮转自愈模型（Crash-Safe & Self-Healing）**
   - 写入采用 `.tmp -> .bak -> .db` 极速原子覆盖轮转，写放大为 0%。
   - 启动自动检测并晋升未竟临时文件；遭遇断电坏块时毫秒级自动从 `.bak` 备份恢复，杜绝静默清空数据。
5. **死锁防御的数据变更监听器（Watcher）**
   - 支持实时订阅键值的新增、修改、删除与命名空间清空。
   - 核心铁律：内部在持有写锁期间收集事件，在**写锁完全释放后**派发给监听回调，保证回调中重入读操作绝对安全、永不发生死锁。
6. **无锁只读快照隔离（Lock-Free Snapshot Isolation）**
   - 基于命名空间级写时复制（Root State CoW）实现纳秒级 $O(1)$ 瞬时快照创建，零深拷贝全库数据。
   - 快照读取完全不持有锁，主库高频并发写入与快照长耗时范围扫描**完全互不阻塞**，内存随引用计数确定性自动回收。

---

## 多语言实现矩阵

| 语言 | 目录 | 包名 / 引入方式 | 平台与运行时支持 | 状态 | 专属文档 |
| :--- | :--- | :--- | :--- | :---: | :--- |
| **.NET / C#** | [`src/dotnet/`](src/dotnet/) | `CarroKV` (NuGet) | `.NET Standard 2.0+`, `.NET 8.0+`, Mono, Unity | ✅ 已就绪 | [👉 .NET 使用与 API 文档](src/dotnet/README.md) |
| **Rust** | [`src/rust/`](src/rust/) | `carro_kv` (crates.io / Git) | `rustc 1.89+` (Windows, Linux, macOS) | ✅ 已就绪 | [👉 Rust 使用与 API 文档](src/rust/README.md) |
| **Go** | [`src/go/`](src/go/) | `github.com/mcxiaoke/CarroKV/src/go` | `Go 1.21+` (Windows, Linux, macOS) | ✅ 已就绪 | [👉 Go 使用与 API 文档](src/go/README.md) |
| **Kotlin** | [`src/kotlin/`](src/kotlin/) | `com.github.mcxiaoke:CarroKV` (JitPack) | `Kotlin 1.9+`, JVM 17+ (Android / Desktop / Server) | ✅ 已就绪 | [👉 Kotlin 使用与 API 文档](src/kotlin/README.md) |
| **Dart** | [`src/dart/`](src/dart/) | `ckv` (Git 直连, path: `src/dart`) | `Dart 3.0+`, Flutter (iOS / Android / Desktop) | ✅ 已就绪 | [👉 Dart / Flutter 使用与 API 文档](src/dart/README.md) |

---

## 核心功能一览

### 1. 常见数据类型强类型支持
CarroKV 对常见应用数据类型提供原生编码与强类型安全拦截：
- **Bytes** (`0x00`): 二进制原始字节流
- **String** (`0x01`): UTF-8 字符串（严格进行 Unicode 编码校验）
- **Int64** (`0x02`): 64 位带符号小端补码整数
- **Float64** (`0x03`): 64 位 IEEE 754 双精度浮点数（内置 NaN 规范化归一）
- **Bool** (`0x04`): 单字节布尔值

### 2. 实用核心 API
- `contains_key(ns, key)`: O(1) 高效判断键是否存在，避免反序列化与多余的内存拷贝。
- `count(ns)`: 获取指定命名空间下的有效条目总数。
- `clear_namespace(ns)`: 快速清空指定命名空间下的所有数据（保留命名空间名），触发原子快照落盘。
- `drop_namespace(ns)`: 彻底物理删除该命名空间及其所有键值。
- `increment_i64(ns, key, delta)`: 原子的 64 位整数累加/递减器。若键不存在自动初始化为 `delta`；若存在且为 `int64` 则累加；若类型不匹配抛出 `TypeMismatchError` 强类型拦截。

### 3. 事务批处理 (Batch)
支持纯内存暂存区（Staging Area）批处理。在批处理回调中执行的写操作在提交前均停留在暂存区，若发生异常或主动抛错则**全额丢弃回滚**；成功后原子合入主状态并落盘，最后向监听器派发事件。

批内可直接读取自己刚写入的值（Read-Your-Own-Writes）：暂存区优先，暂存删除/清空对命中键返回"不存在"，本批次未触碰的键回落到已提交状态，因此能在提交前做"读-判断-写"的条件分支。

### 4. 字典序范围扫描 (ScanRange & IterPrefix)
所有数据按 **无符号 UTF-8 字节序** 绝对升序存储，支持高效的 `keys(ns, prefix)` 前缀匹配与 `scan_range(ns, startKey, endKey)` 区间迭代。

### 5. 只读快照隔离 (Snapshot Isolation)
调用 `snapshot()` 可在纳秒级获得一个冻结在创建时刻的点时间只读视图（Point-in-Time Consistent View）。快照内部基于写时复制（CoW），读操作完全无锁，即便主库在后台高频写入并落盘，快照内的全库迭代查询也绝不受任何干扰或阻塞，是耗时报表计算、离线分析与全量导出的理想机制。

---

## 引擎模式与默认值（选型指引）

CarroKV 的**默认值是"可靠性优先"的保守组合**：`SnapshotAtomic` + `Always`。每次写入都把当前存活数据序列化为完整快照，`fsync` 后经 `.tmp → .bak → .db` 原子轮转替换主库，磁盘上任意时刻不存在"新库写了一半"的状态，断电最多丢失正在进行的这一次写。它是慢的（每次写入 O(N) 序列化 + 一次 fsync），也是最不容易出错的。

| 场景 | 推荐配置 | 说明 |
| :--- | :--- | :--- |
| 配置、凭证、账号状态、桌面 App 本地状态（数据量 < 2MB） | **默认值**（`SnapshotAtomic` + `Always`） | 可靠性与实现复杂度最优，无需调参 |
| 高频写入缓存、离线事件日志、会话数据（10 ~ 50 MB，且能接受周期性暂停） | `engineMode = AppendOnlyLog`（`Deferred` 或 `Always`） | 追加帧写入，稳态 0.18 ms/写、与库大小无关；垃圾量达到 `max(4MB, 存活/4)` 时压实一次，暂停 ≈4 ms/MB（50 MB 实测 p99 287 ms） |
| 可容忍进程崩溃丢 100ms 数据的批量导入 | `AppendOnlyLog` 或 `SnapshotAtomic` + `Deferred`（`syncIntervalMs = 100`） | 吞吐最高，但进程异常退出会丢失最近一个刷盘周期的写入 |

### ⚠️ 默认引擎的写延迟随库大小线性增长（实测）

`SnapshotAtomic` 每次写入都会**全量序列化整库并 fsync**，代价是 O(库大小)。这是默认值，
但库本身**没有任何运行时护栏或告警**阻止你把它用在远大于建议规模的库上。下表为实跑的
稳态写延迟中位数：

| 存活条目 | `SnapshotAtomic` + `Immediate` | `AppendOnlyLog` |
| ---: | ---: | ---: |
| 500 | 2.29 ms（≈437 ops/s） | 0.18 ms（≈5,500 ops/s） |
| 2,000 | 2.93 ms（≈341 ops/s） | 同上 |
| 5,000 | 4.43 ms（≈226 ops/s） | 同上 |
| ~50,000（外推，约 5.7 MB） | ~40 ms（≈25 ops/s） | 同上 |

`AppendOnlyLog` 的稳态写延迟**与库大小无关**（同上，右列不随行变化）。按此斜率外推，
把默认模式用在 50 MB 库上会比 `AppendOnlyLog` 慢约两个数量级。

> **结论：数据量超过约 2 MB，或存在高频写入时，请显式设置
> `engineMode = AppendOnlyLog`。** 各语言 `DbOptions.EngineMode` 的字段文档里也有同一条硬警告。

---

## 项目文档导航

- [📖 CKV 跨语言共有接口统一规范 (docs/API-SPEC.md)](docs/API-SPEC.md)：五门语言统一方法签名、选项、快照、监听器与错误体系映射表。
- [🚀 开发、测试与发布全流程指南 (docs/DEVELOPMENT-AND-RELEASE-GUIDE.md)](docs/DEVELOPMENT-AND-RELEASE-GUIDE.md)：开发环境、单语言测试、Golden Vectors、五语言 Interop 校验及全平台发布 Playbook。
- [📐 二进制核心设计规范 v3.2 (docs/KV-DB-DESIGN-SPEC.md)](docs/KV-DB-DESIGN-SPEC.md)：磁盘帧格式、锁模型、确定性贪心装箱算法与跨语言行为准则。
- [📊 规模与延迟性能基准测试 (docs/BENCHMARKS-20261017.md)](docs/BENCHMARKS-20261017.md)：2/10/50/100 MB 数据规模下的打开速度、稳态写延迟与压实暂停实测数据。
- [🐛 全项目五语言深度代码审查报告 (docs/REVIEW-20261017-201309-full-code-review.md)](docs/REVIEW-20261017-201309-full-code-review.md)：5 门语言约 20000 行生产代码的逐行审查，**7 项 P0 / 40 项 P1 / 47 项 P2 / 40 项 P3** 分级缺陷清单，附 12 组独立探针实测取证与六批次修复路线图。已合并外部集成反馈（含一次真实线上崩溃的现场日志）。
- [📝 历史设计与变更记录 (docs/CHANGES-20261017.md)](docs/CHANGES-20261017.md)：工程重构、CI 稳定性加固与多语言特性追踪记录。
