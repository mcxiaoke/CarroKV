# CarroKV for .NET

**English** | [简体中文](README.zh.md)

**CarroKV (.NET)** is a high-performance, minimalist, zero-native-dependency, cross-platform embedded Key-Value database engine, strictly conforming to the [CKV Binary Specification v3.2](../../docs/KV-DB-DESIGN-SPEC.md) and the [Common API Specification](../../docs/API-SPEC.md) ([Chinese](../../docs/API-SPEC.zh.md)).

---

## Installation (NuGet)

Install via .NET CLI or NuGet Package Manager:

```bash
dotnet add package CarroKV
```

Import the namespace in C#:

```csharp
using Ckv;
```

---

## Core Features

- **100% Pure Managed Implementation (Zero Native Dependency)**: No native C/C++ dynamic libraries required (no `.dll` / `.so` / `.dylib`). Drop-in deployment completely free of P/Invoke platform compatibility or memory safety hazards.
- **Broad Multi-Target Support**:
  - `netstandard2.0`: Compatible with .NET Standard 2.0+, .NET Framework 4.6.1+, Mono, Xamarin, and Unity environments;
  - `net8.0`: Optimized for modern .NET 8+ with zero-allocation `Span<T>` and high-performance memory primitives.
- **In-Memory Nanosecond Reads**: Read operations require zero deserialization and zero system calls, hitting sorted in-memory structures in tens of nanoseconds.
- **Byte-Level Cross-Language Determinism**: Shares the exact same binary on-disk layout, unsigned UTF-8 byte ordering, and IEEE 802.3 CRC32 checksums with **Rust**, **Go**, **Kotlin**, and **Dart**. Database files can be hot-swapped seamlessly between platforms.
- **Resilient Two-Generation Atomic Rotation**: Writes commit atomically via `.tmp → .bak → .db` renaming. Startup automatically promotes pending `.tmp` files; damaged files prompt restoration from `.bak` (or automatically with `AutoRecoverFromBackup`), preventing silent data loss.
- **Kernel File Locking + In-Process Singleton**: Uses OS-level file locking (Windows `LockFileEx` / Unix `fcntl`) on `<db>.lock` during connection lifetime. Re-opening the same path within the same process safely returns the shared engine instance via a weak-reference registry.
- **Deadlock-Free Change Listeners (Watcher)**: Notifications are dispatched *after* the write lock is released, making reentrant reads inside callbacks completely safe.
- **Lock-Free Snapshot Isolation**: Instantaneous $O(1)$ point-in-time snapshot creation using namespace-level Copy-on-Write (CoW). Snapshot range scans and background writes **never block each other**.

---

## Quick Start

### 1. Opening a Database & Basic CRUD

```csharp
using Ckv;

// Open or automatically create the database.
//
// 💡 Handle Wrapper Architecture (ADR-20261017):
//    Each Open returns an independent facade handle managing an atomic reference
//    count to the underlying engine core. This fully supports idiomatic `using var db = CkvDb.Open(...)`!
//    Each module can independently own and dispose its handle without affecting concurrent modules.
//    The underlying engine only flushes and releases the cross-process lock when all handles are closed.
using var db = CkvDb.Open("app_data.db");

// 1. Store and retrieve String
db.PutString("users", "user:1001", "Alice");
string? name = db.GetString("users", "user:1001");
Console.WriteLine($"Name: {name}"); // Output: Alice

// 2. Store and retrieve raw binary Bytes
byte[] payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
db.PutBytes("users", "user:1001:avatar", payload);
byte[]? avatar = db.GetBytes("users", "user:1001:avatar");

// 3. Strongly-typed integers & atomic increment
db.PutInt64("stats", "page_views", 1000L);
db.IncrementInt64("stats", "page_views", 1L); // Atomic increment

// 4. Key existence check and deletion
bool exists = db.ContainsKey("users", "user:1001");
bool deleted = db.Delete("users", "user:1001");
```

### 2. Convenience API & Indexer Syntax

When explicit namespace isolation is not needed, all operations target the default namespace `""`, with convenient C# indexers:

```csharp
// 1. Indexer syntax (operates on default namespace "")
db["theme"] = "dark";
string? theme = db["theme"];

// 2. Convenience methods without namespace parameter
db.PutString("session_token", "xyz_token_123");
string? token = db.GetString("session_token");
bool hasToken = db.ContainsKey("session_token");
db.Delete("session_token");

// 3. Namespace Scope facade (Bucket-style pattern)
// Bind a lightweight scope to avoid repeatedly passing the namespace argument:
var users = db.NS("users");
users["user:1001"] = "Alice";
users.PutInt64("user:1001:score", 100L);
string? userName = users.GetString("user:1001");
List<Entry> userList = users.ScanRange("user:1000", "user:2000");

// Snapshots and atomic batches also support namespace scopes:
db.Batch(batch =>
{
    var batchUsers = batch.NS("users");
    batchUsers["user:1002"] = "Bob";
});
```

### 3. Namespaces & Range Scanning

```csharp
// List all namespaces (sorted in ascending unsigned UTF-8 byte order)
List<string> namespaces = db.Namespaces();

// List matching keys with prefix
List<string> keys = db.Keys("users", prefix: "user:");

// Range scan [startKey, endKey)
List<Entry> entries = db.ScanRange("users", startKey: "user:1000", endKey: "user:2000");

// Buffered prefix iteration
List<Entry> userEntries = db.IterPrefix("users", prefix: "user:");
```

### 4. Atomic Batch Transactions (Batch)

Batches support method chaining and Read-Your-Own-Writes, committed in a single atomic transaction:

```csharp
db.Batch(batch =>
{
    batch.PutString("orders", "order:001", "Created")
         .PutInt64("orders", "order:001:amount", 9900L)
         .Delete("orders", "order:draft:001");
});
```

### 5. Lock-Free Snapshot Isolation

```csharp
// Instantaneous creation of a point-in-time consistent snapshot
using var snapshot = db.Snapshot();

// Snapshot scanning and background writes never block each other
var snapEntries = snapshot.ScanRange("users");
```

### 6. Data Change Listeners (Watcher)

```csharp
// Register a watcher (returns an unwatch handle)
using var watcher = db.Watch(e =>
{
    Console.WriteLine($"Op={e.Op}, Ns={e.Namespace}, Key={e.Key}");
});

// Reentrant reads within callbacks are completely safe and deadlock-free
db.PutString("users", "user:1002", "Bob");
```

---

## Core API Reference

| Method | Description |
| :--- | :--- |
| `Open(path, options?)` | Open or create DB with in-process singleton protection. Returns independent facade handle with atomic reference counting. |
| `Close()` / `Dispose()` | Releases current handle (idempotent). True flush and lock release occur only when active handle count reaches zero. |
| `ContainsKey(ns, key)` | Checks key existence ($O(1)$ in-memory lookup). |
| `Count(ns)` | Returns active entry count in namespace. |
| `GetString(ns, key)` | Reads UTF-8 string; throws `TypeMismatchError` on type conflict. |
| `GetBytes(ns, key)` | Reads raw binary byte array. |
| `GetInt64(ns, key)` | Reads 64-bit signed integer; throws `TypeMismatchError` on type conflict. |
| `GetFloat64(ns, key)` | Reads 64-bit double float; throws `TypeMismatchError` on type conflict. |
| `GetBool(ns, key)` | Reads boolean value; throws `TypeMismatchError` on type conflict. |
| `PutString(ns, key, val)` | Writes UTF-8 string and syncs to disk. |
| `PutBytes(ns, key, val)` | Writes raw byte array and syncs to disk. |
| `PutInt64(ns, key, val)` | Writes 64-bit signed integer and syncs to disk. |
| `PutFloat64(ns, key, val)`| Writes 64-bit double float and syncs to disk. |
| `PutBool(ns, key, val)` | Writes boolean value and syncs to disk. |
| `IncrementInt64(ns, key, delta)` | Atomically adds delta to 64-bit integer counter; validates type strictly. |
| `Delete(ns, key)` | Deletes key; returns whether key was present. |
| `Namespaces()` | Returns all namespace names in ascending UTF-8 order. |
| `Keys(ns, prefix?)` | Returns matching keys in ascending UTF-8 order. |
| `IterPrefix(ns, prefix?)` | Buffered prefix scan returning matched entries. |
| `ScanRange(ns, startKey?, endKey?)` | Scans half-open `[startKey, endKey)` range in ascending order. |
| `DropNamespace(ns)` | Permanently removes namespace and all its data. |
| `ClearNamespace(ns)` | Wipes entries in namespace while keeping namespace record. |
| `Batch(action)` | Atomic batch execution (all-or-nothing). |
| `Snapshot()` / `CreateSnapshot()` | Creates lock-free point-in-time snapshot (`IDisposable`). |
| `Watch(callback)` | Registers change watcher delegate; returns `WatcherHandle`. |
| `Flush()` / `Compact()` | Forces compaction / sync rewrite. |
| `FileSize()` | Returns current `.db` file size in bytes on disk. |
| `Stats()` | Returns active entry counts, namespaces, and TxID stats. |
| `Backup(destPath)` | Instance method: creates an atomic snapshot copy of live database. |
| `BackupFile(srcPath, destPath, options?)` | **Static helper (ADR-20261017)**: Reuses active instance if open, or opens exclusively, exports, and cleans up. |

---

## Configuration Options (`DbOptions`)

```csharp
var options = new DbOptions
{
    // ⚠️ SnapshotAtomic serializes the full database and triggers fsync on every write, costing O(DB Size).
    //    Measured median steady write latency: 500 entries: 2.29ms; 2,000 entries: 2.93ms; 5,000 entries: 4.43ms.
    //    AppendOnlyLog achieves ~0.18ms/write independent of DB size.
    //    For datasets exceeding ~2MB or write-heavy workloads, choose StorageEngineMode.AppendOnlyLog.
    EngineMode = StorageEngineMode.SnapshotAtomic,
    SyncMode = SyncMode.Immediate,
    SyncIntervalMs = 100,
    ReadOnly = false,
    AutoRecoverFromBackup = false,
    LockTimeoutMs = 10000
};

var db = CkvDb.Open("custom.db", options);
```

> **Option Conflict on Reentrant Open**: Specifying a `ReadOnly` value conflicting with an active instance throws `ConflictingOptionsError` (preventing silent privilege escalation). `EngineMode` and `SyncMode` conflicts trigger a warning via `OnWarning("onOptionsConflict")` and retain existing options.

---

## Exception Hierarchy (`DbError`)

All database exceptions inherit from `DbError` (with legacy `CkvException` aliases preserved):

- `CorruptedError` (`CorruptedDatabaseException`): File corruption, invalid magic number, or CRC32 checksum mismatch;
- `LockedError` (`DatabaseLockedException`): Lock timeout or attempted write on a read-only instance;
- `ConcurrentModificationError` (`ConcurrentModificationException`): External concurrent file modification detected;
- `TypeMismatchError` (`TypeMismatchException`): Strict type guard violation (e.g. attempting to read Int64 as String);
- `InvalidUtf8Error` (`InvalidUtf8Exception`): Value or key contains invalid UTF-8 bytes;
- `KeyTooLargeError` (`KeyTooLargeException`): Key > 64 KB or Value > 16 MB;
- `InvalidNamespaceError` (`InvalidNamespaceException`): Namespace name is invalid or exceeds bounds;
- `UnsupportedVersionError` (`UnsupportedVersionException`): On-disk database format is newer than supported;
- `ConflictingOptionsError`: Conflicting `ReadOnly` option on reentrant open;
- `IoError`: Underlying file system or disk I/O error.
