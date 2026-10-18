# CarroKV for .NET

[English](README.md) | **简体中文**

**CarroKV (.NET)** 是一个严格遵循 [CKV 二进制规范 v3.2](../../docs/KV-DB-DESIGN-SPEC.md) 与 [跨语言共有接口规范](../../docs/API-SPEC.zh.md) 的高性能、极简、无 Native 依赖的跨平台嵌入式 Key-Value 数据库引擎。

---

## 安装方式 (NuGet)

通过 .NET CLI 或包管理器直接安装：

```bash
dotnet add package CarroKV
```

在代码中引入命名空间：
```csharp
using Ckv;
```

---

## 核心特性

- **100% 纯托管实现（Zero Native Dependency）**：无需携带任何 C/C++ 动态链接库（无需 `.dll` / `.so` / `.dylib`），即拷即用，彻底杜绝 P/Invoke 平台兼容性问题与崩溃隐患。
- **宽广跨平台多目标支持**：
  - `netstandard2.0`：兼容 .NET Standard 2.0+、.NET Framework 4.6.1+、Mono、Xamarin 与 Unity 等经典环境；
  - `net8.0`：为现代 .NET 8+ 提供深度优化的零拷贝 `Span<T>` 与高性能内存原语支持。
- **全内存纳秒级读取**：只读操作无需反序列化与系统调用，直接命中内存有序字典，单次读取仅需数十纳秒。
- **字节级跨语言确定性**：与 **Rust**、**Go**、**Kotlin**、**Dart** 实现共享完全一致的二进制磁盘格式与 IEEE 802.3 CRC32 校验码，任意跨语言热插拔无损互读互写。
- **高韧性两代原子轮转**：写入通过 `.tmp → .bak → .db` 原子重命名提交，断电或进程崩溃后重开自动晋升完整的 `.tmp`；主库损坏时默认拒绝以空库打开并提示用 `.bak` 恢复（可显式开启 `AutoRecoverFromBackup` 回退）。
- **内核锁 + 进程内单例**：`<db>.lock` 上使用操作系统内核锁（Windows `LockFileEx` / Unix `fcntl`），连接生命周期内持有，`Dispose()` 释放。同进程内相同路径的重复打开由弱引用注册表返回同一实例。
- **死锁防御的变更监听器（Watcher）**：支持监听数据变动，通知机制在写锁完全释放后派发，回调中重入读操作绝对安全。
- **无锁只读快照隔离（Snapshot Isolation）**：基于命名空间级写时复制（CoW），$O(1)$ 瞬时轻量创建。快照长耗时范围扫描与主库高频并发写**完全互不阻塞**。

---

## 快速上手 (Quick Start)

### 1. 打开数据库与基本 CRUD

```csharp
using Ckv;

// 打开或自动创建数据库。
//
// 💡 句柄包装模型（ADR-20261017）：每次 Open 返回独立的门面句柄，内部维护底层持久化核心
//    的原子引用计数。支持标准且地道的 `using var db = CkvDb.Open(...)` 写法！
//    各模块可独立持有、独立 Dispose()；单个句柄的释放绝不影响并发其他模块，
//    仅当全进程所有持有者均已释放（引用计数归零）时，底层引擎才会真正最终刷盘并释放排他锁。
using var db = CkvDb.Open("app_data.db");

// 1. 存取字符串 (String)
db.PutString("users", "user:1001", "Alice");
string? name = db.GetString("users", "user:1001");
Console.WriteLine($"Name: {name}"); // Output: Alice

// 2. 存取原始字节 (Bytes)
byte[] payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
db.PutBytes("users", "user:1001:avatar", payload);
byte[]? avatar = db.GetBytes("users", "user:1001:avatar");

// 3. 存取整数与浮点数
db.PutInt64("stats", "page_views", 1000L);
db.IncrementInt64("stats", "page_views", 1L); // 原子自增

// 4. 删除键与存在性探查
bool exists = db.ContainsKey("users", "user:1001");
bool deleted = db.Delete("users", "user:1001");
```

### 2. 命名空间与范围扫描

```csharp
// 列出所有命名空间（按无符号 UTF-8 字节序排列）
List<string> namespaces = db.Namespaces();

// 获取前缀匹配的键
List<string> keys = db.Keys("users", prefix: "user:");

// 范围扫描 [startKey, endKey)
List<Entry> entries = db.ScanRange("users", startKey: "user:1000", endKey: "user:2000");

// 前缀迭代
List<Entry> userEntries = db.IterPrefix("users", prefix: "user:");
```

### 3. 原子批处理事务 (Batch)

批处理支持链式操作与读己所写（Read-Your-Own-Writes），所有变更单次原子落盘提交：

```csharp
db.Batch(batch =>
{
    batch.PutString("orders", "order:001", "Created")
         .PutInt64("orders", "order:001:amount", 9900L)
         .Delete("orders", "order:draft:001");
});
```

### 4. 无锁只读快照隔离 (Snapshot)

```csharp
// 瞬时创建点时间一致性只读快照
using var snapshot = db.Snapshot();

// 快照扫描与外部高频写入完全互不阻塞
var snapEntries = snapshot.ScanRange("users");
```

### 5. 数据变更监听器 (Watcher)

```csharp
// 注册监听器（返回注销句柄）
using var watcher = db.Watch(e =>
{
    Console.WriteLine($"Op={e.Op}, Ns={e.Namespace}, Key={e.Key}");
});

// 在回调中重入读主库绝对安全，绝不死锁
db.PutString("users", "user:1002", "Bob");
```

---

## 核心 API 汇总

| 方法 | 说明 |
| :--- | :--- |
| `Open(path, options?)` | 打开或创建数据库（单进程内具有同路径单例防重入保护）。返回独立的门面句柄，内部由原子引用计数管理底层引擎 |
| `Close()` / `Dispose()` | 释放当前句柄（幂等无害）。底层引用计数递减；仅当全进程所有句柄归零时才真正最终刷盘并释放跨进程锁 |
| `ContainsKey(ns, key)` | 探测指定键是否存在（O(1) 内存探查） |
| `Count(ns)` | 获取指定命名空间下的有效条目总数 |
| `GetString(ns, key)` | 读取 UTF-8 字符串；类型不符抛 `TypeMismatchError` |
| `GetBytes(ns, key)` | 读取原始二进制字节数组 |
| `GetInt64(ns, key)` | 读取 64 位带符号整数；类型不符抛 `TypeMismatchError` |
| `GetFloat64(ns, key)` | 读取 64 位双精度浮点数；类型不符抛 `TypeMismatchError` |
| `GetBool(ns, key)` | 读取布尔值；类型不符抛 `TypeMismatchError` |
| `PutString(ns, key, val)` | 写入 UTF-8 字符串并落盘 |
| `PutBytes(ns, key, val)` | 写入原始字节数组并落盘 |
| `PutInt64(ns, key, val)` | 写入 64 位整数并落盘 |
| `PutFloat64(ns, key, val)` | 写入 64 位双精度浮点数并落盘 |
| `PutBool(ns, key, val)` | 写入布尔值并落盘 |
| `IncrementInt64(ns, key, delta)` | 原子增减 64 位整数计数器；严格进行类型校验 |
| `Delete(ns, key)` | 删除指定键，返回是否实际删除了有效键 |
| `Namespaces()` | 获取所有命名空间名称（UTF-8 升序） |
| `Keys(ns, prefix?)` | 按 UTF-8 字节无符号升序返回匹配前缀的所有键名 |
| `IterPrefix(ns, prefix?)` | 缓冲式前缀扫描匹配条目 |
| `ScanRange(ns, startKey?, endKey?)`| 按 UTF-8 字节无符号升序迭代 `[startKey, endKey)` 条目 |
| `DropNamespace(ns)` | 彻底删除并销毁指定命名空间 |
| `ClearNamespace(ns)` | 清空指定命名空间下的所有条目（保留命名空间名） |
| `Batch(action)` | 原子事务批处理（全成或全败提交） |
| `Snapshot()` / `CreateSnapshot()`| 纳秒级创建点时间只读快照（实现 `IDisposable`，完全无锁） |
| `Watch(callback)` | 注册变更监听委托，返回 `WatcherHandle` |
| `Flush()` / `Compact()` | 显式强制重写紧凑落盘 |
| `FileSize()` | 获取当前 `.db` 物理文件字节大小 |
| `Stats()` | 获取内存条目、命名空间数与事务 ID 统计指标 |
| `Backup(destPath)` | 实例方法：原子导出独立完整的 `.db` 副本（只读模式下亦支持） |
| `BackupFile(srcPath, destPath, options?)` | **静态便捷方法（ADR-20261017）**：优先复用进程内活跃实例导出，未打开时独占打开导出后自闭环释放 |

---

## 配置选项 (`DbOptions`)

```csharp
var options = new DbOptions
{
    // ⚠️ SnapshotAtomic 每次写入都会全量序列化整库并 fsync，成本 O(库大小)。
    //    实测稳态写延迟中位数：500 条目 2.29ms、2,000 条目 2.93ms、5,000 条目 4.43ms；
    //    AppendOnlyLog 稳态约 0.18ms/写且与库大小无关。
    //    数据量超过约 2MB 或存在高频写入时，请选 StorageEngineMode.AppendOnlyLog。
    EngineMode = StorageEngineMode.SnapshotAtomic,
    SyncMode = SyncMode.Immediate,
    SyncIntervalMs = 100,
    ReadOnly = false,
    AutoRecoverFromBackup = false,
    LockTimeoutMs = 10000
};

// 同样是共享实例：不要 using，应用退出前 Dispose 一次
var db = CkvDb.Open("custom.db", options);
```

> **同路径二次打开的选项冲突**：`ReadOnly` 与既有实例不一致时抛
> `ConflictingOptionsError`（绝不静默返回一个权限不同的实例）；
> `EngineMode` / `SyncMode` 不一致时通过 `OnWarning("onOptionsConflict")` 告警并沿用既有 options。

---

## 异常体系 (`DbError`)

所有的异常类型均继承自标准 `DbError`（同时保留 `CkvException` 兼容基类）：

- `CorruptedError`（`CorruptedDatabaseException`）：文件损坏、魔数不符或 CRC32 校验失败；
- `LockedError`（`DatabaseLockedException`）：锁被其他进程占用超时或只读模式下尝试写入；
- `ConcurrentModificationError`（`ConcurrentModificationException`）：检测到外部文件并发变动；
- `TypeMismatchError`（`TypeMismatchException`）：类型守卫拦截（例如把 Int64 当 String 读）；
- `InvalidUtf8Error`（`InvalidUtf8Exception`）：字符串包含非法 UTF-8 字节序列；
- `KeyTooLargeError`（`KeyTooLargeException`）：Key > 64KB 或 Value > 16MB；
- `InvalidNamespaceError`（`InvalidNamespaceException`）：命名空间超限或不合法；
- `UnsupportedVersionError`（`UnsupportedVersionException`）：遇到不支持的高版本数据文件；
- `ConflictingOptionsError`：同路径二次打开时 `ReadOnly` 与既有实例冲突；
- `IoError`：底层系统磁盘 I/O 错误。
