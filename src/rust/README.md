# carro_kv (Rust)

**carro_kv** 是 CKV 跨语言嵌入式 Key-Value 数据库引擎的 Rust 原生实现，严格遵循 [CKV 二进制规范 v3.2](../../docs/KV-DB-DESIGN-SPEC.md) 与 [跨语言共有接口规范](../../docs/API-SPEC.md)。

专为桌面应用、CLI 工具、边缘节点与轻量服务打造，面向**小型配置/状态数据集**（单进程、通常 < 2MB、读多写少）。

---

## 核心设计特性

- **100% 纯标准库实现（Zero Native / C Library Dependency）**：
  零 FFI，不调用任何 C/C++ 动态链接库，不使用 `mmap`，杜绝跨平台编译打包与内存悬挂隐患。
- **纳秒级内存点查性能**：
  数据常驻内存有序结构（BTreeMap），只读操作完全无系统调用、无磁盘 I/O、无数据反序列化，单次点查仅需数十纳秒。
- **两代原子轮转自愈模型（Crash-Safe & Self-Healing）**：
  写入通过 `.tmp -> .bak -> .db` 极速原子替换，启动自动检测未竟临时文件，遇到物理坏块时可毫秒级从 `.bak` 恢复，杜绝静默清空数据。
- **字节级跨语言互通性**：
  与 **.NET**、**Go**、**Kotlin**、**Dart** 实现共享统一内联 IEEE 802.3 CRC32 算法与无符号 UTF-8 字节序，`.db` 文件在各语言间自由热插拔无损互读互写。
- **无死锁事件通知 (Watcher)**：
  内部在持有写锁期间收集事件，在**完全释放写锁之后**派发回调，回调内重入读主库绝对安全、永不发生死锁。
- **无锁只读快照隔离 (Lock-Free Snapshot Isolation)**：
  基于 Root State CoW 实现纳秒级快照创建，长耗时范围扫描与主库高频并发写入**完全互不阻塞**。

---

## 安装引入

### 方式一：通过 crates.io 安装（推荐）
在 `Cargo.toml` 中添加依赖：
```toml
[dependencies]
carro_kv = "0.1.0"
```

或者使用 `cargo add`：
```bash
cargo add carro_kv
```

### 方式二：通过 Git 仓库依赖直连
```toml
[dependencies]
carro_kv = { git = "https://github.com/mcxiaoke/CarroKV", tag = "v0.1.0" }
```

---

## 快速上手 (Quick Start)

### 1. 打开数据库与基本 CRUD

```rust
use carro_kv::{Db, DbResult};

fn main() -> DbResult<()> {
    // 打开或自动创建数据库
    let db = Db::open("app_data.db", None)?;

    // 1. 存取字符串
    db.put_string("config", "theme", "dracula")?;
    if let Some(theme) = db.get_string("config", "theme")? {
        println!("Theme: {}", theme);
    }

    // 2. 存取原始字节流
    db.put_bytes("users", "u:1:avatar", &[0xDE, 0xAD, 0xBE, 0xEF])?;
    let avatar = db.get_bytes("users", "u:1:avatar")?;

    // 3. 强类型整数与原子自增
    db.put_i64("stats", "pv", 1000)?;
    let new_pv = db.increment_i64("stats", "pv", 1)?; // 返回自增后的值 1001

    // 4. 判断存在与删除
    let exists = db.contains_key("config", "theme")?;
    let removed = db.delete("config", "theme")?;

    // 关闭数据库（释放操作系统文件锁与后台刷盘线程；亦可在 drop 时自动释放）
    db.close()?;
    Ok(())
}
```

### 2. 命名空间与范围扫描

```rust
// 列出所有命名空间（按无符号 UTF-8 字节序升序）
let namespaces: Vec<String> = db.namespaces();

// 获取指定命名空间下的匹配键
let keys: Vec<String> = db.keys("users", Some("u:"))?;

// 范围切片遍历 [start_key, end_key)
let entries = db.scan_range("users", Some("u:1000"), Some("u:2000"))?;
for entry in entries {
    println!("{}: {:?}", entry.key, entry.value);
}

// 缓冲式前缀匹配
let prefix_entries = db.iter_prefix("users", Some("u:"))?;
```

### 3. 原子批处理事务 (Batch)

批处理支持链式操作与读己所写（Read-Your-Own-Writes），所有操作全成或全败单次落盘提交：

```rust
db.batch(|b| {
    b.put_string("orders", "order:001", "created")?;
    b.put_i64("orders", "order:001:amount", 99)?;
    b.delete("orders", "order:draft:001")?;
    Ok(())
})?;
```

### 4. 无锁只读快照隔离 (Snapshot)

```rust
// 纳秒级创建点时间只读快照
let snap = db.snapshot()?;

// 快照只读扫描期间，外部主库高频写入完全互不阻塞
let items = snap.scan_range("users", None, None)?;
for item in items {
    println!("Snap item: {}", item.key);
}
```

### 5. 数据变更监听 (Watcher)

```rust
// 注册监听器，返回 WatcherHandle
let watcher = db.watch(|event| {
    println!("Change: op={:?}, ns={}, key={}", event.op, event.namespace, event.key);
});

// 在回调之外触发写操作，回调中发起重入读绝对安全
db.put_string("users", "u:2", "Bob")?;

// 显式注销监听
watcher.unwatch();
```

---

## 配置选项 (`DbOptions`)

```rust
use carro_kv::{Db, DbOptions, StorageEngineMode, SyncMode};

let mut opts = DbOptions::default();
opts.storage_engine_mode = StorageEngineMode::SnapshotAtomic; // SnapshotAtomic 或 AppendOnlyLog
opts.sync_mode = SyncMode::Always;                           // Always, Periodic, Deferred, Manual
opts.sync_interval_ms = 50;                                  // 后台刷盘周期
opts.read_only = false;                                      // 是否以只读模式打开
opts.auto_recover_from_backup = false;                       // 主库损坏时是否回退 .bak
opts.lock_timeout_ms = 10000;                                // 文件锁超时（毫秒）

let db = Db::open("custom.db", Some(opts))?;
```

---

## 错误处理体系 (`DbError`)

`carro_kv` 统一使用 `DbError` 枚举，实现了 `std::error::Error`：

```rust
use carro_kv::{Db, DbError};

match db.get_string("cfg", "key") {
    Ok(Some(val)) => println!("Value: {val}"),
    Ok(None) => println!("Key not found"),
    Err(DbError::TypeMismatch(msg)) => eprintln!("Type guard error: {msg}"),
    Err(DbError::Locked(msg)) => eprintln!("Database locked by another process: {msg}"),
    Err(DbError::Corrupted(msg)) => eprintln!("CRC32 or magic checksum mismatch: {msg}"),
    Err(e) => eprintln!("Error: {e}"),
}
```
