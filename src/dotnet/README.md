# CKV for .NET

**CKV (.NET)** 是一个严格遵循 **CKV v3.2 规范** 的高性能、极简、无 Native 依赖的跨平台嵌入式 Key-Value 数据库引擎。

---

## 核心特性

- **100% 纯托管实现（Zero Native Dependency）**：无需携带任何 C/C++ 动态链接库（无需 `.dll` / `.so` / `.dylib`），即拷即用，彻底杜绝 P/Invoke 平台兼容性问题与崩溃隐患。
- **宽广跨平台多目标支持**：
  - `netstandard2.0`：兼容 .NET Standard 2.0+、.NET Framework 4.6.1+、Mono、Xamarin 与 Unity 等经典环境；
  - `net8.0`：为现代 .NET 8+ 提供深度优化的零拷贝 `Span<T>` 与高性能内存原语支持。
- **全内存纳秒级读取**：只读操作无需反序列化与系统调用，直接命中内存有序字典，单次读取仅需数十纳秒。
- **字节级跨语言确定性**：与 CKV 的 **Rust**、**Go** 等实现共享完全一致的二进制磁盘格式与 IEEE 802.3 CRC32 校验码，支持任意跨语言无缝热插拔读写。
- **高韧性两代原子轮转**：写入通过 `.tmp → .bak → .db` 原子重命名提交，断电或进程崩溃后重开会晋升完整的 `.tmp`；主库缺失时默认**拒绝以空库打开**并提示用 `.bak` 恢复（需显式开启 `AutoRecoverFromBackup` 才自动回退），不会静默清空。
- **内核锁 + 单进程单例**：`<db>.lock` 上使用操作系统内核锁（Windows `LockFileEx`），连接生命周期内持有，`Dispose()` 释放；锁文件内容不参与判活，因此没有时钟漂移与锁窃取问题。同进程内相同路径的重复打开由弱引用注册表返回同一实例。
- **死锁防御的变更监听器（Watcher）**：支持监听数据变动，通知机制在写锁完全释放后派发，回调中重入读操作绝对安全。
- **无锁只读快照隔离（Snapshot Isolation）**：基于命名空间级写时复制（CoW），$O(1)$ 瞬时轻量创建。快照长耗时范围扫描与主库高频并发写**完全互不阻塞**，支持点时间一致性视图，实现 `IDisposable` 资源释放。

---

## 快速上手 (Quick Start)

### 1. 打开数据库与基本 CRUD

```csharp
using Ckv;

// 打开或自动创建数据库
using var db = CkvDb.Open("app_data.db");

// 1. 存取字符串 (String)
db.PutString("users", "user:1001", "Alice");
string? name = db.GetString("users", "user:1001");
Console.WriteLine($"Name: {name}"); // Output: Alice

// 2. 存取原始字节 (Bytes)
byte[] payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
db.PutBytes("users", "user:1001:avatar", payload);
byte[]? avatar = db.GetBytes("users", "user:1001:avatar");

// 3. 删除键
bool deleted = db.Delete("users", "user:1001");
Console.WriteLine($"Deleted: {deleted}"); // Output: True
```

### 2. 常用强类型支持与类型守卫

CKV 针对常见基本类型内置了强类型编解码与严格类型守卫，杜绝静默类型污染：

```csharp
// 64 位带符号整数
db.PutInt64("stats", "page_views", 1000000L);
long? views = db.GetInt64("stats", "page_views");

// 64 位双精度浮点数（内置 IEEE 754 NaN 规范化归一）
db.PutFloat64("config", "threshold", 99.95);
double? threshold = db.GetFloat64("config", "threshold");

// 布尔值
db.PutBool("config", "feature_flag", true);
bool? flag = db.GetBool("config", "feature_flag");

// 类型安全拦截：当对 string 类型调用 GetInt64 时将抛出 TypeMismatchException
try
{
    db.PutString("stats", "wrong_type", "hello");
    db.GetInt64("stats", "wrong_type");
}
catch (TypeMismatchException ex)
{
    Console.WriteLine($"Caught expected mismatch: {ex.Message}");
}
```

### 3. P0 实用 API：计数器与快速探测

```csharp
// 1. 快速判断键是否存在（无需反序列化值）
if (db.ContainsKey("stats", "page_views"))
{
    Console.WriteLine("Key exists!");
}

// 2. 统计指定命名空间下的有效键总数
int count = db.Count("stats");
Console.WriteLine($"Stats entry count: {count}");

// 3. 原子的 64 位整数累加器 (IncrementInt64)
// 若键不存在则直接初始化为 delta；若存在且为 int64 则原子加上 delta；若类型不符则拦截抛错
long newCount = db.IncrementInt64("counters", "login_hits", 1);   // 初始化为 1
newCount = db.IncrementInt64("counters", "login_hits", 10);      // 累加为 11
newCount = db.IncrementInt64("counters", "login_hits", -5);      // 递减为 6

// 4. 清空命名空间（保留命名空间但删除其中所有数据，并原子刷盘）
bool cleared = db.ClearNamespace("counters");
```

### 4. 事务批处理 (Batch)

通过 `Batch` 可以在纯内存暂存区（Staging Area）内暂存一系列操作，若代码块抛出异常则**全额回滚、不产生任何落盘影响**；执行成功则一次性合并至主状态并原子刷盘：

```csharp
db.Batch(writer =>
{
    writer.PutString("tenant_a", "config:theme", "dark")
          .PutInt64("tenant_a", "config:quota", 50000L)
          .PutBool("tenant_a", "config:beta", true)
          .Delete("tenant_a", "config:deprecated")
          .ClearNamespace("tenant_b"); // 支持命名空间清空
});
```

### 5. 字典序范围扫描 (ScanRange) 与前缀检索 (Keys)

CKV 内存与磁盘均保证基于 **无符号 UTF-8 字节序** 绝对升序：

```csharp
db.PutString("devices", "sensor:001", "active");
db.PutString("devices", "sensor:002", "standby");
db.PutString("devices", "sensor:003", "offline");
db.PutString("devices", "terminal:001", "ready");

// 1. 前缀键名查询
List<string> sensorKeys = db.Keys("devices", prefix: "sensor:");
// 返回: ["sensor:001", "sensor:002", "sensor:003"]

// 2. 范围区间扫描 [startKey, endKey)
List<Entry> range = db.ScanRange("devices", startKey: "sensor:001", endKey: "sensor:003");
foreach (var entry in range)
{
    Console.WriteLine($"{entry.Key} -> {entry.AsString()}");
}
```

### 6. 数据变更监听 (Watcher & KeyChanged)

支持订阅全局键值变更。通知回调在**写锁释放之后**执行，因此在回调中执行读操作是绝对安全的，不会造成死锁：

```csharp
// 方式 A：调用 Watch 方法
db.Watch(ev =>
{
    Console.WriteLine($"[Watch] Op={ev.Op}, Namespace='{ev.Namespace}', Key='{ev.Key}'");
    // 安全：在回调中直接读取数据库不会发生死锁
    if (ev.Op == KeyChangeOp.Put)
    {
        var exists = db.ContainsKey(ev.Namespace, ev.Key);
    }
});

// 方式 B：使用 C# 标准事件 KeyChanged
db.KeyChanged += ev =>
{
    if (ev.Op == KeyChangeOp.Delete)
    {
        Console.WriteLine($"Key deleted: {ev.Key}");
    }
};
```

### 7. 只读快照隔离 (Snapshot Isolation)

通过 `CreateSnapshot()` 获得点时间一致性视图。创建快照耗时仅数纳秒且**零深拷贝数据**。快照对象彻底无锁，即便主库在后台高频写入或落盘，快照内的全库迭代也完全不受任何干扰与阻塞：

```csharp
// 创建只读快照（实现 IDisposable，推荐 using 作用域）
using (var snapshot = db.CreateSnapshot())
{
    Console.WriteLine($"Snapshot TxID: {snapshot.TxId}");

    // 主库并发写入新数据并删除旧数据
    db.PutString("users", "user:1002", "Bob");
    db.Delete("users", "user:1001");

    // 快照视角冻结在创建时刻：旧键仍存在，新键不可见
    string? alice = snapshot.GetString("users", "user:1001"); // 输出: Alice
    bool hasBob = snapshot.ContainsKey("users", "user:1002");   // 输出: False

    // 快照提供完整只读 API（Keys, ScanRange, Count, Get* 等）
    List<Entry> allUsers = snapshot.ScanRange("users");
}
```

---

## API 参考清单

### `CkvDb` 核心实例方法

| 方法 | 说明 |
| :--- | :--- |
| `Open(path, options?)` | 打开或创建数据库（单进程内具有同路径单例防重入保护） |
| `Close()` / `Dispose()` | 关闭数据库，释放内存与单例锁 |
| `ContainsKey(ns, key)` | 探测指定键是否存在（O(1) 内存查询，无值反序列化开销） |
| `Count(ns)` | 获取指定命名空间下的有效条目总数 |
| `GetString(ns, key)` | 读取 UTF-8 字符串；类型不符抛 `TypeMismatchException` |
| `GetBytes(ns, key)` | 读取原始二进制字节数组 |
| `GetInt64(ns, key)` | 读取 64 位整数；类型不符抛 `TypeMismatchException` |
| `GetFloat64(ns, key)` | 读取 64 位双精度浮点数；类型不符抛 `TypeMismatchException` |
| `GetBool(ns, key)` | 读取布尔值；类型不符抛 `TypeMismatchException` |
| `PutString(ns, key, val)` | 写入 UTF-8 字符串，并原子快照落盘 |
| `PutBytes(ns, key, val)` | 写入原始字节数组，并原子快照落盘 |
| `PutInt64(ns, key, val)` | 写入 64 位整数，并原子快照落盘 |
| `PutFloat64(ns, key, val)` | 写入 64 位双精度浮点数，并原子快照落盘 |
| `PutBool(ns, key, val)` | 写入布尔值，并原子快照落盘 |
| `IncrementInt64(ns, key, delta)` | 原子增减 64 位整数计数器；严格进行类型校验 |
| `Delete(ns, key)` | 删除指定键，返回是否实际删除了有效键 |
| `DropNamespace(ns)` | 彻底删除并销毁指定命名空间 |
| `ClearNamespace(ns)` | 清空指定命名空间下的所有条目 |
| `Batch(action)` | 纯内存暂存事务批处理，支持链式写操作 |
| `Keys(ns, prefix?)` | 按 UTF-8 字节无符号升序返回匹配前缀的所有键名 |
| `ScanRange(ns, startKey?, endKey?)`| 按 UTF-8 字节无符号升序迭代 `[startKey, endKey)` 条目 |
| `Watch(callback)` | 注册变更监听委托 |
| `KeyChanged` | C# 标准事件，订阅键值变更 |
| `CreateSnapshot()` | 纳秒级创建点时间只读快照（实现 `IDisposable`，完全无锁读写互不阻塞） |

### `Snapshot` 只读快照实例方法

快照实例仅提供只读查询接口，不持有主库读写锁，读操作彻底无锁：

| 属性 / 方法 | 说明 |
| :--- | :--- |
| `TxId` | 获取快照创建时所冻结的单调事务版本号 |
| `ContainsKey(ns, key)` | 探测指定键是否存在（O(1) 内存查询，无值反序列化开销） |
| `Count(ns)` | 获取快照视图下指定命名空间的有效条目总数 |
| `GetString(ns, key)` | 读取 UTF-8 字符串；类型不符抛 `TypeMismatchException` |
| `GetBytes(ns, key)` | 读取原始二进制字节数组 |
| `GetInt64(ns, key)` | 读取 64 位整数；类型不符抛 `TypeMismatchException` |
| `GetFloat64(ns, key)` | 读取 64 位双精度浮点数；类型不符抛 `TypeMismatchException` |
| `GetBool(ns, key)` | 读取布尔值；类型不符抛 `TypeMismatchException` |
| `Keys(ns, prefix?)` | 按 UTF-8 字节无符号升序返回匹配前缀的所有键名 |
| `ScanRange(ns, startKey?, endKey?)`| 按 UTF-8 字节无符号升序迭代 `[startKey, endKey)` 条目 |
| `Dispose()` | 显式释放快照对底层命名空间树的引用 |

---

## 配置选项 (`DbOptions`)

```csharp
var options = new DbOptions
{
    SyncOnWrite = true,           // 是否在每次写提交时显式执行 fsync（默认 true）
    AutoRecoverFromBackup = false,// 主库损坏时是否自动从 .bak 恢复（默认 false，见决策 D1）
    ReadOnly = false,             // 是否以只读模式打开（默认 false）
    LockTimeoutMs = 10000         // 锁超时检测时间（默认 10000 毫秒）
};

using var db = CkvDb.Open("custom.db", options);
```

> **目录 fsync 的平台差异**：轮转后会尽力 fsync 父目录，使 `rename` 产生的目录项在掉电后依然存在。
> Windows 由 NTFS 保证、无需额外操作；**.NET 8 在 Linux 上无法以目录路径打开句柄（实测抛
> `UnauthorizedAccessException`），因此该平台降级为 no-op**，并在首次轮转时通过
> `OnWarning("onDirSyncUnavailable", ...)` 上报一次。CKV 不会为此引入 P/Invoke。

---

## 异常体系 (`CkvException`)

所有异常均继承自 `CkvException`，方便上层业务统一捕获：

- `CorruptedDatabaseException`：数据库格式损坏（魔数错误、CRC32 校验失败等）；
- `DatabaseLockedException`：获取数据库锁超时或被其他进程占用；
- `ConcurrentModificationException`：文件被外部进程/工具修改（TxID 或长度与实例账本不一致），拒绝覆盖写入；
- `TypeMismatchException`：读取值或对键计数时，底层存储类型与预期类型不一致；
- `InvalidUtf8Exception`：底层原始字节无法被解析为合法 UTF-8 编码；
- `KeyTooLargeException`：Key 长度超过 65,535 字节或 Value 超过 16 MB；
- `InvalidNamespaceException`：命名空间 UTF-8 字节长度超过 255 字节；
- `UnsupportedVersionException`：文件版本高于当前库支持的最高读取版本。

具体抛出的是上述基类的同名子类（如 `LockedError : DatabaseLockedException`、`CorruptedError : CorruptedDatabaseException`），按基类捕获即可，与 Rust 的 `DbError` 变体一一对应。
