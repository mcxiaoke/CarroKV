// Probe 2 (lean): isolate STEADY-STATE write latency in AOL mode once live
// data exceeds compaction_threshold_bytes (default 4MB).
//
// Method: bulk-load quickly with compaction OFF (threshold=0), close, then
// reopen with the DEFAULT threshold (4MB) and time individual writes. The file
// is already larger than the threshold, so if maybe_compact_after_write
// triggers on `file_size >= threshold` (rather than on accumulated garbage),
// every write now pays a full-database rewrite.

use ckv::{Db, DbOptions, StorageEngineMode, SyncMode};
use std::time::Instant;

fn wal(threshold: u64) -> DbOptions {
    DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Deferred)
        .sync_interval_ms(3_600_000)
        .compaction_threshold_bytes(threshold)
}

/// Bulk-load `keys` x `val_len` bytes with compaction off, then close.
fn fast_load(path: &std::path::Path, keys: usize, val_len: usize) {
    let db = Db::open(path, Some(wal(0))).unwrap();
    let value = "v".repeat(val_len);
    for i in 0..keys {
        db.put_string("ns", format!("k{i:06}"), &value).unwrap();
    }
    db.close().unwrap();
}

/// Reopen with the given threshold and time `n` writes.
fn steady_state(label: &str, path: &std::path::Path, threshold: u64, n: usize) {
    let db = Db::open(path, Some(wal(threshold))).unwrap();
    let value = "v".repeat(1_000);
    let count = db.count("ns").unwrap();
    let file_len = std::fs::metadata(path).unwrap().len();

    let t = Instant::now();
    for i in 0..n {
        db.put_string("ns", format!("hot{i:04}"), &value).unwrap();
    }
    let per_put_ms = t.elapsed().as_secs_f64() * 1000.0 / n as f64;

    let t2 = Instant::now();
    for _ in 0..n {
        db.put_string("ns", "k000000", &value).unwrap(); // overwrite = pure garbage
    }
    let per_overwrite_ms = t2.elapsed().as_secs_f64() * 1000.0 / n as f64;

    println!(
        "{label:<40} live={count:>6} file={file_len:>9}B | put {:>9.3} ms ({:>7.0} ops/s) | overwrite {:>9.3} ms ({:>7.0} ops/s)",
        per_put_ms, 1000.0 / per_put_ms, per_overwrite_ms, 1000.0 / per_overwrite_ms,
    );
    drop(db);
}

fn main() {
    let dir = tempfile::tempdir().unwrap();

    for (keys, tag) in [(600usize, "0.6MB"), (6_000, "6MB"), (20_000, "20MB")] {
        let path = dir.path().join(format!("s_{tag}.db"));
        fast_load(&path, keys, 1_000);
        steady_state(&format!("live ~{tag}, threshold=0 (off)"), &path, 0, 50);
        steady_state(
            &format!("live ~{tag}, threshold=4MB (DEFAULT)"),
            &path,
            4 * 1024 * 1024,
            50,
        );
        println!();
    }
}
