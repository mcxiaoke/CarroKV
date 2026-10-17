# ckv (Rust)

Rust implementation of CKV — a lightweight, pure-native, multi-language embedded
Key-Value store for **small configuration/state datasets** (single process,
typically < 2 MB, read-heavy).

- No FFI, no `mmap`, no shared C/C++ libraries: standard library only.
- Byte-identical `.db` files across Rust / Go / .NET / Kotlin / Dart.
- Crash-safe two-generation rotation (`.tmp` → `.bak` → `.db`) with self-healing
  on open.
- Two storage engines: `SnapshotAtomic` (default, whole-file rewrite on write)
  and `AppendOnlyLog` (append + periodic compaction, for higher write rates).

## Explicit non-goals

- **Multi-process concurrent read/write of the same database.** The connection
  lock makes a second opener fail fast with `Locked`; it does not coordinate
  writers.
- **Large datasets.** `SnapshotAtomic` (the default) rewrites the whole file on
  every write, so it is meant for < 2 MB. `AppendOnlyLog` keeps writes flat at
  ~0.2 ms regardless of size, but compaction pauses and open time still grow
  linearly (~4 ms/MB and ~13.5 ms/MB). Measured envelope: **≤ 50 MB**; above
  100 MB neither memory nor open time is characterised. See
  [`docs/BENCHMARKS-20261017.md`](../../docs/BENCHMARKS-20261017.md).

## Requirements

Rust 1.89+ (uses `File::try_lock`).

## Example

```rust
use ckv::Db;

# fn main() -> Result<(), Box<dyn std::error::Error>> {
let db = Db::open("app.db", None)?;
db.put_string("cfg", "theme", "dark")?;
assert_eq!(db.get_string("cfg", "theme")?, Some("dark".to_string()));
db.close()?; // releases the inter-process lock and flushes pending writes
# Ok(())
# }
```

Full specification: [`docs/KV-DB-DESIGN-SPEC.md`](../../docs/KV-DB-DESIGN-SPEC.md).
API freeze baseline: [`docs/API-V1-FREEZE.md`](../../docs/API-V1-FREEZE.md).

## Status

`0.1.0` — pre-release. The binary format is frozen as v1.0; the crate version
moves to `1.0.0` once the release gates in `docs/FIX-PLAN-V2-*.md` pass.

## License

Apache-2.0. See [`LICENSE`](../../LICENSE).
