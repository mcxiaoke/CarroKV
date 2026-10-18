# CarroKV for Dart / Flutter

**English** | [简体中文](README.zh.md)

**CarroKV for Dart** is a pure Dart native implementation of the CKV cross-language embedded Key-Value database engine, strictly conforming to the [CKV Binary Specification v3.2](../../docs/KV-DB-DESIGN-SPEC.md) and the [Common API Specification](../../docs/API-SPEC.md) ([Chinese](../../docs/API-SPEC.zh.md)).

Designed specifically for Flutter mobile apps (iOS / Android), desktop applications (Windows / macOS / Linux), and Dart CLI / backend services. It requires no native SQLite or C/C++ shared libraries, completely eliminating FFI platform-compatibility issues and crash hazards.

---

## Installation & Setup (Git Dependency)

Add the dependency to your `pubspec.yaml`:

```yaml
dependencies:
  ckv:
    git:
      url: https://github.com/mcxiaoke/CarroKV.git
      ref: v0.2.1 # Can also reference master or a specific commit SHA
      path: src/dart # Subdirectory path is required
```

Import it in your Dart code:

```dart
import 'package:ckv/ckv.dart';
```

---

## Core Features

- **100% Pure Dart (Zero FFI / Zero Native Dependency)**:
  Runs seamlessly across all Flutter platforms and standard Dart VMs with zero C/C++ dynamic libraries and zero native compilation friction.
- **In-Memory Nanosecond Reads**:
  Active data is held in sorted in-memory structures. Point lookups take tens of nanoseconds without blocking the UI thread or causing frame drops.
- **Byte-Level Cross-Language Determinism**:
  Shares identical inline IEEE 802.3 CRC32 checksums and unsigned UTF-8 byte ordering with **Rust**, **.NET**, **Go**, and **Kotlin**. `.db` files are fully hot-swappable across all platforms.
- **Crash-Safe Two-Generation Atomic Rotation**:
  Employs `.tmp -> .bak -> .db` atomic replacement. Recovers within milliseconds if an app terminates abruptly or crashes mid-write.
- **Deadlock-Free Change Listeners (Watcher)**:
  Dispatches change events asynchronously *after* releasing the write lock, guaranteeing reentrant reads from callbacks are safe and deadlock-free.
- **Lock-Free Read-Only Snapshot Isolation**:
  Instantaneous point-in-time snapshots allow long-running range scans to proceed concurrently without blocking or being blocked by background writes.

---

## Quick Start

### 1. Opening a Database & Basic CRUD

```dart
import 'package:ckv/ckv.dart';

void main() {
  // Open or create the database
  final db = CkvDb.open('app_data.db');
  try {
    // 1. Store and retrieve String
    db.putString('users', 'user:1001', 'Alice');
    final name = db.getString('users', 'user:1001');
    print('User Name: $name'); // Output: Alice

    // 2. Store and retrieve raw binary Bytes
    final avatar = [0x01, 0x02, 0x03, 0x04];
    db.putBytes('users', 'user:1001:avatar', avatar);
    final readAvatar = db.getBytes('users', 'user:1001:avatar');

    // 3. Strongly-typed Int64 & atomic increment
    db.putInt64('stats', 'page_views', 1000);
    final newViews = db.incrementInt64('stats', 'page_views', 1);
    print('New Views: $newViews');

    // 4. Key existence check and deletion
    final exists = db.containsKey('users', 'user:1001');
    final deleted = db.delete('users', 'user:1001');
  } finally {
    db.close();
  }
}
```

### 2. Convenience API & Indexer Syntax

When explicit namespace isolation is not needed, all operations target the default namespace `""`, with convenient Dart `[]` / `[]=` indexers:

```dart
// 1. Indexer syntax (operates on default namespace "")
db['theme'] = 'dark';
final theme = db['theme'];

// 2. Convenience CRUD without namespace parameter
db.putString('session_token', 'xyz_token_123');
final token = db.getString('session_token');
final hasToken = db.containsKey('session_token');
db.delete('session_token');

// 3. Namespace Scope facade (Bucket-style pattern)
// Bind a lightweight scope to avoid repeatedly passing the namespace argument:
final users = db.ns('users');
users['user:1001'] = 'Alice';
users.putInt64('user:1001:score', 100);
final userName = users.getString('user:1001');
final userList = users.scanRange(startKey: 'user:1000', endKey: 'user:2000');

// Snapshots and atomic batches also support namespace scopes:
db.batch((b) {
  final batchUsers = b.ns('users');
  batchUsers['user:1002'] = 'Bob';
});
```

### 3. Namespaces & Range Scanning

```dart
// List all namespaces (sorted in ascending unsigned UTF-8 order)
final List<String> namespaces = db.namespaces();

// List matching keys with prefix
final List<String> keys = db.keys('users', prefix: 'user:');

// Buffered prefix scan
final List<Entry> entries = db.iterPrefix('users', prefix: 'user:');
for (final entry in entries) {
  print('${entry.key} -> ${entry.value}');
}

// Half-open range scan [startKey, endKey)
final rangeEntries = db.scanRange('users', startKey: 'user:1000', endKey: 'user:2000');
```

### 4. Atomic Batch Transactions (Batch)

Batch execution supports fluent operations and Read-Your-Own-Writes, committed in a single atomic transaction:

```dart
db.batch((b) {
  b.putString('orders', 'order:001', 'created')
   .putInt64('orders', 'order:001:amount', 9900)
   .delete('orders', 'order:draft:001');
});
```

### 5. Lock-Free Snapshot Isolation

```dart
// Create an instantaneous read-only snapshot
final snap = db.snapshot();
try {
  // Snapshot range scan never blocks or gets blocked by concurrent writes
  final snapItems = snap.scanRange('users');
  for (final item in snapItems) {
    print('Snap: ${item.key}');
  }
} finally {
  snap.close();
}
```

### 6. Data Change Listeners (Watcher)

```dart
// Register a watcher (returns an unwatch handle)
final watcher = db.watch((event) {
  print('Change: op=${event.op}, ns=${event.namespace}, key=${event.key}');
});

// Mutate data; callbacks trigger safely after the write lock is released
db.putString('users', 'user:1002', 'Bob');

// Explicitly unregister
watcher.unwatch();
```

---

## Configuration Options (`DbOptions`)

```dart
final options = DbOptions(
  // ⚠️ snapshotAtomic serializes the full database and triggers fsync on every write, costing O(DB Size).
  //    Measured median steady write latency: 500 entries: 2.29ms; 2,000 entries: 2.93ms; 5,000 entries: 4.43ms.
  //    appendOnlyLog achieves ~0.18ms/write independent of DB size.
  //    For datasets exceeding ~2MB or write-heavy workloads, choose StorageEngineMode.appendOnlyLog.
  engineMode: StorageEngineMode.snapshotAtomic,
  syncMode: SyncMode.immediate,
  syncIntervalMs: 100,
  readOnly: false,
  autoRecoverFromBackup: false,
  lockTimeoutMs: 10000,
);

final db = CkvDb.open('custom.db', options);
```

> **Option Conflict on Reentrant Open**: Specifying a `readOnly` option conflicting with an existing active instance throws `ConflictingOptionsError` (preventing silent privilege escalation). `engineMode` and `syncMode` conflicts trigger a warning via `onWarning("onOptionsConflict")` and retain the active options.

---

## Exception Hierarchy (`DbError`)

All error types inherit from `DbError`:

- `CorruptedError`: Corrupted file format (invalid magic number, failed CRC32 checksum, etc.);
- `LockedError`: Lock timeout or attempted write on a read-only instance;
- `ConcurrentModificationError`: External modification or version conflict detected;
- `TypeMismatchError`: Strict type guard violation (e.g. attempting to read Int64 as String);
- `InvalidUtf8Error`: String value or key contains invalid UTF-8 sequences;
- `KeyTooLargeError`: Key > 64 KB or Value > 16 MB;
- `InvalidNamespaceError`: Namespace name is invalid or exceeds bounds;
- `UnsupportedVersionError`: File version is higher than supported;
- `IoError`: Underlying file system or disk I/O error.
