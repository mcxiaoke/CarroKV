# CarroKV for Go

**English** | [简体中文](README.zh.md)

**CarroKV for Go** is a pure Go native implementation of the CKV embedded Key-Value database engine, strictly conforming to the [CKV Binary Specification v3.2](../../docs/KV-DB-DESIGN-SPEC.md) and the [Common API Specification](../../docs/API-SPEC.md) ([Chinese](../../docs/API-SPEC.zh.md)).

Designed for lightweight microservices, CLI command-line tools, edge computing, and small state stores, without CGO dynamic libraries or heavy dependencies.

---

## Installation & Setup

Install using the Go module tool:

```bash
go get github.com/mcxiaoke/CarroKV/src/go@v0.2.0
```

Import it in your Go code:

```go
import (
    "github.com/mcxiaoke/CarroKV/src/go"
)
```

---

## Core Features

- **100% Pure Go (Zero CGO / Zero FFI)**:
  Cross-compilation is trivial across Windows, Linux, and macOS without CGO performance penalties or runtime overhead.
- **In-Memory Nanosecond Reads**:
  Active data is held in sorted in-memory structures. Point lookups take tens of nanoseconds with zero disk I/O bottlenecks.
- **Byte-Level Cross-Language Determinism**:
  Shares identical inline IEEE 802.3 CRC32 checksums, little-endian encodings, and unsigned UTF-8 byte ordering with **Rust**, **.NET**, **Kotlin**, and **Dart**.
- **Crash-Safe Two-Generation Atomic Rotation**:
  Uses `.tmp -> .bak -> .db` atomic replacement. Automatically promotes valid temporary files and prevents data corruption on crash or power loss.
- **Deadlock-Free Change Listeners (Watcher)**:
  Dispatches events asynchronously *after* releasing the write lock, guaranteeing reentrant reads from callbacks are safe and deadlock-free.
- **Lock-Free Read-Only Snapshot Isolation**:
  Instantaneous point-in-time snapshots allow long-running range scans to proceed concurrently without blocking or being blocked by background writes.

---

## Quick Start

### 1. Opening a Database & Basic CRUD

```go
package main

import (
    "fmt"
    "log"

    "github.com/mcxiaoke/CarroKV/src/go"
)

func main() {
    // 1. Open or create database (using default options)
    db, err := ckv.Open("app_data.db", nil)
    if err != nil {
        log.Fatalf("failed to open db: %v", err)
    }
    defer db.Close()

    // 2. Store and retrieve String
    if err := db.PutString("users", "user:1001", "Alice"); err != nil {
        log.Fatal(err)
    }
    name, err := db.GetString("users", "user:1001")
    if err != nil {
        log.Fatal(err)
    }
    fmt.Printf("User Name: %s\n", *name)

    // 3. Store and retrieve raw binary Bytes
    avatarBytes := []byte{0xDE, 0xAD, 0xBE, 0xEF}
    _ = db.PutBytes("users", "user:1001:avatar", avatarBytes)

    // 4. Strongly-typed Int64 & atomic increment
    _ = db.PutInt64("stats", "page_views", 1000)
    newViews, _ := db.IncrementInt64("stats", "page_views", 1)
    fmt.Printf("New Views: %d\n", newViews)

    // 5. Key existence check and deletion
    if exists, _ := db.ContainsKey("users", "user:1001"); exists {
        deleted, _ := db.Delete("users", "user:1001")
        fmt.Printf("Deleted: %v\n", deleted)
    }
}
```

### 2. Namespaces & Range Scanning

```go
// List all namespaces (sorted in ascending unsigned UTF-8 order)
namespaces := db.Namespaces()

// List matching keys with prefix
keys, _ := db.Keys("users", "user:")

// Buffered prefix scan
entries, _ := db.IterPrefix("users", "user:")
for _, entry := range entries {
    fmt.Printf("Key: %s, Value: %v\n", entry.Key, entry.Value)
}

// Half-open range scan [startKey, endKey)
start := "user:1000"
end := "user:2000"
rangeEntries, _ := db.ScanRange("users", &start, &end)
```

### 3. Atomic Batch Transactions (Batch)

Batch execution supports fluent operations and Read-Your-Own-Writes, committed in a single atomic transaction:

```go
err := db.Batch(func(b *ckv.BatchWriter) error {
    if err := b.PutString("orders", "order:001", "created"); err != nil {
        return err
    }
    if err := b.PutInt64("orders", "order:001:price", 9900); err != nil {
        return err
    }
    return b.Delete("orders", "order:draft:001")
})
```

### 4. Lock-Free Snapshot Isolation

```go
// Create an instantaneous read-only snapshot
snap, err := db.Snapshot()
if err != nil {
    log.Fatal(err)
}
defer snap.Close()

// Snapshot range scan never blocks or gets blocked by concurrent writes
entries, _ := snap.ScanRange("users", nil, nil)
```

### 5. Data Change Listeners (Watcher)

```go
// Register a watcher
watcher := db.Watch(func(ev ckv.KeyChangeEvent) {
    fmt.Printf("Event: Op=%v, Ns=%s, Key=%s\n", ev.Op, ev.Namespace, ev.Key)
})

// Reentrant reads within callbacks are completely safe and deadlock-free
_ = db.PutString("users", "user:1002", "Bob")

// Unregister watcher
watcher.Unwatch()
```

---

## Configuration Options (`Options`)

```go
opts := &ckv.Options{
    // ⚠️ SnapshotAtomic serializes the full database and triggers fsync on every write, costing O(DB Size).
    //    Measured median steady write latency: 500 entries: 2.29ms; 2,000 entries: 2.93ms; 5,000 entries: 4.43ms.
    //    AppendOnlyLog achieves ~0.18ms/write independent of DB size.
    //    For datasets exceeding ~2MB or write-heavy workloads, choose ckv.StorageEngineAppendOnlyLog.
    EngineMode:            ckv.StorageEngineSnapshotAtomic,
    SyncMode:              ckv.SyncModeImmediate,
    SyncIntervalMs:        100,
    ReadOnly:              false,
    AutoRecoverFromBackup: false,
    LockTimeoutMs:         10000,
}

db, err := ckv.Open("custom.db", *opts)
```

> **Option Conflict on Reentrant Open**: Specifying a `ReadOnly` value conflicting with an active instance returns `*ckv.ConflictingOptionsError` (preventing silent privilege escalation). `EngineMode` and `SyncMode` conflicts trigger a warning via `OnWarning("onOptionsConflict")` and retain existing options.

---

## Error Handling

CarroKV exports strongly-typed error structs conforming to the common specification, supporting inspections via `errors.As`:

- `*ckv.CorruptedError`: File corruption, invalid magic bytes, or CRC32 checksum mismatch;
- `*ckv.LockedError`: Lock timeout or attempted write on a read-only instance;
- `*ckv.TypeMismatchError`: Strict type guard violation (e.g. attempting to read Int64 as String);
- `*ckv.ConcurrentModificationError`: External concurrent modification or version conflict;
- `*ckv.KeyTooLargeError`: Key > 64 KB or Value > 16 MB;
- `*ckv.InvalidUtf8Error`: String value or key contains invalid UTF-8 bytes;
- `*ckv.IoError`: Underlying file system or disk I/O error.
