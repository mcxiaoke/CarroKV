# CarroKV for Kotlin (JVM)

**English** | [简体中文](README.zh.md)

**CarroKV for Kotlin** is a pure Kotlin / JVM native implementation of the CKV embedded Key-Value database engine, strictly conforming to the [CKV Binary Specification v3.2](../../docs/KV-DB-DESIGN-SPEC.md) and the [Common API Specification](../../docs/API-SPEC.md) ([Chinese](../../docs/API-SPEC.zh.md)).

Designed for Android applications, Kotlin Multiplatform desktop apps, and backend microservices, without dependencies on SQLite, RocksDB, or C/C++ shared libraries, completely eliminating JNI / FFI crash hazards.

---

## Installation & Setup (JitPack)

### 1. Add JitPack Repository
In your `settings.gradle.kts`:

```kotlin
dependencyResolutionManagement {
    repositories {
        mavenCentral()
        maven { url = uri("https://jitpack.io") }
    }
}
```

### 2. Add Dependency
In your module's `build.gradle.kts`:

```kotlin
dependencies {
    implementation("com.github.mcxiaoke:CarroKV:v0.2.0")
}
```

Import in your Kotlin code:

```kotlin
import ckv.CkvDb
import ckv.DbOptions
```

---

## Core Features

- **100% Pure Kotlin / JVM (Zero Native Dependency)**:
  Requires no `.so` or `.dll` binaries; lightweight, drop-in setup with native JVM cross-platform compatibility.
- **In-Memory Nanosecond Reads**:
  Active data resides in sorted in-memory structures. Point lookups take tens of nanoseconds with zero disk I/O pauses.
- **Byte-Level Cross-Language Determinism**:
  Shares identical inline IEEE 802.3 CRC32 checksums and unsigned UTF-8 byte ordering with **Rust**, **.NET**, **Go**, and **Dart**.
- **Crash-Safe Two-Generation Atomic Rotation**:
  Employs `.tmp -> .bak -> .db` atomic replacement. Recovers within milliseconds if an app terminates abruptly or crashes mid-write.
- **Deadlock-Free Change Listeners (Watcher)**:
  Dispatches change events asynchronously *after* releasing the write lock, guaranteeing reentrant reads from callbacks are safe and deadlock-free.
- **Lock-Free Read-Only Snapshot Isolation**:
  Instantaneous point-in-time snapshots allow long-running range scans to proceed concurrently without blocking or being blocked by background writes.

---

## Quick Start

### 1. Opening a Database & Basic CRUD

```kotlin
import ckv.CkvDb

fun main() {
    // Open or create database (implements AutoCloseable, supports `use` block)
    CkvDb.open("app_data.db").use { db ->
        // 1. Store and retrieve String
        db.putString("users", "user:1001", "Alice")
        val name = db.getString("users", "user:1001")
        println("User Name: $name") // Output: Alice

        // 2. Store and retrieve raw byte array (Bytes)
        val avatar = byteArrayOf(0x01, 0x02, 0x03, 0x04)
        db.putBytes("users", "user:1001:avatar", avatar)
        val readAvatar = db.getBytes("users", "user:1001:avatar")

        // 3. Strongly-typed Int64 & atomic increment
        db.putInt64("stats", "page_views", 1000L)
        val newViews = db.incrementInt64("stats", "page_views", 1L)
        println("New Views: $newViews")

        // 4. Key existence check and deletion
        val exists = db.containsKey("users", "user:1001")
        val deleted = db.delete("users", "user:1001")
    }
}
```

### 2. Namespaces & Range Scanning

```kotlin
// List all namespaces (sorted in ascending unsigned UTF-8 order)
val namespaces: List<String> = db.namespaces()

// List matching keys with prefix
val keys: List<String> = db.keys("users", prefix = "user:")

// Buffered prefix scan
val entries = db.iterPrefix("users", prefix = "user:")
for (entry in entries) {
    println("${entry.key} -> ${entry.value}")
}

// Half-open range scan [startKey, endKey)
val rangeEntries = db.scanRange("users", startKey = "user:1000", endKey = "user:2000")
```

### 3. Atomic Batch Transactions (Batch)

Batch execution supports fluent operations and Read-Your-Own-Writes, committed in a single atomic transaction:

```kotlin
db.batch { b ->
    b.putString("orders", "order:001", "created")
    b.putInt64("orders", "order:001:amount", 9900L)
    b.delete("orders", "order:draft:001")
}
```

### 4. Lock-Free Snapshot Isolation

```kotlin
// Create an instantaneous read-only snapshot
db.snapshot().use { snap ->
    // Snapshot range scan never blocks or gets blocked by concurrent writes
    val snapItems = snap.scanRange("users")
    for (item in snapItems) {
        println("Snap: ${item.key}")
    }
}
```

### 5. Data Change Listeners (Watcher)

```kotlin
// Register a watcher (returns an unwatch handle)
val watcher = db.watch { event ->
    println("Change: op=${event.op}, ns=${event.namespace}, key=${event.key}")
}

// Reentrant reads within callbacks are completely safe and deadlock-free
db.putString("users", "user:1002", "Bob")

// Explicitly unregister
watcher.unwatch()
```

---

## Configuration Options (`DbOptions`)

```kotlin
import ckv.DbOptions
import ckv.StorageEngineMode
import ckv.SyncMode

val options = DbOptions(
    // ⚠️ SNAPSHOT serializes the full database and triggers fsync on every write, costing O(DB Size).
    //    Measured median steady write latency: 500 entries: 2.29ms; 2,000 entries: 2.93ms; 5,000 entries: 4.43ms.
    //    APPEND_ONLY_LOG achieves ~0.18ms/write independent of DB size.
    //    For datasets exceeding ~2MB or write-heavy workloads, choose StorageEngineMode.APPEND_ONLY_LOG.
    engineMode = StorageEngineMode.SNAPSHOT,
    syncMode = SyncMode.IMMEDIATE,
    syncIntervalMs = 100,
    readOnly = false,
    autoRecoverFromBackup = false,
    lockTimeoutMs = 10000
)

val db = CkvDb.open("custom.db", options)
```

> **Option Conflict on Reentrant Open**: Specifying a `readOnly` option conflicting with an active instance throws `ConflictingOptionsError` (preventing silent privilege escalation). `engineMode` and `syncMode` conflicts trigger a warning via `onWarning("onOptionsConflict")` and retain active options.

---

## Exception Hierarchy (`DbError`)

All error types inherit from standard `DbError`:

- `CorruptedError`: File corruption, invalid magic bytes, or CRC32 checksum mismatch;
- `LockedError`: Lock timeout or attempted write on a read-only instance;
- `ConcurrentModificationError`: External concurrent modification or version conflict;
- `TypeMismatchError`: Strict type guard violation (e.g. attempting to read Int64 as String);
- `InvalidUtf8Error`: String value or key contains invalid UTF-8 bytes;
- `KeyTooLargeError`: Key > 64 KB or Value > 16 MB;
- `InvalidNamespaceError`: Namespace name is invalid or exceeds bounds;
- `UnsupportedVersionError`: On-disk database format is newer than supported;
- `IoError`: Underlying file system or disk I/O error.
