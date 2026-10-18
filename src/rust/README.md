# carro_kv (Rust)

**English** | [简体中文](README.zh.md)

**carro_kv** is the Rust native implementation of the CKV cross-language embedded Key-Value database engine, strictly conforming to the [CKV Binary Specification v3.2](../../docs/KV-DB-DESIGN-SPEC.md) and the [Common API Specification](../../docs/API-SPEC.md) ([Chinese](../../docs/API-SPEC.zh.md)).

Designed for desktop applications, CLI tools, edge nodes, and lightweight services, targeting **small configuration and state datasets** (single-process, typically < 2 MB, read-heavy).

---

## Core Design Features

- **100% Pure Standard Library Implementation (Zero Native / C Library Dependency)**:
  Zero FFI, no C/C++ shared libraries, no `mmap`. Eliminates cross-platform compilation friction and memory hanging hazards.
- **Nanosecond In-Memory Point Lookup Performance**:
  Active data is held in sorted in-memory structures (`BTreeMap`). Read operations execute with zero system calls, zero disk I/O, and zero deserialization overhead—single point lookups take tens of nanoseconds.
- **Two-Generation Atomic Rotation & Self-Healing (Crash-Safe & Self-Healing)**:
  Writes commit atomically via `.tmp -> .bak -> .db` rotation. Startup automatically detects pending temporary files; recovers from `.bak` in milliseconds if corrupted by power loss, preventing silent data loss.
- **Byte-Level Cross-Language Interoperability**:
  Shares identical inline IEEE 802.3 CRC32 checksums and unsigned UTF-8 byte ordering with **.NET**, **Go**, **Kotlin**, and **Dart**. Database files can be hot-swapped seamlessly between platforms.
- **Deadlock-Free Change Listeners (Watcher)**:
  Events are gathered during write locks and dispatched **only after the write lock is completely released**, ensuring reentrant read operations from callbacks are completely safe and deadlock-free.
- **Lock-Free Read-Only Snapshot Isolation**:
  Instantaneous point-in-time snapshot creation using Root State CoW. Snapshot range scans and background writes **never block each other**.

---

## Installation & Setup

### Option 1: Via crates.io (Recommended)
Add to your `Cargo.toml`:

```toml
[dependencies]
carro_kv = "0.2.0"
```

Or run `cargo add`:

```bash
cargo add carro_kv
```

### Option 2: Via Git Dependency
```toml
[dependencies]
carro_kv = { git = "https://github.com/mcxiaoke/CarroKV", tag = "v0.2.0" }
```

---

## Quick Start

### 1. Opening a Database & Basic CRUD

```rust
use carro_kv::{Db, DbResult};

fn main() -> DbResult<()> {
    // Open or create the database
    let db = Db::open("app_data.db", None)?;

    // 1. Store and retrieve String
    db.put_string("config", "theme", "dracula")?;
    if let Some(theme) = db.get_string("config", "theme")? {
        println!("Theme: {}", theme);
    }

    // 2. Store and retrieve raw binary Bytes
    db.put_bytes("users", "u:1:avatar", &[0xDE, 0xAD, 0xBE, 0xEF])?;
    let avatar = db.get_bytes("users", "u:1:avatar")?;

    // 3. Strongly-typed Int64 & atomic increment
    db.put_i64("stats", "pv", 1000)?;
    let new_pv = db.increment_i64("stats", "pv", 1)?; // Returns 1001

    // 4. Key existence check and deletion
    let exists = db.contains_key("config", "theme")?;
    let removed = db.delete("config", "theme")?;

    // Close database (releases OS file lock and background sync threads; also automatic on drop)
    db.close()?;
    Ok(())
}
```

### 2. Namespaces & Range Scanning

```rust
// List all namespaces (sorted in ascending unsigned UTF-8 order)
let namespaces: Vec<String> = db.namespaces();

// List matching keys with prefix
let keys: Vec<String> = db.keys("users", Some("u:"))?;

// Half-open range scan [start_key, end_key)
let entries = db.scan_range("users", Some("u:1000"), Some("u:2000"))?;
for entry in entries {
    println!("{}: {:?}", entry.key, entry.value);
}

// Buffered prefix scan
let prefix_entries = db.iter_prefix("users", Some("u:"))?;
```

### 3. Atomic Batch Transactions (Batch)

Batch execution supports fluent operations and Read-Your-Own-Writes, committed in a single atomic transaction:

```rust
db.batch(|b| {
    b.put_string("orders", "order:001", "created")?;
    b.put_i64("orders", "order:001:amount", 99)?;
    b.delete("orders", "order:draft:001")?;
    Ok(())
})?;
```

### 4. Lock-Free Snapshot Isolation

```rust
// Create an instantaneous read-only snapshot
let snap = db.snapshot()?;

// Snapshot range scan never blocks or gets blocked by concurrent writes
let items = snap.scan_range("users", None, None)?;
for item in items {
    println!("Snap item: {}", item.key);
}
```

### 5. Data Change Listeners (Watcher)

```rust
// Register a watcher (returns WatcherHandle)
let watcher = db.watch(|event| {
    println!("Change: op={:?}, ns={}, key={}", event.op, event.namespace, event.key);
});

// Reentrant reads within callbacks are completely safe and deadlock-free
db.put_string("users", "u:2", "Bob")?;

// Unregister watcher
watcher.unwatch();
```

---

## Configuration Options (`DbOptions`)

```rust
use carro_kv::{Db, DbOptions, StorageEngineMode, SyncMode};

let opts = DbOptions::new()
    // ⚠️ SnapshotAtomic serializes the full database and triggers fsync on every write, costing O(DB Size).
    //    Measured median steady write latency: 500 entries: 2.29ms; 2,000 entries: 2.93ms; 5,000 entries: 4.43ms.
    //    AppendOnlyLog achieves ~0.18ms/write independent of DB size.
    //    For datasets exceeding ~2MB or write-heavy workloads, choose StorageEngineMode::AppendOnlyLog.
    .engine_mode(StorageEngineMode::SnapshotAtomic)
    .sync_mode(SyncMode::Immediate)
    .sync_interval_ms(100)
    .read_only(false)
    .lock_timeout_ms(10_000);

let db = Db::open("custom.db", Some(opts))?;
```

> **Option Conflict on Reentrant Open**: Specifying a `read_only` option conflicting with an active instance returns `DbError::ConflictingOptions` (preventing silent privilege escalation). `engine_mode` and `sync_mode` conflicts trigger a warning via `on_warning("onOptionsConflict")` and retain active options.

---

## Error Handling (`DbError`)

`carro_kv` uses the standard `DbError` enum implementing `std::error::Error`:

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
