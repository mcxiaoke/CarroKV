# CKV 只读快照隔离（Snapshot Isolation）技术设计规范

## 1. 设计目标与核心契约

在嵌入式 Key-Value 数据库应用场景中，**只读快照隔离（Snapshot Isolation / Read-Only Transaction）** 用于向调用方提供冻结在某一逻辑时刻的**时间旅行点（Point-in-Time）的一致性数据视图**。典型业务场景包括：跨表批量分析报表、全库数据校验与导出、后台异步备份以及耗时较长的范围查询。

为达到工业级可靠性，只读快照隔离必须严格满足以下四大铁律：

1. **读写完全互不阻塞（Non-blocking Readers and Writers）**：
   快照进行全库范围迭代无论耗时多久，**绝对不能阻塞主数据库的任何并发写入**；主库的并发写入也**绝对不能阻塞快照的读操作**。
2. **极速创建（Zero-Deep-Copy Creation）**：
   创建快照必须是 $O(1)$ 的瞬时轻量操作（耗时控制在数十纳秒级别），**严禁对整库数据执行全量深拷贝**。
3. **确定性内存垃圾回收（Deterministic Garbage Collection）**：
   快照生命周期结束被释放（Drop/Dispose/Close）后，它持有的已被主库覆写或删除的旧版本数据，必须能够被确定性自动释放，杜绝内存泄漏。
4. **与规范 §1.2 存储演进完全兼容（Phase-2 Ready）**：
   该机制完全建立在内存并发视图层，与底层的磁盘落盘形态（阶段一的全量轮转、阶段二的增量追加写）彻底解耦，并天然为阶段二的“后台无锁异步 Compaction”提供基础设施。

---

## 2. 业界主流引擎实现对比与架构选型

我们深度核对了知名开源存储引擎的快照实现：

| 引擎 | 架构类型 | 快照隔离机制 | 优点 | 在 CKV 中的适用性分析 |
| :--- | :--- | :--- | :--- | :--- |
| **RocksDB / LevelDB** | LSM-Tree | **MVCC 逻辑序列号过滤**：每个条目带 `SeqNo`，快照记录 `SnapshotSeqNo`。读取时遍历跳表过滤 `SeqNo <= SnapshotSeqNo`。 | 写入快，天然与 LSM 追加写结合 | **淘汰**。需要全局追踪 `min_snapshot_seq` 做复杂的 Compaction 水位修剪，在全内存纯原生多语言实现下容易引发悬垂或内存膨胀。 |
| **redb / LMDB** | B-Tree | **CoW 路径复制**：快照持久化持有当时的根页面指针（`Root Page ID`）。 | 事务级别高，读完全无锁 | **淘汰**。Page-FreeList 与物理页面重分配机制过于厚重，破坏 CKV 跨语言 100% 极简纯原生的定位。 |
| **CKV 选用方案** | 内存有序索引 | **命名空间级写时复制状态置换（Namespace-Level CoW with Shared References）**：主库状态由只读结构体与智能指针持有；创建快照仅复制指针引用；写入时对受影响命名空间写时克隆。 | **创建耗时 < 10ns**、读彻底无锁、全语言标准库天然支持（Rust `Arc`、Go 指针/GC、.NET 引用/GC），旧内存随引用计数自动清空。 | **完美当选**。极简、鲁棒、无死锁、无版本链维护负担。 |

---

## 3. CKV 架构实现方案：状态写时复制（Root State CoW）

### 3.1 核心数据结构模型

```
主数据库 (CkvDb)
  │
  └── 写锁保护的内部状态指针 (state)
            │
            ├──> [DbState (TxID: 100)] 引用计数 = 2 (主库 + Snapshot A 共享)
            │          ├── Namespace "users"   ──> Arc<NamespaceStore> (A)
            │          └── Namespace "orders"  ──> Arc<NamespaceStore> (B)
            │                                             ▲
快照实例 (Snapshot A)                                      │
  └── 持有独立持有的 DbState 引用 ───────────────────────────┘
```

1. **`NamespaceStore`（只读共享）**：内部保存有序条目列表或红黑树。对外暴露无锁只读检索接口。
2. **`DbState`（只读状态快照）**：
   - `TxID`: 当前状态对应的最大事务序列号；
   - `Namespaces`: 命名空间字典 `Map<String, Arc<NamespaceStore>>`。
3. **`Snapshot`（快照实例）**：
   - 持有独立克隆的 `Arc<DbState>`，与主库解耦生命周期。
   - 实现只读接口，**完全不提供任何写方法**。

### 3.2 快照生命周期全流程

#### 1. 创建快照（$O(1)$ 纳秒级）
- 主库短暂获取读锁（或原子加载 `state` 指针）；
- 拷贝当前 `state` 的引用（Rust: `Arc::clone(&self.state)`；Go/C#: 增加引用持有）；
- 构建 `Snapshot` 对象返回。
- **开销**：仅几个 CPU 指针赋值指令，耗时约 5~15 纳秒，**0 字节数据复制**。

#### 2. 快照只读访问（Lock-Free 无锁读取）
- 快照执行 `get_string`、`contains_key`、`scan_range` 等只读操作时，**绝对不向主库申请任何读写锁（RwLock）**。
- 直接遍历其私有持有的不可变 `NamespaceStore`，达到纯单线程级别的内存直读极限吞吐。

#### 3. 主库并发写与分支合并（Namespace-Level CoW）
- 当主库发生 `Put`、`Delete`、`Batch` 时：
  1. 写线程获取主库写锁；
  2. 复制当前 `state.namespaces` 的映射字典（浅拷贝，仅复制几到几十个指针）；
  3. 若本次写操作只修改了 `orders` 命名空间：
     - 若该 `orders` 命名空间的引用计数大于 1（说明正被 Snapshot A 引用）：**执行写时克隆**，为写操作复制一份独立的私有 `NamespaceStore` 并在其上修改；
     - 若未被修改的 `users` 命名空间：指针直接保留共享，不产生任何额外内存开销；
  4. 写操作完成并落盘 `.tmp -> .bak -> .db` 成功后：
     - 生成新的 `DbState (TxID: 101)`，原子替换主库持有的 `state`；
  5. 释放主库写锁。

#### 4. 资源销毁与垃圾回收（Deterministic Cleanup）
- Snapshot A 退出其作用域（Rust `Drop`、Go `Close()`、.NET `Dispose()`）：
  - 释放对旧版 `DbState (TxID: 100)` 的引用；
  - 旧版 `orders` 的物理内存因引用计数归零而被操作系统/运行时垃圾回收；
  - 没有任何活跃快照时，内存中仅保留最新的唯一一份活跃数据。

---

## 4. 统一 API 契约与跨语言规范

快照对外导出的只读接口必须与数据库主实例的只读方法完全保持 100% 镜像一致：

### 4.1 方法清单

| 方法名 | 返回类型 | 语义与行为规范 |
| :--- | :--- | :--- |
| `get_bytes(ns, key)` | `Option<Vec<u8>>` / `byte[]?` | 内存无锁直读原始字节，键不存在返回空 |
| `get_string(ns, key)` | `Result<Option<String>>` / `string?` | 读取 UTF-8 字符串；类型不匹配抛 `TypeMismatchError` |
| `get_i64(ns, key)` | `Result<Option<i64>>` / `long?` | 读取 64 位整数；类型不匹配抛 `TypeMismatchError` |
| `get_f64(ns, key)` | `Result<Option<f64>>` / `double?` | 读取 64 位双精度浮点数；类型不匹配抛 `TypeMismatchError` |
| `get_bool(ns, key)` | `Result<Option<bool>>` / `bool?` | 读取布尔值；类型不匹配抛 `TypeMismatchError` |
| `contains_key(ns, key)` | `bool` | 快速探测键是否存在 |
| `count(ns)` | `usize` / `int` | 统计指定命名空间下的有效条目数 |
| `keys(ns, prefix?)` | `Vec<String>` / `List<string>` | 按 UTF-8 无符号字节字典序升序遍历键 |
| `scan_range(ns, start, end)` | `Vec<Entry>` / `List<Entry>` | 按 UTF-8 无符号字典序升序扫描区间 `[start, end)` |
| `tx_id()` / `sequence_number()` | `u64` / `ulong` | 返回快照创建时冻结的事务版本号（TxID） |

### 4.2 语言特定生命周期管理契约

- **Rust**：
  ```rust
  let snapshot = db.snapshot();
  // snapshot 具有独立持有所有权（通过 Arc<DbState>），可在线程间转移（Send + Sync）
  // 离开作用域时通过 Drop 自动递减引用计数
  ```
- **Go**：
  ```go
  snap := db.Snapshot()
  defer snap.Close() // 显式释放状态引用，支持 runtime.SetFinalizer 兜底防泄露
  ```
- **.NET (C#)**：
  ```csharp
  using (var snap = db.CreateSnapshot())
  {
      // 实现 IDisposable，与 using 语句无缝配合
  }
  ```

---

## 5. 防御性工程设计与避坑指南

1. **防死锁保证**：
   快照上的所有只读方法**绝对不能访问主库的读写锁（RwLock）**。快照所读的数据在逻辑上是只读不可变的，直接无锁并发访问即可。
2. **防“僵死快照”内存泄露（Stale Snapshot Leak）**：
   业务若创建快照后长期不关闭且持续向主库海量写入，会导致历史条目因引用无法释放而内存膨胀。
   - 在 `Snapshot` 内部记录创建时间戳 `created_at`；
   - 增加 `DbOptions.snapshot_timeout_ms`（默认 30 分钟），若检测到未关闭的过期快照，通过 `onWarning` 回调发出告警；
   - 在 Go 与 .NET 中，注册终结器（Finalizer）作为未调用 `Close/Dispose` 时的安全兜底。
3. **与阶段二（追加写日志与后台 Compaction）的完美协同**：
   阶段二进行后台异步压缩时，压缩线程直接利用 `db.snapshot()` 获取当前时刻的冻结视图，前台线程继续高频追加新写入；压缩完毕后做极短的指针置换，完全消除压缩造成的 Stop-The-World 卡顿。
