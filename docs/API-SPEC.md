# CKV Common API Specification

**English** | [简体中文](API-SPEC.zh.md)

> - **Target Scope**: The five official CKV implementations (**Rust**, **.NET**, **Go**, **Kotlin**, **Dart**) and future language bindings.
> - **Specification Version**: v1.0 (API Frozen)
> - **Superordinate Specification**: [KV-DB-DESIGN-SPEC.md](KV-DB-DESIGN-SPEC.md) (Binary on-disk layout spec v3.2; this document freezes in-process public APIs at the language level).

---

## 1. Specification Design Principles

1. **Strict Binary & Disk Layout Alignment**: All language implementations share identical on-disk layout (32-byte file header + 28-byte frame header + IEEE 802.3 CRC32 checksums), guaranteeing 100% lossless hot-swappable data file exchange across all five languages.
2. **Cross-Language Interface Consistency**: Core concepts, method sets, parameter semantics, default configurations, and state transitions remain strictly equivalent across all platforms.
3. **Idiomatic Native Conventions**:
   - **Rust**: `snake_case`, returns `Result<T, DbError>`, borrow checking, and RAII resource management;
   - **.NET / C#**: `PascalCase`, implements `IDisposable`, throws exceptions;
   - **Go**: `CamelCase`, returns `(T, error)`, uses `defer db.Close()`;
   - **Kotlin**: `camelCase`, implements `AutoCloseable`, provides extension functions and `use` block support;
   - **Dart**: `camelCase`, asynchronous event-loop friendly with synchronous in-memory state, single-isolate safe.
4. **Strict Type-Guard Enforcement**: Native encoding and decoding for primitive types without silent type pollution; attempting to read a key using a type incompatible with the stored data MUST throw/return `TypeMismatchError`.
5. **Deadlock-Free Event Notifications**: The Watcher architecture strictly adheres to the rule: events are buffered while holding the write lock and dispatched **only after the write lock is completely released**, ensuring reentrant read calls within listener callbacks are completely safe.

---

## 2. Five-Language Core API Mapping Matrix

| Category / Operation | Unified Semantics | Rust (`carro_kv`) | .NET (`CarroKV`) | Go (`CarroKV/src/go`) | Kotlin (`CarroKV`) | Dart (`ckv`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Open Database** | Open or create DB | `Db::open(path, opts)` | `CkvDb.Open(path, opts)` | `ckv.Open(path, opts)` | `CkvDb.open(path, opts)` | `CkvDb.open(path, opts)` |
| **Close Database** | Release kernel lock & threads | `db.close()` / `drop(db)` | `db.Dispose()` / `Close()` | `db.Close()` | `db.close()` | `db.close()` |
| **Check Closed** | Check if instance is closed | `db.is_closed()` | `db.IsClosed` | `db.IsClosed()` | `db.isClosed` | `db.isClosed` |
| **Put Raw Bytes** | Store raw binary (`0x00`) | `put_bytes(ns, k, v)` | `PutBytes(ns, k, v)` | `PutBytes(ns, k, v)` | `putBytes(ns, k, v)` | `putBytes(ns, k, v)` |
| **Get Raw Bytes** | Read raw binary | `get_bytes(ns, k)` | `GetBytes(ns, k)` | `GetBytes(ns, k)` | `getBytes(ns, k)` | `getBytes(ns, k)` |
| **Put/Get String** | UTF-8 String (`0x01`) | `put_string` / `get_string` | `PutString` / `GetString` | `PutString` / `GetString` | `putString` / `getString` | `putString` / `getString` |
| **Put/Get Int64** | Signed Int64 (`0x02`) | `put_i64` / `get_i64` | `PutInt64` / `GetInt64` | `PutInt64` / `GetInt64` | `putInt64` / `getInt64` | `putInt64` / `getInt64` |
| **Put/Get Float64** | IEEE 754 Float64 (`0x03`) | `put_f64` / `get_f64` | `PutFloat64` / `GetFloat64` | `PutFloat64` / `GetFloat64` | `putFloat64` / `getFloat64` | `putFloat64` / `getFloat64` |
| **Put/Get Bool** | Boolean (`0x04`) | `put_bool` / `get_bool` | `PutBool` / `GetBool` | `PutBool` / `GetBool` | `putBool` / `getBool` | `putBool` / `getBool` |
| **Atomic Increment** | Signed two's-complement add | `increment_i64(ns, k, d)` | `IncrementInt64(ns, k, d)` | `IncrementInt64(ns, k, d)` | `incrementInt64(ns, k, d)` | `incrementInt64(ns, k, d)` |
| **Delete Key** | Remove entry (tombstone) | `delete(ns, k)` | `Delete(ns, k)` | `Delete(ns, k)` | `delete(ns, k)` | `delete(ns, k)` |
| **Key Existence** | $O(1)$ in-memory check | `contains_key(ns, k)` | `ContainsKey(ns, k)` | `ContainsKey(ns, k)` | `containsKey(ns, k)` | `containsKey(ns, k)` |
| **Entry Count** | Live entry count in namespace | `count(ns)` | `Count(ns)` | `Count(ns)` | `count(ns)` | `count(ns)` |
| **List Namespaces** | All namespaces in UTF-8 order | `namespaces()` | `Namespaces()` | `Namespaces()` | `namespaces()` | `namespaces()` |
| **List Keys** | Keys in UTF-8 order | `keys(ns)` | `Keys(ns)` | `Keys(ns)` | `keys(ns)` | `keys(ns)` |
| **Prefix Iteration** | Buffered prefix scan | `iter_prefix(ns, prefix)` | `IterPrefix(ns, prefix)` | `IterPrefix(ns, prefix)` | `iterPrefix(ns, prefix)` | `iterPrefix(ns, prefix)` |
| **Range Slice Scan** | Half-open `[start, end)` scan | `scan_range(ns, s, e)` | `ScanRange(ns, s, e)` | `ScanRange(ns, s, e)` | `scanRange(ns, s, e)` | `scanRange(ns, s, e)` |
| **Clear Namespace** | Wipe entries; keep namespace | `clear_namespace(ns)` | `ClearNamespace(ns)` | `ClearNamespace(ns)` | `clearNamespace(ns)` | `clearNamespace(ns)` |
| **Drop Namespace** | Remove namespace and all keys | `drop_namespace(ns)` | `DropNamespace(ns)` | `DropNamespace(ns)` | `dropNamespace(ns)` | `dropNamespace(ns)` |
| **Atomic Batch** | All-or-nothing atomic commit | `batch { ... }` | `Batch(b => { ... })` | `Batch(func(b) error)` | `batch { ... }` | `batch((b) { ... })` |
| **Read-Only Snapshot**| $O(1)$ CoW isolated snapshot | `db.snapshot()` | `db.Snapshot()` | `db.Snapshot()` | `db.snapshot()` | `db.snapshot()` |
| **Watch Changes** | Subscribe to key/ns mutations | `db.watch(cb) -> handle` | `db.Watch(cb) -> handle` | `db.Watch(cb) -> watcher` | `db.watch(cb) -> handle` | `db.watch(cb) -> handle` |
| **Unwatch** | Deregister mutation callback | `handle.unwatch()` | `handle.Dispose()` | `watcher.Unwatch()` | `handle.unwatch()` | `handle.unwatch()` |
| **Flush to Disk** | Trigger compact / rewrite sync | `db.flush()` / `compact()` | `db.Flush()` / `Compact()` | `db.Flush()` / `Compact()` | `db.flush()` / `compact()` | `db.flush()` / `compact()` |
| **Physical File Size**| File size in bytes on disk | `db.file_size()` | `db.FileSize()` | `db.FileSize()` | `db.fileSize()` | `db.fileSize()` |
| **Database Stats** | Entry counts & TxID metrics | `db.stats()` | `db.Stats()` | `db.Stats()` | `db.stats()` | `db.stats()` |
| **Hot Backup** | Non-disruptive snapshot export | `db.backup(dest_path)` | `db.Backup(dest_path)` | `db.Backup(dest_path)` | `db.backup(dest_path)` | `db.backup(dest_path)` |

---

## 3. Detailed Interface Specifications & Contractual Behaviors

### 3.1 Database Lifecycle & Configuration Options (`DbOptions`)

Each language uses a dedicated configuration structure (`DbOptions` / `Options`) to configure engine behavior.

#### Core Configuration Options & Defaults:
* **`read_only` (bool, default: `false`)**:
  Opens the database in read-only mode. Write/mutate operations are strictly disallowed (invoking write APIs throws/returns `LockedError`); however, **calling `backup(dest)` is permitted** to allow non-disruptive exports.
* **`storage_engine_mode` (default: `SnapshotAtomic`)**:
  - `SnapshotAtomic`: Full-snapshot atomic rotation with 0% write amplification, optimized for datasets < 2 MB;
  - `AppendOnlyLog`: WAL append-only mode with ultra-low steady-state write latency, optimized for write-heavy workloads.
* **`sync_mode` (default: `Always`)**:
  - `Always`: Every write synchronously blocks until the file is flushed and fsynced;
  - `Periodic`: A background timer periodically flushes buffered changes;
  - `Deferred`: Writes flag a dirty state, and a debounced background thread performs flushes;
  - `Manual`: In-memory only; persistence occurs only when explicitly calling `flush()`.
* **`sync_interval_ms` (u64/int, default: `50`)**:
  Flush interval for background worker threads in `Periodic` or `Deferred` mode.
* **`lock_timeout_ms` (u32/int, default: `10000`, 10 seconds)**:
  Maximum duration to wait for acquiring the cross-process OS kernel file lock. Throws/returns `LockedError` on timeout.
* **`auto_recover_from_backup` (bool, default: `false`)**:
  Controls whether to automatically restore and promote from `.bak` if the primary `.db` file suffers physical corruption. Ignored in read-only mode.
* **`auto_compaction_threshold` (u64/long, default: `4194304`, 4 MB)**:
  Minimum physical file size threshold to trigger background compaction in WAL mode.
* **`dead_ratio_compaction_threshold` (f64/double, default: `0.5`, 50%)**:
  Ratio of dead/tombstone bytes to total file size required to trigger background compaction in WAL mode.

#### In-Process Singleton & Ownership Model (ADR-20261017):
- **Weak-Reference Registry**: Calling `Open` multiple times with the same normalized absolute path within the same process returns independent facade handles wrapping the **same underlying core engine (`DbCore`)**. Configuration parameters are locked to the initial `Open` call.
- **Reference Counting & Independent Handles**: Each `Open` increments the core reference count. Consumers can idiomatically use local scopes (e.g. `using var db = ...` in C# or `db.use { ... }` in Kotlin). Closing an individual handle decrements the count and marks only that handle as closed; repeated `close()` calls on the same handle are harmless no-ops.
- **Physical Close Trigger**: Only when all active handles in the process are closed (reference count drops to zero) or collected does the underlying engine perform its final flush and release the cross-process file lock.
- **Option Conflict Handling (Batch 1 Revision)**: When a secondary open specifies a `ReadOnly` value conflicting with the active instance, it **MUST throw `ConflictingOptionsError`** (§4). It must never silently reuse an instance with mismatched permissions. When `EngineMode` or `SyncMode` conflict, the engine dispatches a warning via `onWarning("onOptionsConflict")` and continues using the existing options.
- **Backup Paradigms (Batch 2 Revision)**:
  - *Active / Online Database*: Directly call the instance method `db.Backup(dest)` to create an atomic snapshot copy without interrupting or closing the active instance.
  - *Offline / One-Shot Backup*: Use the static convenience method `CkvDb.BackupFile(src, dst)` to safely inspect the registry, reuse active instances if present, or open exclusively, export, and release.

---

### 3.2 Strongly-Typed Read/Write & Type Guards

1. **Type Code Mapping (ABI Specification)**:
   - `0x00`: **Bytes** (raw binary byte array)
   - `0x01`: **String** (UTF-8 encoded text string, strictly validated for Unicode correctness)
   - `0x02`: **Int64** (64-bit signed integer, little-endian)
   - `0x03`: **Float64** (64-bit double-precision IEEE 754 float; NaN values are canonicalized on write)
   - `0x04`: **Bool** (single-byte boolean; `0x00` = false, `0x01` = true)
2. **Type-Guard Enforcement**:
   - If a key was stored as an `Int64`, subsequent invocations of `GetString` or `GetBytes` **MUST throw/return `TypeMismatchError`**. Silent data coercion or returning raw bytes without type verification is strictly forbidden.
3. **Non-Existent Keys**:
   - Querying a non-existent key returns null / empty (Rust returns `Ok(None)`; Go returns `(nil, nil)`; .NET, Kotlin, and Dart return `null`). It is not treated as an error condition.
4. **Key and Value Size Boundaries**:
   - Maximum Key Size: `65,535` bytes (64 KB - 1);
   - Maximum Value Size: `16,777,215` bytes (16 MB - 1);
   - Exceeding either boundary causes the API to immediately fail with `KeyTooLargeError`.

---

### 3.3 Namespaces & Range Scanning

1. **Default Namespace**:
   - Passing `""` (empty string) or `null` selects the default namespace.
2. **Sort Order**:
   - Keys returned by `namespaces()` and `keys(ns)` MUST be sorted in strict ascending **unsigned UTF-8 byte order** (0..255).
3. **Prefix Iteration `iter_prefix(ns, prefix)`**:
   - Buffered snapshot semantics: collects matching entries into an isolated collection before returning, preventing internal lock leakage or dangling iterators.
   - Any keys containing invalid UTF-8 bytes are safely skipped.
4. **Clear vs. Drop**:
   - `clear_namespace(ns)`: Preserves the namespace metadata (still listed in `namespaces()`), but marks all existing key-value entries as deleted.
   - `drop_namespace(ns)`: Completely removes the namespace and all its underlying data.
5. **Namespace-Omitted Convenience APIs and Namespace Scopes**:
   - **Overload-capable languages (.NET / Kotlin)**: Direct overloads omitting the `ns` parameter operate on the default namespace (`""`); native string indexers / operators (e.g., C# `db[key]`, Kotlin `db[key]`) are provided;
   - **Scope facades (cross-language alignment)**: All languages support `db.NS(ns)` / `db.ns(ns)` / `db.default_ns()` / `db.defaultNs` / `db.Default()` to obtain a lightweight zero-allocation namespace facade (`NamespaceScope`, `SnapshotNamespaceScope`, `BatchNamespaceScope`), removing the need to repeatedly pass the namespace argument.

---

### 3.4 Atomic Batch Operations (Batch)

Batch execution allows atomic commits of multiple writes and deletes under a single write lock:
* **All-or-Nothing Guarantee**: If any failure or exception occurs during the batch callback, all staged mutations are discarded, leaving in-memory and on-disk states untouched.
* **Single Commit & Sync**: An entire batch is assigned a single monotonic `TxID` and committed via a single atomic file rename or WAL append.
* **Read-Your-Own-Writes**: Inside the batch callback, mutations staged earlier in the same batch can be read immediately, allowing transactional read-evaluate-write patterns prior to commit.

---

### 3.5 Lock-Free Snapshot Isolation

1. **Nanosecond-Scale Creation**: Powered by namespace-level Copy-on-Write (Root State CoW), creating a snapshot requires only reading the pointer and incrementing reference counters (tens of nanoseconds).
2. **Lock-Free Concurrency**:
   - Snapshot reads hold zero locks;
   - Background writes, WAL appends, and disk flushes on the primary database **never block or get blocked by** long-running snapshot range scans.
3. **Gentle Closed Semantics**:
   - Once a snapshot is closed or disposed, subsequent read attempts gracefully return null / empty rather than throwing fatal exceptions.

---

### 3.6 Data Change Listeners (Watcher)

1. **Four ABI Mutation Events**:
   ```protobuf
   Put           = 1  // Key created or value replaced
   Delete        = 2  // Key deleted (tombstone)
   DropNamespace = 3  // Entire namespace dropped
   Clear         = 4  // Namespace cleared
   ```
2. **Deadlock-Free Dispatch Guarantee**:
   - Mutation events are buffered internally while the database holds the write lock;
   - Events are dispatched to registered callbacks **only after the write lock is completely released**;
   - Listeners may safely perform reentrant reads (`db.get_*()`, `db.count()`) without risking deadlocks.
3. **Explicit Unregistration**:
   - `watch()` returns an unregistration handle (`WatcherHandle` / `Watcher`);
   - Calling `unwatch()` or `handle.Dispose()` stops further notifications immediately.

---

## 4. Unified Error Hierarchy Mapping

All five language implementations map to identical error categories with consistent semantics:

| Standard Error Type | Trigger Conditions | Rust (`DbError`) | .NET (Exception Class) | Go (Exported Error Type) | Kotlin (Exception Class) | Dart (Exception Class) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`DbError`** | Root error / exception | `DbError` (enum) | `DbError` (extends Exception) | `DbError` (interface) | `DbError` (extends Exception) | `DbError` (implements Exception) |
| **`CorruptedError`** | Magic checksum mismatch, CRC32 error, broken frames | `DbError::Corrupted(String)` | `CorruptedError` | `*CorruptedError` | `CorruptedError` | `CorruptedError` |
| **`LockedError`** | File lock acquisition timed out, or write attempted in read-only mode | `DbError::Locked(String)` | `LockedError` | `*LockedError` | `LockedError` | `LockedError` |
| **`ConcurrentModificationError`** | External file modification or concurrent version collision | `DbError::ConcurrentModification(String)` | `ConcurrentModificationError` | `*ConcurrentModificationError` | `ConcurrentModificationError` | `ConcurrentModificationError` |
| **`InvalidUtf8Error`** | Key or String value contains invalid UTF-8 sequences | `DbError::InvalidUtf8(String)` | `InvalidUtf8Error` | `*InvalidUtf8Error` | `InvalidUtf8Error` | `InvalidUtf8Error` |
| **`KeyTooLargeError`** | Key > 64 KB or Value > 16 MB | `DbError::KeyTooLarge(String)` | `KeyTooLargeError` | `*KeyTooLargeError` | `KeyTooLargeError` | `KeyTooLargeError` |
| **`InvalidNamespaceError`** | Namespace name is invalid or exceeds bounds | `DbError::InvalidNamespace(String)` | `InvalidNamespaceError` | `*InvalidNamespaceError` | `InvalidNamespaceError` | `InvalidNamespaceError` |
| **`UnsupportedVersionError`** | On-disk database version is newer than supported format | `DbError::UnsupportedVersion(String)` | `UnsupportedVersionError` | `*UnsupportedVersionError` | `UnsupportedVersionError` | `UnsupportedVersionError` |
| **`TypeMismatchError`** | Attempting to read value with mismatched type (e.g. Int64 as String) | `DbError::TypeMismatch(String)` | `TypeMismatchError` | `*TypeMismatchError` | `TypeMismatchError` | `TypeMismatchError` |
| **`IoError`** | Operating system I/O, file permission, or disk failure | `DbError::Io(std::io::Error)` | `IoError` | `*IoError` | `IoError` | `IoError` |
| **`ConflictingOptionsError`** | Reentrant open on same path with conflicting `ReadOnly` setting | `DbError::ConflictingOptions(String)` | `ConflictingOptionsError` | `*ConflictingOptionsError` | `ConflictingOptionsError` | `ConflictingOptionsError` |

> **Boundaries of `ConflictingOptionsError` (Batch 1 Addition)**: Under in-process singleton semantics, a cache-hit `open()` returns the existing active instance. Thus, it cannot honor a `ReadOnly` request that contradicts the active instance. Opening with `ReadOnly = true` and silently receiving a read-write instance constitutes **silent privilege escalation**. Conversely, opening with `ReadOnly = false` on an active read-only instance causes future writes to fail far away with `LockedError`. Both directions MUST be rejected with `ConflictingOptionsError`. In contrast, `EngineMode` and `SyncMode` discrepancies affect only performance and sync timing; they dispatch an `onWarning("onOptionsConflict")` warning and preserve existing options.

---

## 5. Cross-Language Conformance Checklist

When implementing new language bindings or conducting cross-language verification, the following checks MUST pass:
1. **Golden Vectors**: Must cleanly parse all deterministic test fixtures in `testdata/` without data corruption;
2. **IEEE 802.3 CRC32**: Every language must yield identical CRC32 checksums for identical byte sequences;
3. **Interoperability (Interop)**: A `.db` file written by any of the five languages must be completely readable and verifiable by all other four languages, and vice versa.
