# CKV (Cross-language Key-Value Database)

**CKV** 是一个轻量级、跨语言、纯单文件嵌入式 Key-Value 数据库引擎，严格遵循 [CKV v3.2 二进制设计规范](docs/KV-DB-DESIGN-SPEC.md)。

它专为桌面客户端、移动端、边缘计算以及轻量微服务打造，面向**小型配置/状态数据集**（单进程、通常 < 2MB、读多写少）。

> **状态：pre-release（0.1.0）**。磁盘格式已冻结为 v1.0，但 API 与实现仍在修复批次中；发布门禁通过后版本号才升至 `1.0.0`。
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
   - 同一份 `.db` 文件可在 **Rust**、**Go**、**.NET** 之间自由热插拔无损互读互写。
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

| 语言 | 目录 | 平台 / 运行时支持 | 状态 | 说明文档 |
| :--- | :--- | :--- | :--- | :--- |
| **.NET / C#** | `src/dotnet/` | `.NET Standard 2.0+`, `.NET 8.0+`, Mono, Unity | ✅ 已就绪 (100% 托管) | [👉 .NET 使用与 API 文档](src/dotnet/README.md) |
| **Rust** | `src/rust/` | `rustc 1.89+` (Windows, Linux, macOS) | ✅ 已就绪 (标准库轻量实现) | [👉 ckv crate 说明](src/rust/README.md) |
| **Go** | `src/go/` | `Go 1.21+` (Windows, Linux, macOS) | ✅ 已就绪 (标准库纯 Go) | *(待 API 稳定后补充)* |
| **Dart** | `src/dart/` | `Dart 3.0+`, Flutter (VM / 原生) | ✅ 已就绪 (纯 Dart/单线程安全) | *(待 API 稳定后补充)* |
| **Kotlin** | `src/kotlin/` | `Kotlin 1.9+`, JVM 17+ (Windows, Linux, macOS) | ✅ 已就绪 (纯 Kotlin/JVM 标准库) | *(待 API 稳定后补充)* |

---

## 核心功能一览

### 1. 常见数据类型强类型支持
CKV 对常见应用数据类型提供原生编码与强类型安全拦截：
- **Bytes** (`0x00`): 二进制原始字节流
- **String** (`0x01`): UTF-8 字符串（严格进行 Unicode 编码校验）
- **Int64** (`0x02`): 64 位带符号小端补码整数
- **Float64** (`0x03`): 64 位 IEEE 754 双精度浮点数（内置 NaN 规范化归一）
- **Bool** (`0x04`): 单字节布尔值

### 2. 实用 API (P0)
- `contains_key(ns, key)`: O(1) 高效判断键是否存在，避免反序列化与多余的内存拷贝。
- `count(ns)`: 获取指定命名空间下的有效条目总数。
- `clear_namespace(ns)`: 快速清空指定命名空间下的所有数据并触发原子快照落盘。
- `increment_i64(ns, key, delta)`: 原子的 64 位整数累加/递减器。若键不存在自动初始化为 `delta`；若存在且为 `int64` 则累加；若类型不匹配抛出 `TypeMismatchError` 强类型拦截。

### 3. 事务批处理 (Batch)
支持纯内存暂存区（Staging Area）批处理。在批处理回调中执行的写操作在提交前均停留在暂存区，若发生异常或主动抛错则**全额丢弃回滚**；成功后原子合入主状态并落盘，最后向监听器派发事件。

批内可直接读取自己刚写入的值（Read-Your-Own-Writes）：暂存区优先，暂存删除/清空对命中键返回"不存在"，本批次未触碰的键回落到已提交状态，因此能在提交前做"读-判断-写"的条件分支。

### 4. 字典序范围扫描 (ScanRange)
所有数据按 **无符号 UTF-8 字节序** 绝对升序存储，支持高效的 `keys(ns, prefix)` 前缀匹配与 `scanRange(ns, startKey, endKey)` 区间迭代。

### 5. 只读快照隔离 (Snapshot Isolation)
调用 `snapshot()` / `CreateSnapshot()` 可在纳秒级获得一个冻结在创建时刻的点时间只读视图（Point-in-Time Consistent View）。快照内部基于写时复制（CoW），读操作完全无锁，即便主库在后台高频写入并落盘，快照内的全库迭代查询也绝不受任何干扰或阻塞，是耗时报表计算、离线分析与全量导出的理想机制。

---

## 引擎模式与默认值（选型指引）

CKV 的**默认值是"可靠性优先"的保守组合**：`SnapshotAtomic` + `Immediate`。每次写入都把当前存活数据序列化为完整快照，`fsync` 后经 `.tmp → .bak → .db` 原子轮转替换主库，磁盘上任意时刻不存在"新库写了一半"的状态，断电最多丢失正在进行的这一次写。它是慢的（每次写入 O(N) 序列化 + 一次 fsync），也是最不容易出错的。

| 场景 | 推荐配置 | 说明 |
| :--- | :--- | :--- |
| 配置、凭证、账号状态、桌面 App 本地状态（数据量 < 2MB） | **默认值**（`SnapshotAtomic` + `Immediate`） | 可靠性与实现复杂度最优，无需调参 |
| 高频写入缓存、离线事件日志、会话数据（1MB ~ 100MB） | `engineMode = AppendOnlyLog` + `Immediate` | 追加帧写入，仅在跨过 `compactionThresholdBytes`（默认 4MB）时压实 |
| 可容忍进程崩溃丢 100ms 数据的批量导入 | `AppendOnlyLog` 或 `SnapshotAtomic` + `Deferred`（`syncIntervalMs = 100`） | 吞吐最高，但进程异常退出会丢失最近一个刷盘周期的写入 |

已知取舍（有实测数据支撑，非推测）：

- `SnapshotAtomic` + `Immediate` 是最慢的路径。同日基准中 Rust 约 253 ops/s、WAF 54x（写 200 次时 Go/.NET 的 WAF 可达 107x）。
- `AppendOnlyLog` 模式下 `Deferred` 相对 `Immediate` 的实测提升约 **+233%**（约 1,500 → 5,000 ops/s，Windows 本机、每键 1KB），差距主要来自每写 fsync。历史上的 "15%~31%" 声明与实测不符，已更正。
- 库内常被引用的 "68 万 ops/s" 是 **`AppendOnlyLog` + `Manual` 的批量口径**：5,000 次写只在内存暂存、结束时一次 flush，数据集约 640KB。它不是每写吞吐，也不是持久化吞吐；同口径下 redb `Durability::None` 为 109 万 ops/s。

因此：**需要吞吐时请显式选择 `AppendOnlyLog`，不要依赖默认值**。默认值不会为了跑分而牺牲掉电安全。

### 已知限制

| 限制 | 表现 | 说明 |
| :--- | :--- | :--- |
| 多进程并发读写同一库 | 第二个打开者等待 `lockTimeoutMs` 后返回 `Locked` | 设计边界，不做进程间并发协调 |
| 数据集 > 约 10MB | 可运行，但打开时间、内存驻留（约文件大小的 2.2 倍）与单写延迟不再有承诺 | 全量驻留内存 + 全量快照重写的架构后果 |
| .NET 在 Linux 上的目录 fsync | 轮转后的目录项落盘不可用（托管 API 缺失），掉电可能回滚最后一次轮转；`.bak` 保留上一代 | 触发 `onDirSyncUnavailable` 告警；Rust/Go 完整 |
| 压实期间的读延迟 | 压实是写锁内的 O(存活) 全量重写，读 p99 随库大小上升 | 与数据规模相关，见 `docs/KV-DB-DESIGN-SPEC.md` §4.5 |

五语言默认值矩阵（一致性由跨语言测试守护）：

| 选项 | Rust | Go | .NET | Kotlin | Dart |
| :--- | :---: | :---: | :---: | :---: | :---: |
| `lockTimeoutMs` | 10000 | 10000 | 10000 | 10000 | 10000 |
| `syncMode` | Immediate | Immediate | Immediate | Immediate | Immediate |
| `syncIntervalMs` | 100 | 100 | 100 | 100 | 100 |
| `engineMode` | SnapshotAtomic | SnapshotAtomic | SnapshotAtomic | SnapshotAtomic | SnapshotAtomic |
| `compactionThresholdBytes` | 4 MB | 4 MB | 4 MB | 4 MB | 4 MB |

---

## 项目导航

- [CKV 核心设计规范 (v3.2)](docs/KV-DB-DESIGN-SPEC.md)：详细规定磁盘帧格式、锁模型、确定性贪心装箱算法、黄金测试向量与跨语言行为准则。
- [API v1.0 冻结基线](docs/API-V1-FREEZE.md)：跨语言 API 契约与已裁决的行为差异。
- [.NET 说明文档与完整 API 指南](src/dotnet/README.md)：针对 .NET 开发者的快速上手指南、代码示例与异常体系说明。
- [Rust crate 说明](src/rust/README.md)：定位、示例、MSRV 与明确不支持的场景。
