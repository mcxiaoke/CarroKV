# Changelog

## 0.2.1

- Added convenience API without namespace argument (defaults to `""`).
- Added indexer syntax sugar `db[key]` and `db[key] = value`.
- Added Namespace Scope facade (`db.ns()`, `db.defaultNs`, `snap.ns()`, `batch.ns()`).

## 0.2.0

- Production readiness upgrade aligning with KV-DB-DESIGN-SPEC v3.2.
- Two-phase Immediate write pipeline, deduplicated CoW per namespace, and warning dispatch outside write lock.
- First-class async API `flushAsync()` and `backupAsync()`.
- Added complete package metadata, topics, and analysis linting.

## 0.1.0

- Initial release of CKV for Dart.
- Supports Snapshot and AppendOnlyLog storage engines conforming to KV-DB-DESIGN-SPEC v3.2.
- Point-in-time snapshot isolation, transactions, and multi-namespace support.
- Fully compatible cross-platform binary format and golden vectors.
