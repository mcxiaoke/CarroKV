//! FIX-21 regression: the automatic-compaction trigger must measure **garbage**
//! (file size minus live payload), not the absolute file size.
//!
//! With the old `new_file_size >= compaction_threshold_bytes` condition, a
//! compaction rewrites the file down to exactly the live size, so once the live
//! data reaches the threshold *every* subsequent write satisfied the condition and
//! paid for a full-database rewrite (measured: 26 ms/write at 6 MB live, i.e.
//! ~38 ops/s instead of ~5,000).
//!
//! These assertions are deliberately behavioural (file size deltas), not timing
//! based: wall-clock thresholds are flaky on CI runners.

use carro_kv::{Db, DbOptions, StorageEngineMode, SyncMode};

const THRESHOLD: u64 = 4 * 1024 * 1024;

fn wal(threshold: u64) -> DbOptions {
    DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Deferred)
        .sync_interval_ms(3_600_000)
        .compaction_threshold_bytes(threshold)
}

/// Loads `keys` x 1 KB with compaction disabled, so the fixture itself never
/// compacts and the reopened database starts with a clean (garbage-free) log.
fn load_fixture(path: &std::path::Path, keys: usize) {
    let value = "v".repeat(1000);
    let db = Db::open(path, Some(wal(0))).unwrap();
    for i in 0..keys {
        db.put_string("ns", format!("k{i:06}"), &value).unwrap();
    }
    db.flush().unwrap();
    db.close().unwrap();
}

#[test]
fn overwrites_below_threshold_do_not_rewrite_the_whole_database() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("cliff.db");
    load_fixture(&path, 6_000); // ~6 MB live, already above the 4 MB threshold

    let value = "v".repeat(1000);
    let db = Db::open(&path, Some(wal(THRESHOLD))).unwrap();
    let before = db.stats().file_size;
    assert!(
        before >= 5_500_000,
        "fixture unexpectedly small: {before} bytes"
    );

    // 50 overwrites = ~50 KB of pure garbage, 80x below the 4 MB threshold: the
    // log must simply grow. Under the file-size trigger each of these rewrote the
    // whole 6 MB database and the file never grew.
    for i in 0..50 {
        db.put_string("ns", format!("k{i:06}"), &value).unwrap();
    }
    let after = db.stats().file_size;
    db.close().unwrap();

    assert!(
        after >= before + 40_000,
        "file did not grow ({before} -> {after}): every write is still triggering \
         a full-database rewrite"
    );
}

#[test]
fn garbage_past_the_threshold_still_compacts() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("reclaim.db");
    load_fixture(&path, 6_000);

    let value = "v".repeat(1000);
    let db = Db::open(&path, Some(wal(THRESHOLD))).unwrap();
    let before = db.stats().file_size;

    // ~4.5 MB of pure garbage: must be reclaimed once, taking the file back to
    // roughly the live size (~6 MB).
    for i in 0..4_500 {
        db.put_string("ns", format!("k{i:06}"), &value).unwrap();
    }
    let after = db.stats().file_size;
    db.close().unwrap();

    assert!(
        after < before + 2_000_000,
        "compaction did not reclaim {before} -> {after} bytes of garbage"
    );
}
