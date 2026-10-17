# CKV 存储引擎缺口分析与改进方案技术报告

> **报告日期**: 2026-10-16 21:20:00  
> **评估基线**: `dev` 分支最新提交 (全量 332 项单元测试 100% 通过)  
> **涉及语言**: Rust (1.85+), Go (1.24+), .NET (C# 12 / .NET 8.0), Dart (3.0+), Kotlin (JVM 17+)  
> **对标参考**: RocksDB, Pebble, BadgerDB, LMDB, bbolt, SQLite, 微信 MMKV, Redis  

---

## 1. 评估体系与工程数据基线

### 1.1 对标坐标系界定

评估 CKV 是否达到“业界主流标准”，必须建立在清晰的架构定位与分类基准之上：

1. **主流通用/服务器级磁盘 KV 引擎（RocksDB, Pebble, BadgerDB, LMDB, bbolt）**：
   - **核心特征**：支持超出物理内存的数据集（Out-of-Core）、磁盘索引树/分层 SSTable、组提交（Group Commit）、Page-based 存储或 B-Tree 空闲页原地复用、多版本可串行化事务（MVCC/SSI）。
   - **对标结论**：**尚未达到标准**。CKV 的架构模型为全量常驻内存，不支持磁盘换页与跨批冲突事务，二者存在架构代差。
2. **轻量级客户端/嵌入式状态存储（微信 MMKV, Hive / Hive CE, Bitcask, Redis 单机 RDB/AOF）**：
   - **核心特征**：专为桌面端与移动端设计；无 Native 动态库依赖；数据常驻内存或 mmap 映射；增量追加与快照恢复；低内存与低 CPU 占用；单文件便携。
   - **对标结论**：**已达到该细分领域的工业级可用水准**。

### 1.2 全语言工程体量与测试覆盖度（实测统计）

截至 2026-10-16，项目全部 5 种语言实现的代码量与测试执行状态如下：

| 语言实现 | 源码文件数 | 源码总行数 (LOC) | 单元测试文件数 | 测试总行数 (LOC) | 测源比例 | 测试通过率（本地实测） |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| **Rust** | 8 | 3,230 | 23 | 3,531 | **1.09 : 1** | **91 passed**, 1 ignored (0.00s) |
| **Go** | 9 | 3,088 | 18 | 4,077 | **1.32 : 1** | **51 passed** (7.75s) |
| **.NET (C#)** | 9 | 3,754 | 19 | 3,853 | **1.03 : 1** | **92 passed** (4.00s) |
| **Dart** | 11 | 2,178 | 11 | 1,520 | 0.70 : 1 | **47 passed** (1.00s) |
| **Kotlin** | 11 | 2,039 | 11 | 1,537 | 0.75 : 1 | **51 passed** (33.0s) |
| **全库汇总** | **48** | **14,289** | **82** | **14,518** | **1.01 : 1** | **332 passed, 0 failed** (100% 通过) |

### 1.3 核心性能与写放大实测数据

- **强持久化落盘 (`SyncMode::Immediate`, 每次写入同步调用 `fsync`)**：
  - Rust 吞吐量：**446 ops/s**（平均延迟 2.24 ms，P99 2.95 ms，WAF 1.28x）；
  - Go 吞吐量：**1,029 ops/s**（平均延迟 0.96 ms，P99 2.01 ms，WAF 1.28x）；
  - .NET 吞吐量：**439 ops/s**（平均延迟 2.28 ms，P99 3.01 ms，WAF 1.28x）。
- **后台周期落盘 (`SyncMode::Deferred`, 100ms 周期写合并)**：
  - 快照写合并吞吐：**251,149 ~ 549,129 ops/s**（平均延迟 0.35 ~ 0.99 μs）；
  - WAL 增量写吞吐：**513 ~ 2,558 ops/s**。
- **空间压实收缩率 (`Compaction`)**：
  - 1,000 次覆写后 WAL 体积从 110,032 字节压缩至 142 字节，缩减率 **99.9%**，耗时 3.42 ms。

---

## 2. 核心缺口深度剖析 (Gaps Analysis)

### 2.1 工程架构层面缺口

#### 缺口 1：内存容量天花板与 $O(N)$ 冷启动加载开销
- **代码位置**：
  - Rust: `src/rust/src/db.rs:90` (`data: BTreeMap<Vec<u8>, Arc<BTreeMap<Vec<u8>, (u8, Vec<u8>)>>>`)
  - Rust: `src/rust/src/format.rs:275-420` (`load_with_recovery`)
  - Go: `src/go/db.go:39` (`namespaces map[string]*NamespaceStore`)
  - Kotlin: `src/kotlin/src/main/kotlin/ckv/Storage.kt:19` (`TreeMap<ByteArray, InternalEntry>`)
- **机理与限制**：
  1. 所有有效键值对均以语言原生堆对象常驻于 RAM 中。单库数据量受限于物理可用内存，若尝试写入几 GB 数据，将直接导致宿主进程 OOM；
  2. 启动打开数据库时，`FrameLoader` 必须从物理偏移量 32 开始，顺序读取并反序列化整个 `.db` 文件的所有历史或存活帧。
- **与主流引擎差距**：
  RocksDB / LMDB / bbolt 冷启动仅需读取几个元数据页（`Manifest` 或 `Meta Page`），耗时固定在毫秒级，且数据按需加载进 Page Cache，支持远大于内存的数据集。

#### 缺口 2：无 Group Commit 与单写者物理 fsync 吞吐硬上限
- **代码位置**：
  - Rust: `src/rust/src/db.rs:1650-1662` (`append_entries`)
  - Go: `src/go/db.go:1701-1715` (`appendEntries`)
- **机理与限制**：
  在 `Immediate` 模式下，写路径在独占锁保护下按需打开文件追加写入并调用 `fsync`。在 NVMe SSD 上，单次系统级 `fsync` 耗时约 1~2 ms，且无并发写批次合并（Group Commit），单写者极限吞吐被物理介质延迟限制在 500~1,000 ops/s。
- **与主流引擎差距**：
  RocksDB / LevelDB / MySQL / PostgreSQL 采用 Group Commit 机制：多个并发写入线程将操作排入写队列，由单一 Leader 线程合并为单个物理批次执行一次 `fsync`，将数千并发写者的落盘成本平摊，吞吐可提升 10~50 倍。

#### 缺口 3：全库全量快照压实（Full Rewrite Compaction）与写停顿毛刺
- **代码位置**：
  - Rust: `src/rust/src/db.rs:1880-1925` (`compact_internal`)
  - Go: `src/go/db.go:1630-1675` (`compactInternal`)
- **机理与限制**：
  压实逻辑是全量遍历内存中所有存活键值对，按 UTF-8 字节序重写为完整的 `.tmp` 快照文件，并覆盖替换主库。
  1. 压实开销与**全库数据总量 $N$** 严格成正比。若库内有 50MB 存活数据，即便仅由于高频覆写导致增量追加了 4MB 日志，压实仍需对全部 50MB 数据做完整序列化与落盘，导致数百毫秒的 I/O 阻塞；
  2. 压实发生在写操作调用栈中，导致触发压实的那一次写入延迟出现长尾毛刺（Compaction Stall）。
- **与主流引擎差距**：
  RocksDB 采用 Leveled Compaction，每次仅对局部 2MB~64MB 的 SSTable 进行增量合并；LMDB / bbolt 基于 B-Tree 空闲页链表（Freelist）原地覆写，无需任何 Compaction。

#### 缺口 4：跨进程并发能力受限（独占内核锁）
- **代码位置**：
  - Rust: `src/rust/src/lock.rs:20-80`
  - Go: `src/go/lock_windows.go` / `src/go/lock_unix.go`
  - .NET: `src/dotnet/src/Ckv/LockFile.cs`
- **机理与限制**：
  读写打开均通过操作系统内核文件锁（flock / LockFileEx / FileShare）排他锁定整库。
  1. 不支持多进程同时并发写；
  2. 不支持跨进程“一写多读”（写者持排他锁，只读进程打开直接被拒绝并抛出 `LockedError`）。
- **与主流引擎差距**：
  SQLite 支持跨进程 WAL 模式下的一写多读；LMDB 支持跨进程多读者与单写者完全并发。

---

### 2.2 功能完整性层面缺口

#### 缺口 5：`BatchWriter` 缺少批内“读已写”（Read-Your-Own-Writes）
- **代码位置**：
  - Rust: `src/rust/src/db.rs:1370-1460` (`BatchWriter`)
  - Go: `src/go/db.go:1180-1250`
  - .NET: `src/dotnet/src/Ckv/Db.cs:1900-1950`
- **机理与限制**：
  当前 `BatchWriter` 仅提供 `put_bytes`, `put_string`, `delete`, `clear_namespace` 等写入方法，**未提供 `get` 接口**。在 `batch(fn)` 回调中无法读取当前批次已写入的中间值，也无法在批次内基于已有数据做条件校验分支。
- **与主流引擎差距**：
  RocksDB 的 `WriteBatchWithIndex`、LMDB / bbolt 的读写事务 `Tx` 均支持在事务提交前读取自身暂存的未提交修改。

#### 缺口 6：缺少磁盘流式迭代器（Disk Iterator / Cursor）
- **代码位置**：
  - Rust: `src/rust/src/db.rs:430-490` (`scan_range`)
- **机理与限制**：
  范围扫描是直接在内存有序树（BTreeMap / 红黑树）上做内存迭代。无法做到在不把全部数据读入内存的前提下，按块从磁盘流式读取一段范围的数据。
- **与主流引擎差距**：
  主流引擎的迭代器是磁盘驱动的游标（Cursor），通过二分定位 SSTable Block 或 B-Tree 节点，内存占用恒定，可遍历海量数据集。

#### 缺口 7：缺少数据块压缩（Compression）与前缀编码
- **机理与限制**：
  CKV 当前在磁盘帧中存储的是未经压缩的原始键值字节流。当值包含大量文本、JSON 或结构化数据时，磁盘占用和写放大均未利用现代轻量压缩算法（如 LZ4、ZSTD、Snappy）进行优化。

#### 缺口 8：缺少 TTL（键生存时间）与自适应空间回收
- **机理与限制**：
  1. 不支持为 Key 指定生命周期（TTL），无法自动过期淘汰历史时序或会话数据；
  2. 压实触发阈值仅由物理文件大小（`CompactionThresholdBytes`）决定。若用户删除了库中 90% 的键，物理文件由删除墓碑占据但总大小未跨过阈值时，引擎**永远不会自动触发压实**，无效空间无法自适应回收。

---

### 2.3 可靠性与容灾深水区缺口

#### 缺口 9：POSIX 目录元数据持久化缺口（Directory fsync Gap）
- **代码位置**：
  - .NET: `src/dotnet/src/Ckv/Db.cs:976-1022` (`AtomicReplace`)
  - Dart: `src/dart/lib/src/db.dart:310-330`
  - Kotlin: `src/kotlin/src/main/kotlin/ckv/FileRotation.kt`
- **机理与风险**：
  Rust 和 Go 已在 Unix 平台补齐了原子替换后的 `fsync_parent_dir`，但 .NET、Dart、Kotlin 在完成 `rename(tmp, db)` 后均未对父目录描述符执行 `fsync`。在 Linux ext4 / XFS 上，若重命名后瞬间整机掉电，重启后可能丢失新目录项，导致文件回滚至重命名之前。

#### 缺口 10：缺少断电与系统崩溃故障注入实证体系（VFS Crash Injection）
- **机理与风险**：
  当前测试主要覆盖坏字节探测、主动截断坏尾与单例选项冲突验证。缺少类似 SQLite `test_vfs` 或 Linux `dm-flakey` / `dm-dust` 的硬件级崩溃模拟层，无法验证“在任意 I/O 写入切片中途掉电、文件系统元数据撕裂、`fsync` 返回 EIO 失败”等严苛场景下数据库的自愈行为。

#### 缺口 11：长生命周期快照引发的写时复制内存膨胀（CoW Memory Spike）
- **代码位置**：
  - Rust: `src/rust/src/db.rs:528` (`Arc::make_mut(ns_arc)`)
- **机理与风险**：
  快照隔离基于命名空间粒度的状态克隆（Namespace-level CoW）。若某个命名空间包含 50 万条数据，而上层业务持有一个只读快照未及时释放，主库对该命名空间进行高频单键写入时，每次写入均会触发对整个 BTreeMap 的克隆，导致物理内存瞬时暴增和 GC 压力。

---

## 3. 建议改进方案与实施路线图

为了保持 CKV **“100% 纯原生、零外部 Native 动态库依赖、多语言字节级互通”** 的核心特色，改进方案分为两期：

```
+--------------------------------------------------------------------------------+
|                             CKV 技术演进路线图                                  |
+--------------------------------------------------------------------------------+
                                        │
           +----------------------------+----------------------------+
           │                                                         │
           ▼                                                         ▼
【阶段一：轻量增强与可靠性加固 (v1.1)】               【阶段二：架构扩展与可选能力 (v2.0)】
- 补齐 .NET/Dart/Kotlin 目录 fsync                 - 组提交 (Group Commit) 写合并管道
- BatchWriter 支持批内读已写 (Get)                  - 命名空间内分段增量压实 (Segmented Compaction)
- 引入死键比例感知的自适应压实触发器                  - 纯原生轻量块级压缩 (LZ4/Deflate)
- 内存配额反压控制 (Memory Budget)                 - 可选的跨进程单写多读协议 (IPC Read Sharing)
- 虚拟 I/O 故障注入模拟测试套件                      - 键过期 TTL 后台淘汰器
```

---

### 3.1 阶段一：轻量增强与可靠性加固 (v1.1)

本阶段**不破坏现有二进制格式与核心架构**，以极高的性价比补齐当前主要短板。

#### 改进 1：补齐跨语言 POSIX 目录 `fsync`
- **目标**：彻底消除断电后元数据未落盘导致的文件丢失风险。
- **实施方案**：
  - **.NET**：在 Linux/macOS 下通过 P/Invoke 或 POSIX API (`open(dir, O_RDONLY)` -> `fsync(dirFd)` -> `close(dirFd)`)；
  - **Kotlin (JVM)**：使用 `FileChannel.open(dirPath, StandardOpenOption.READ).force(true)` 刷盘父目录；
  - **Dart**：对 `File(path).parent` 增加尽力而为的目录同步尝试。

#### 改进 2：`BatchWriter` 增加批内“读已写”支持
- **目标**：允许事务回调读取自身暂存的未提交数据。
- **实施方案**：
  - 在 `BatchWriter` 内存结构中除保留 `ops: Vec<BatchOp>` 外，维护一个批内轻量索引 `staging: Map<(ns, key), (val_type, bytes)>`；
  - 增加 `get_bytes(ns, key)`、`get_string(ns, key)`：先在 `staging` 中查找；若命中墓碑则返回空；若未命中则回退查询主库当前快照；
  - 保持写入时暂存，异常全额回滚。

#### 改进 3：自适应压实触发器（文件大小 + 删键比例感知）
- **目标**：解决大批量删键后无效空间无法回收的问题。
- **实施方案**：
  - 在实例运行时维护物理文件估算大小 `estimatedFileSize` 与累计有效载荷大小 `livePayloadSize`；
  - 当满足以下任一条件时触发自动压实：
    1. 物理大小超过 `compactionThresholdBytes`；
    2. 物理大小大于一定基线（如 2MB）且无效比例 `(fileSize - livePayloadSize) / fileSize > 0.5`（死数据超过 50%）。

#### 改进 4：内存上限配额与反压保护（Memory Budget Guard）
- **目标**：防止受限环境（如移动端或边缘网关）因数据暴增导致整机 OOM。
- **实施方案**：
  - `DbOptions` 新增 `maxMemoryBudgetBytes: uint64`（默认 0 不限制）；
  - 写入前粗略统计增量内存（键长 + 值长 + 节点元数据预估），超过预算时抛出 `OutOfMemoryLimitError` 或阻塞等待只读快照释放。

#### 改进 5：构建 VFS 级软件故障注入测试套件
- **目标**：在不依赖物理断电的前提下，实证检验写入中途崩溃一致性。
- **实施方案**：
  - 抽象文件 I/O 适配层（Rust `trait FileIo`、Go `interface FileSystem`）；
  - 实现故障注入包装器：支持设置“在写入第 $N$ 个字节后模拟内核崩溃（即刻终止写入）”、“在 `fsync` 时模拟磁盘 EIO 报错”；
  - 验证数据库在各类不完整状态下的自动截断自愈与 `CorruptedError` 拦截正确性。

---

### 3.2 阶段二：架构扩展与可选能力 (v2.0)

本阶段面向更严苛的性能与功能需求，供未来按需选型演进。

#### 改进 6：组提交（Group Commit）写合并管道
- **适用场景**：多线程高频调用 `SyncMode::Immediate` 强持久化写入。
- **实施方案**：
  - 引入无锁 MPSC（多生产者单消费者）请求队列；
  - 多个业务线程提交写请求后挂起等待通道信号；
  - 后台唯一的 Writer 线程批量提取队列中所有待写帧，单次系统调用连续写入磁盘并执行一次 `fsync`；
  - 写入成功后并行唤醒所有等待中的写线程。可将强持久化吞吐提升至 10,000~30,000 ops/s。

#### 改进 7：分段日志与局部压实（Segmented WAL & Partial Compaction）
- **适用场景**：单库数据量扩大至 50MB~500MB，全量快照重写延迟过高。
- **实施方案**：
  - 将单文件拆分为固定大小（如 16MB）的段文件（Segment Files：`00001.log`, `00002.log`）；
  - 压实仅针对已经写满的历史段文件进行合并，活跃段文件保持写入，消除全库全量快照的长时间写停顿。

#### 改进 8：纯原生轻量数据压缩（Optional Block Compression）
- **适用场景**：大量文本、JSON 或日志类数据存储。
- **实施方案**：
  - 在帧头中引入 `CompressionType` 标志位（`0x00=None`, `0x01=LZ4`, `0x02=Deflate`）；
  - 五语言均使用标准库或纯原生轻量实现（无需 C/C++ 动态库绑定）；
  - 当单个 BatchFrame 的 Payload 大于阈值（如 4KB）时自动压缩载荷。

---

## 4. 架构取舍与防退化原则 (What NOT to do)

在推进任何改进时，必须坚守 CKV 的核心设计边界，防止为了盲目追求功能而破坏产品定位：

1. **坚决不引入任何 C/C++ Native 动态库**：
   不为了追求极致压缩或 B-Tree 而引入 SQLite/RocksDB 的 C 共享库。100% 纯原生和跨平台无缝部署是 CKV 最核心的差异化优势。
2. **保持二进制规范跨语言 100% 互通**：
   任何格式变更（如压缩标志、段文件）必须严格在《Binary Format Specification》中统一版本号，并通过多语言黄金测试向量验证后方可合并。
