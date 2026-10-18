# CarroKV (Cross-language Key-Value Database)

**English** | [简体中文](README.zh.md)

**CarroKV** is a lightweight, cross-language, single-file embedded Key-Value database engine, strictly conforming to the [CKV v3.2 Binary Design Specification](docs/KV-DB-DESIGN-SPEC.md) and the [Common API Specification](docs/API-SPEC.md) ([Chinese](docs/API-SPEC.zh.md)).

It is designed for desktop applications, mobile devices, edge computing, and lightweight microservices, targeting **small configuration and state datasets** (single-process, typically < 2 MB, read-heavy).

> **Status: pre-release (0.2.1)**. The on-disk binary format is frozen at v1.0. All language modules are production-ready and covered by automated cross-language regression test suites.
>
> **Explicit Non-Goals**: Multi-process concurrent read/write access to the same database file (a second opener fails fast with `Locked`), and scalability guarantees for datasets exceeding ~10 MB.

---

## Core Design Principles

1. **Zero Native Dependency**
   - No heavy C/C++ shared libraries (no RocksDB, SQLite, LMDB, etc.; no `.dll` / `.so` / `.dylib` to bundle).
   - Each language is implemented 100% in pure managed code or native standard toolchains, completely eliminating crashes and cross-platform deployment friction caused by P/Invoke, CGO, or FFI.
2. **Nanosecond-level In-Memory Read Performance**
   - Data resides in sorted in-memory structures (Rust `BTreeMap`; Go / Kotlin / Dart sorted arrays + binary search; .NET sorted dictionary).
   - Read operations execute with zero system calls, zero disk I/O, and zero deserialization overhead—single point lookups take tens of nanoseconds.
3. **Byte-level Cross-Language Determinism & Interoperability**
   - Unified inline table-driven IEEE 802.3 CRC32 algorithm and unsigned UTF-8 byte ordering (0..255).
   - The same `.db` file can be hot-swapped, read, and written across **Rust**, **.NET**, **Go**, **Kotlin**, and **Dart** with 100% lossless fidelity.
4. **Two-Generation Atomic Rotation & Self-Healing (Crash-Safe & Self-Healing)**
   - Writes use a `.tmp -> .bak -> .db` atomic file rotation model with 0% write amplification.
   - Automatically detects and promotes pending temporary files on startup; recovers from `.bak` in milliseconds if corrupted by power failure, preventing silent data loss.
5. **Deadlock-Free Change Listeners (Watcher)**
   - Real-time event notifications for key creations, updates, deletions, and namespace clearing/dropping.
   - Ironclad rule: events are collected while holding the write lock, but dispatched **only after the write lock is completely released**, ensuring reentrant read operations within listener callbacks are completely safe and deadlock-free.
6. **Lock-Free Snapshot Isolation**
   - Instantaneous $O(1)$ point-in-time snapshot creation using namespace-level Copy-on-Write (Root State CoW), with zero deep-copying of database contents.
   - Snapshot reads hold zero locks; background writes and long-running snapshot range scans **never block each other**. Memory is reclaimed deterministically via reference counting.

---

## Multi-Language Implementation Matrix

| Language | Directory | Package / Dependency | Platform & Runtime Support | Status | Documentation |
| :--- | :--- | :--- | :--- | :---: | :--- |
| **.NET / C#** | [`src/dotnet/`](src/dotnet/) | `CarroKV` (NuGet) | `.NET Standard 2.0+`, `.NET 8.0+`, Mono, Unity | ✅ Ready | [English](src/dotnet/README.md) / [中文](src/dotnet/README.zh.md) |
| **Rust** | [`src/rust/`](src/rust/) | `carro_kv` (crates.io / Git) | `rustc 1.89+` (Windows, Linux, macOS) | ✅ Ready | [English](src/rust/README.md) / [中文](src/rust/README.zh.md) |
| **Go** | [`src/go/`](src/go/) | `github.com/mcxiaoke/CarroKV/src/go` | `Go 1.21+` (Windows, Linux, macOS) | ✅ Ready | [English](src/go/README.md) / [中文](src/go/README.zh.md) |
| **Kotlin** | [`src/kotlin/`](src/kotlin/) | `com.github.mcxiaoke:CarroKV` (JitPack) | `Kotlin 1.9+`, JVM 17+ (Android / Desktop / Server) | ✅ Ready | [English](src/kotlin/README.md) / [中文](src/kotlin/README.zh.md) |
| **Dart** | [`src/dart/`](src/dart/) | `ckv` (Git, path: `src/dart`) | `Dart 3.0+`, Flutter (iOS / Android / Desktop) | ✅ Ready | [English](src/dart/README.md) / [中文](src/dart/README.zh.md) |

---

## Core Feature Overview

### 1. Strongly-Typed Data Support
CarroKV provides native encoding and strict type-guard validation for common application data types:
- **Bytes** (`0x00`): Raw binary byte payload
- **String** (`0x01`): UTF-8 encoded text string (strictly validated for Unicode conformance)
- **Int64** (`0x02`): 64-bit signed two's complement integer in little-endian format
- **Float64** (`0x03`): 64-bit IEEE 754 double-precision float (with NaN canonicalization)
- **Bool** (`0x04`): Single-byte boolean value (`0x00` = false, `0x01` = true)

### 2. Practical Core APIs
- `contains_key(ns, key)`: $O(1)$ in-memory existence check without deserialization or memory allocation.
- `count(ns)`: Returns the total number of active entries in the specified namespace.
- `clear_namespace(ns)`: Empties all data within a namespace while preserving the namespace entry itself, triggering an atomic snapshot sync.
- `drop_namespace(ns)`: Completely removes a namespace and all its key-value pairs.
- `increment_i64(ns, key, delta)`: Atomic 64-bit signed integer increment/decrement. Initializes to `delta` if the key does not exist; increments if the value is an `Int64`; throws `TypeMismatchError` on type conflict.
- **Convenience Access & Namespace Scope Facades**: Supports omitting the `ns` parameter to operate on default namespace `""`. Native indexer syntax `db[key]` is supported in .NET, Kotlin, and Dart. All five languages provide lightweight `db.NS(ns)` / `db.ns(ns)` scope facades (Bucket pattern) to eliminate redundant namespace arguments.

### 3. Transactional Batch Operations
Supports an in-memory staging area for atomic batching. Writes executed within a batch callback remain staged until committed. If an error occurs or an exception is thrown, all staged changes are **completely rolled back**. Upon success, changes are atomically merged into the active state and persisted to disk in a single transaction, followed by event notification to watchers.

Batches support **Read-Your-Own-Writes**: staged changes take precedence, deletions return "non-existent", and untouched keys fall back to committed data, enabling conditional logic ("read-check-write") prior to commit.

### 4. Lexicographical Range Scanning (ScanRange & IterPrefix)
All entries are indexed in strict ascending **unsigned UTF-8 byte order**. Efficient prefix matching (`keys(ns, prefix)`) and range slicing (`scan_range(ns, startKey, endKey)`) are supported natively across all implementations.

### 5. Lock-Free Snapshot Isolation
Calling `snapshot()` creates an instantaneous point-in-time consistent view in nanoseconds. Powered by Copy-on-Write (CoW), snapshot reads are completely lock-free. High-frequency writes and disk flushes on the main database proceed concurrently without blocking or being blocked by snapshot scans—ideal for reports, offline processing, or full-dataset exports.

---

## Engine Modes & Defaults (Selection Guide)

CarroKV defaults to a conservative, **reliability-first** combination: `SnapshotAtomic` + `Immediate`. Every write serializes the entire active dataset into a complete snapshot, invokes `fsync`, and atomically replaces the active database via `.tmp → .bak → .db`. At no point does a partially-written database exist on disk; a sudden power loss loses at most the single write in flight. While simple and robust, each write costs $O(N)$ serialization plus an `fsync`.

| Use Case | Recommended Configuration | Description |
| :--- | :--- | :--- |
| Configs, credentials, auth state, local desktop state (< 2 MB) | **Default** (`SnapshotAtomic` + `Immediate`) | Maximum reliability and simplicity; no tuning needed |
| High-frequency caches, offline event logs, session state (10 ~ 50 MB, periodic pauses acceptable) | `engineMode = AppendOnlyLog` (`Deferred` or `Immediate`) | Append-only frames, ~0.18 ms steady write latency independent of DB size; compactor pauses ≈4 ms/MB when garbage reaches `max(4MB, live/4)` |
| High-throughput bulk imports tolerant of losing ~100 ms on crash | `AppendOnlyLog` or `SnapshotAtomic` + `Deferred` (`syncIntervalMs = 100`) | Highest write throughput; abnormal termination may lose the most recent flush interval |

### ⚠️ Write Latency of Default Engine Scales Linearly with Database Size

In `SnapshotAtomic` mode, every write serializes the full database and triggers an `fsync`, incurring an $O(\text{DB Size})$ overhead. While this is the default mode, the library **enforces no runtime guardrails** to prevent its use on datasets far exceeding recommended sizes. The table below illustrates actual measured median steady-state write latencies:

| Active Entries | `SnapshotAtomic` + `Immediate` | `AppendOnlyLog` |
| ---: | ---: | ---: |
| 500 | 2.29 ms (≈437 ops/s) | 0.18 ms (≈5,500 ops/s) |
| 2,000 | 2.93 ms (≈341 ops/s) | 0.18 ms (≈5,500 ops/s) |
| 5,000 | 4.43 ms (≈226 ops/s) | 0.18 ms (≈5,500 ops/s) |
| ~50,000 (extrapolated, ~5.7 MB) | ~40 ms (≈25 ops/s) | 0.18 ms (≈5,500 ops/s) |

`AppendOnlyLog` steady-state write latency is **independent of database size** (constant across row counts). When scaled to 50 MB, the default mode is approximately two orders of magnitude slower than `AppendOnlyLog`.

> **Recommendation: If your dataset exceeds ~2 MB or involves high-frequency writes, explicitly set `engineMode = AppendOnlyLog`.** This warning is also documented in the `DbOptions.EngineMode` API docs across all supported languages.

---

## Documentation Links

- [📖 Common API Specification (docs/API-SPEC.md)](docs/API-SPEC.md) ([Chinese](docs/API-SPEC.zh.md)): Unified method signatures, options, snapshot semantics, watchers, and error hierarchy across all 5 languages.
- [🚀 Development, Testing, and Release Playbook (docs/DEVELOPMENT-AND-RELEASE-GUIDE.md)](docs/DEVELOPMENT-AND-RELEASE-GUIDE.md): Dev environments, single-language test runs, golden vectors, cross-language interop validation, and multi-platform publishing guide.
- [📐 Binary Design Specification v3.2 (docs/KV-DB-DESIGN-SPEC.md)](docs/KV-DB-DESIGN-SPEC.md): On-disk binary frame layout, locking models, deterministic greedy bin packing, and cross-language requirements.
- [📊 Scale & Latency Benchmarks (docs/BENCHMARKS-20261017.md)](docs/BENCHMARKS-20261017.md): Benchmark results for opening speed, steady-state write latency, and compaction pauses at 2/10/50/100 MB scales.
- [🐛 Comprehensive Five-Language Code Review Report (docs/REVIEW-20261017-201309-full-code-review.md)](docs/REVIEW-20261017-201309-full-code-review.md): Detailed line-by-line review across ~20,000 lines of code in 5 languages, categorizing defects into **7 P0 / 40 P1 / 47 P2 / 40 P3** items with test evidence and remediation roadmap.
- [📝 Historical Design & Changelog (docs/CHANGES-20261017.md)](docs/CHANGES-20261017.md): Architecture refactoring, CI hardening, and multi-language feature tracking.
