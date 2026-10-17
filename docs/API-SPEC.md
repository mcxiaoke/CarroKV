# CKV 跨语言共有接口统一规范 (Common API Specification)

> - **适用范围**：CKV 现有的五种语言实现（**Rust**、**.NET**、**Go**、**Kotlin**、**Dart**）以及未来的新增语言绑定。
> - **规范版本**：v1.0 (API Frozen)
> - **上位规范**：[KV-DB-DESIGN-SPEC.md](KV-DB-DESIGN-SPEC.md)（二进制磁盘格式规范 v3.2，本文冻结语言层的进程内公共 API）。

---

## 1. 规范设计原则

1. **二进制与磁盘格式严格对齐**：各语言底层共享同一份二进制布局（32B 文件头 + 28B 帧头 + CRC32 IEEE 802.3 校验），保证五种语言之间生成的数据文件 100% 无损热插拔互通。
2. **跨语言接口一致性**：所有语言的核心概念、方法集合、参数语义、默认值配置以及状态机流转保持完全等价。
3. **符合各语言原生习惯**：
   - **Rust**：蛇形命名（`snake_case`），返回 `Result<T, DbError>`，借用检查与 RAII 自动资源管理；
   - **.NET / C#**：大驼峰（`PascalCase`），实现 `IDisposable`，异常向上抛出；
   - **Go**：大驼峰（`CamelCase`），返回 `(T, error)`，`defer db.Close()`；
   - **Kotlin**：小驼峰（`camelCase`），实现 `AutoCloseable`，提供扩展函数与 `use` 块支持；
   - **Dart**：小驼峰（`camelCase`），事件循环与同步内存状态，单线程安全。
4. **强类型安全拦截**：对基本类型提供原生编解码支持，杜绝静默类型污染；读取时如果底层实际存储的类型与请求的类型不符，必须统一拦截并抛出/返回 `TypeMismatchError`。
5. **死锁防御的事件通知**：监听器（Watcher）统一遵循"持有写锁期间收集事件，在**完全释放写锁之后**派发回调"的铁律，确保用户回调中发起重入读操作绝对安全。

---

## 2. 五语言核心 API 映射速查表

| 功能接口类别 | 统一语义说明 | Rust (`carro_kv`) | .NET (`CarroKV`) | Go (`CarroKV/src/go`) | Kotlin (`CarroKV`) | Dart (`ckv`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **打开数据库** | 打开或创建 DB | `Db::open(path, opts)` | `CkvDb.Open(path, opts)` | `ckv.Open(path, opts)` | `CkvDb.open(path, opts)` | `CkvDb.open(path, opts)` |
| **关闭数据库** | 释放内核锁与工作线程 | `db.close()` / `drop(db)` | `db.Dispose()` / `Close()` | `db.Close()` | `db.close()` | `db.close()` |
| **判断是否关闭** | 检查当前实例是否已释放 | `db.is_closed()` | `db.IsClosed` | `db.IsClosed()` | `db.isClosed` | `db.isClosed` |
| **写入原始字节** | 存入 Raw Bytes (`0x00`) | `put_bytes(ns, k, v)` | `PutBytes(ns, k, v)` | `PutBytes(ns, k, v)` | `putBytes(ns, k, v)` | `putBytes(ns, k, v)` |
| **读取原始字节** | 获取 Raw Bytes | `get_bytes(ns, k)` | `GetBytes(ns, k)` | `GetBytes(ns, k)` | `getBytes(ns, k)` | `getBytes(ns, k)` |
| **存取字符串** | UTF-8 String (`0x01`) | `put_string` / `get_string` | `PutString` / `GetString` | `PutString` / `GetString` | `putString` / `getString` | `putString` / `getString` |
| **存取 64位整数** | Signed Int64 (`0x02`) | `put_i64` / `get_i64` | `PutInt64` / `GetInt64` | `PutInt64` / `GetInt64` | `putInt64` / `getInt64` | `putInt64` / `getInt64` |
| **存取 64位浮点** | IEEE 754 Float64 (`0x03`) | `put_f64` / `get_f64` | `PutFloat64` / `GetFloat64` | `PutFloat64` / `GetFloat64` | `putFloat64` / `getFloat64` | `putFloat64` / `getFloat64` |
| **存取布尔值** | Boolean (`0x04`) | `put_bool` / `get_bool` | `PutBool` / `GetBool` | `PutBool` / `GetBool` | `putBool` / `getBool` | `putBool` / `getBool` |
| **原子整数累加** | 补码绕回无锁自增 | `increment_i64(ns, k, d)` | `IncrementInt64(ns, k, d)` | `IncrementInt64(ns, k, d)` | `incrementInt64(ns, k, d)` | `incrementInt64(ns, k, d)` |
| **删除键** | 删除指定条目（写入墓碑） | `delete(ns, k)` | `Delete(ns, k)` | `Delete(ns, k)` | `delete(ns, k)` | `delete(ns, k)` |
| **判断键存在** | O(1) 内存探查 | `contains_key(ns, k)` | `ContainsKey(ns, k)` | `ContainsKey(ns, k)` | `containsKey(ns, k)` | `containsKey(ns, k)` |
| **获取条目数** | 统计命名空间有效键数 | `count(ns)` | `Count(ns)` | `Count(ns)` | `count(ns)` | `count(ns)` |
| **枚举命名空间** | 按 UTF-8 升序获取全部 ns | `namespaces()` | `Namespaces()` | `Namespaces()` | `namespaces()` | `namespaces()` |
| **获取全部键** | 按 UTF-8 升序获取全部 key | `keys(ns)` | `Keys(ns)` | `Keys(ns)` | `keys(ns)` | `keys(ns)` |
| **前缀迭代** | 缓冲式前缀扫描匹配 | `iter_prefix(ns, prefix)` | `IterPrefix(ns, prefix)` | `IterPrefix(ns, prefix)` | `iterPrefix(ns, prefix)` | `iterPrefix(ns, prefix)` |
| **范围切片扫描** | 左闭右开 `[start, end)` 扫描 | `scan_range(ns, s, e)` | `ScanRange(ns, s, e)` | `ScanRange(ns, s, e)` | `scanRange(ns, s, e)` | `scanRange(ns, s, e)` |
| **清空命名空间** | 清空数据但保留命名空间 | `clear_namespace(ns)` | `ClearNamespace(ns)` | `ClearNamespace(ns)` | `clearNamespace(ns)` | `clearNamespace(ns)` |
| **丢弃命名空间** | 物理彻底移除命名空间 | `drop_namespace(ns)` | `DropNamespace(ns)` | `DropNamespace(ns)` | `dropNamespace(ns)` | `dropNamespace(ns)` |
| **原子批写入** | 全成或全败原子提交 | `batch { ... }` | `Batch(b => { ... })` | `Batch(func(b) error)` | `batch { ... }` | `batch((b) { ... })` |
| **只读快照** | $O(1)$ CoW 瞬时隔离快照 | `db.snapshot()` | `db.Snapshot()` | `db.Snapshot()` | `db.snapshot()` | `db.snapshot()` |
| **注册监听器** | 订阅键值/命名空间变动 | `db.watch(cb) -> handle` | `db.Watch(cb) -> handle` | `db.Watch(cb) -> watcher` | `db.watch(cb) -> handle` | `db.watch(cb) -> handle` |
| **注销监听器** | 注销变动回调 | `handle.unwatch()` | `handle.Dispose()` | `watcher.Unwatch()` | `handle.unwatch()` | `handle.unwatch()` |
| **手动落盘** | 强制压缩重写落盘 | `db.flush()` / `compact()` | `db.Flush()` / `Compact()` | `db.Flush()` / `Compact()` | `db.flush()` / `compact()` | `db.flush()` / `compact()` |
| **当前物理大小** | 获取 `.db` 物理文件字节 | `db.file_size()` | `db.FileSize()` | `db.FileSize()` | `db.fileSize()` | `db.fileSize()` |
| **统计指标** | 获取内存条目与事务指标 | `db.stats()` | `db.Stats()` | `db.Stats()` | `db.stats()` | `db.stats()` |
| **主动热备份** | 零停机原子导出独立 `.db` | `db.backup(dest_path)` | `db.Backup(dest_path)` | `db.Backup(dest_path)` | `db.backup(dest_path)` | `db.backup(dest_path)` |

---

## 3. 详细接口规范与契约行为

### 3.1 数据库生命周期与打开选项 (Options)

各语言统一通过配置结构体（`DbOptions` / `Options`）控制引擎行为。

#### 核心配置项与默认值：
* **`read_only` (bool, 默认: `false`)**：
  以只读模式打开。只读模式下禁止所有写入与修改操作（调用写入会抛出/返回 `LockedError`）；但**允许调用 `backup(dest)`** 进行数据导出。
* **`storage_engine_mode` (默认: `SnapshotAtomic`)**：
  - `SnapshotAtomic`（快照原子轮转，写放大为 0，适于 <2MB 的配置/状态数据集）；
  - `AppendOnlyLog`（WAL 追加模式，写延迟极低，适于频繁高频写入场景）。
* **`sync_mode` (默认: `Always`)**：
  - `Always`：每次写操作同步等待落盘完成后返回；
  - `Periodic`：定时器在后台周期性刷盘；
  - `Deferred`：写操作置位脏标记，后台专用工作线程防抖短锁刷盘；
  - `Manual`：内存只写，完全由用户调用 `flush()` 时才落盘。
* **`sync_interval_ms` (u64/int, 默认: `50`)**：
  在 `Periodic` 或 `Deferred` 模式下后台任务的检查与刷盘间隔。
* **`lock_timeout_ms` (u32/int, 默认: `10000` 即 10 秒)**：
  获取进程间内核排他锁的最大等待耗时。超时后抛出/返回 `LockedError`。
* **`auto_recover_from_backup` (bool, 默认: `false`)**：
  当 `.db` 主库遭遇物理损坏时，是否允许自动从 `.bak` 恢复晋升。在只读模式下强制忽略此项。
* **`auto_compaction_threshold` (u64/long, 默认: `4194304` 即 4 MB)**：
  WAL 模式下触发后台自动压实重写的最小物理阈值。
* **`dead_ratio_compaction_threshold` (f64/double, 默认: `0.5` 即 50%)**：
  WAL 模式下墓碑及过期无用字节占比阈值。

#### 单进程进程内单例与所有权保证（ADR-20261017）：
- 相同绝对路径的数据库，在同进程内多次调用 `Open`，统一由弱引用注册表复用**同一个底层核心引擎（`DbCore`）**，并返回独立的门面句柄，配置以首次打开为准；
- **引用计数与独立句柄**：每次 `Open` 获得独立句柄，底层引用计数 +1；各个模块均可放心使用 `using var db = ...`。调用方持有的句柄首次 `Dispose()` / `Close()` 时递减引用计数并标记该句柄已关闭，重复调用为无害 No-op（保证句柄级绝对幂等）；
- **物理关闭时机**：只有当所有打开的句柄均已释放（底层引用计数归零）或由 GC 回收后，才会触发底层引擎的最终刷盘与释放跨进程排他锁；
- **选项冲突处理（批次一修订）**：`ReadOnly` 与既有实例不一致时**必须**抛
  `ConflictingOptionsError`（见 §4），不得静默复用；`EngineMode` / `SyncMode` 不一致时
  通过 `onWarning("onOptionsConflict")` 告警并沿用既有 options；
- **备份与快照范式（批次二修订）**：
  - 在线热库：官方推荐直接调用实例方法 `db.Backup(dest)` 执行原子快照导出，它绝不干扰或关闭实例；
  - 离线或一次性备份：推荐使用静态方法 `CkvDb.BackupFile(src, dst)`，内部自动检查注册表复用活跃实例或独占打开，安全可靠。

---

### 3.2 强类型读写与类型守卫规则

1. **类型码映射（ABI 规范）**：
   - `0x00`: **Bytes**（二进制字节数组）
   - `0x01`: **String**（UTF-8 文本字符串，严格执行 Unicode 合法性校验）
   - `0x02`: **Int64**（64 位带符号整数，小端序）
   - `0x03`: **Float64**（64 位双精度浮点数，写入时对 IEEE 754 NaN 规范化归一）
   - `0x04`: **Bool**（单字节布尔值，`0x00` 为 false，`0x01` 为 true）
2. **防类型静默污染（Type Guard）**：
   - 如果一个键是用 `PutInt64` 存入的，后续调用 `GetString` 或 `GetBytes` 时，**必须抛出/返回 `TypeMismatchError`**，绝对不允许静默返回脏数据或尝试非法强转。
3. **不存在的键**：
   - 读取不存在的键返回空/空指针（Rust 返回 `Ok(None)`；Go 返回 `(nil, nil)` 或空值；.NET 返回 `null`；Kotlin 返回 `null`；Dart 返回 `null`），不当作错误。
4. **Key 与 Value 大小边界**：
   - Key 最大长度：`65,535` 字节（64 KB - 1）；
   - Value 最大长度：`16,777,215` 字节（16 MB - 1）；
   - 超出上述上限时，在接口入口处立即拦截并报错 `KeyTooLargeError`。

---

### 3.3 命名空间与范围扫描规范

1. **默认命名空间**：
   - 传入 `""`（空串）或 `null` 代表默认命名空间。
2. **命名空间与键的排序规则**：
   - `namespaces()` 与 `keys(ns)` 返回的所有列表必须严格保证按 **无符号 UTF-8 字节序升序** 排列。
3. **前缀扫描 `iter_prefix(ns, prefix)`**：
   - 缓冲式遍历语义：内部一次性收集全部匹配条目后返回快照列表，避免泄露内部锁或导致并发迭代器悬垂指针；
   - 遇到非合法的 UTF-8 损坏 key 时，执行**安全跳过**策略，保证返回列表均为有效字符串。
4. **清空 vs 丢弃**：
   - `clear_namespace(ns)`：保留命名空间元数据本身（`namespaces()` 中仍能列出），但将其下的所有 key-value 标记删除；
   - `drop_namespace(ns)`：彻底物理擦除该命名空间及其所有数据。

---

### 3.4 原子批处理 (Batch)

通过各语言闭包/委托提供的 `batch` 接口，在持有写锁期间一次性原子提交多个写入与删除：
* **全成或全败 (All-or-Nothing)**：批处理过程中如遭遇任何错误，已暂存的操作全部回滚，内存状态保持原样；
* **单次批次落盘**：整个批次仅产生一个全局自增的 `TxID`，仅执行一次物理重命名/追加提交；
* **读己所写 (Read-Your-Own-Writes)**：在同一个批次内，前一步 `Put` 写入的暂存数据可在后一步被当前批次直接读取或覆盖。

---

### 3.5 无锁只读快照隔离 (Snapshot Isolation)

1. **纳秒级创建**：基于命名空间级写时复制（Root State CoW），快照创建仅需读锁复制外层指针与引用计数，耗时仅几十纳秒。
2. **绝对无锁与高韧性**：
   - 快照读取完全在无锁状态下进行；
   - 主库高频并发写入、后台刷盘与快照长耗时范围扫描**完全互不干扰、互不阻塞**。
3. **释放后温和语义**：
   - 快照关闭/销毁后，再次调用读操作温和地返回空（不存在），不抛出崩溃性异常。

---

### 3.6 数据变更监听器 (Watcher)

1. **四值 ABI 事件稳定**：
   ```protobuf
   Put           = 1  // 键新增或值被覆盖更新
   Delete        = 2  // 键被删除
   DropNamespace = 3  // 命名空间被物理丢弃
   Clear         = 4  // 命名空间被清空
   ```
2. **防死锁派发保证**：
   - 内部在持写锁修改数据期间仅将事件加入待派发队列；
   - 在**写锁完全安全释放之后**，工作线程才按序分发事件给注册的回调；
   - 订阅方回调中重入发起 `db.get_*()`、`db.count()` 绝对安全，永不发生重入死锁。
3. **显式注销**：
   - `watch()` 返回专用的注销句柄（`WatcherHandle` / `Watcher`）；
   - 调用 `unwatch()` 或 `handle.Dispose()` 即可安全注销。

---

## 4. 统一错误体系映射表 (Error Hierarchy)

五种语言的错误类型必须一一对应，类型语义严格保持一致：

| 错误规范名称 | 触发场景 | Rust (`DbError`) | .NET (异常类) | Go (导出的错误类型) | Kotlin (异常类) | Dart (异常类) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`DbError`** | 统一根错误 / 根异常 | `DbError` (enum) | `DbError` (继承自 Exception) | `DbError` (interface) | `DbError` (继承自 Exception) | `DbError` (实现 Exception) |
| **`CorruptedError`** | 魔数校验不匹配、CRC32 损坏、帧格式异常 | `DbError::Corrupted(String)` | `CorruptedError` | `*CorruptedError` | `CorruptedError` | `CorruptedError` |
| **`LockedError`** | 锁被其他进程占用超时、或只读模式下尝试写入 | `DbError::Locked(String)` | `LockedError` | `*LockedError` | `LockedError` | `LockedError` |
| **`ConcurrentModificationError`** | 多线程并发写入冲突或外部并发修改磁盘文件 | `DbError::ConcurrentModification(String)` | `ConcurrentModificationError` | `*ConcurrentModificationError` | `ConcurrentModificationError` | `ConcurrentModificationError` |
| **`InvalidUtf8Error`** | 键名或字符串 Value 包含非法的 UTF-8 字节编码 | `DbError::InvalidUtf8(String)` | `InvalidUtf8Error` | `*InvalidUtf8Error` | `InvalidUtf8Error` | `InvalidUtf8Error` |
| **`KeyTooLargeError`** | Key 超过 64KB 或 Value 超过 16MB | `DbError::KeyTooLarge(String)` | `KeyTooLargeError` | `*KeyTooLargeError` | `KeyTooLargeError` | `KeyTooLargeError` |
| **`InvalidNamespaceError`** | 命名空间名称不合法 | `DbError::InvalidNamespace(String)` | `InvalidNamespaceError` | `*InvalidNamespaceError` | `InvalidNamespaceError` | `InvalidNamespaceError` |
| **`UnsupportedVersionError`** | 遇到不受支持的高版本磁盘协议（未来扩展拦截） | `DbError::UnsupportedVersion(String)` | `UnsupportedVersionError` | `*UnsupportedVersionError` | `UnsupportedVersionError` | `UnsupportedVersionError` |
| **`TypeMismatchError`** | 尝试用错误的类型读取键（例如把 Int64 当 String 读） | `DbError::TypeMismatch(String)` | `TypeMismatchError` | `*TypeMismatchError` | `TypeMismatchError` | `TypeMismatchError` |
| **`IoError`** | 操作系统底层文件读写、权限或磁盘 I/O 失败 | `DbError::Io(std::io::Error)` | `IoError` | `*IoError` | `IoError` | `IoError` |
| **`ConflictingOptionsError`** | 对同一路径 reentrant open 时 `ReadOnly` 与既有实例冲突 | `DbError::ConflictingOptions(String)` | `ConflictingOptionsError` | `*ConflictingOptionsError` | `ConflictingOptionsError` | `ConflictingOptionsError` |

> **`ConflictingOptionsError` 的边界（批次一新增）**：进程内单例语义下，命中注册表的
> `open()` **必须**原样返回既有实例，因此无法满足与既有实例不同的 `ReadOnly` 请求。
> 以 `ReadOnly = true` 打开却静默拿到读写实例是**静默提权**（且 `onWarning` 默认为空时
> 连告警都看不见），反方向则会让后续写入在远处以 `LockedError` 失败、误导排查。
> 两个方向都必须拒绝。`EngineMode` / `SyncMode` 冲突只造成性能与落盘语义差异、
> 不构成权限问题，维持 `onWarning("onOptionsConflict")` 告警并沿用既有 options。

---

## 5. 跨语言规范遵守核对清单

新语言移植或进行跨语言调用时，必须执行以下验证：
1. **Golden Vectors 校验**：必须能成功无损解析 `testdata/` 下的全套确定性黄金测试夹具；
2. **IEEE 802.3 CRC32**：各语言计算同一串字节的 CRC32 结果必须完全一致；
3. **互通测试 (Interop)**：任意一门语言写出的 `.db` 文件，其余四门语言必须均能完整读取，反之亦然。
