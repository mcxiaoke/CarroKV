//! Adaptive compaction: reclaim a log whose live payload has collapsed, even
//! when the file never crosses `compaction_threshold_bytes`.
//!
//! Three properties are pinned here:
//!
//! 1. **Dead-ratio trigger** — an under-threshold log that is mostly garbage is
//!    compacted, and no live key is lost by the rewrite;
//! 2. **Floor** — a small log (well under the 2 MB baseline) is *not* rewritten
//!    just because it is 90% dead: the rewrite would cost more than it saves;
//! 3. **Off switch** — `compaction_threshold_bytes == 0` still disables every
//!    automatic compaction path, including the dead-ratio one.

use carro_kv::{Db, DbOptions, StorageEngineMode, SyncMode};
use std::fs;
use tempfile::tempdir;

const MIN_ADAPTIVE_SIZE: u64 = 2 * 1024 * 1024;

fn wal_options(threshold_bytes: u64) -> DbOptions {
    DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Deferred)
        .sync_interval_ms(3_600_000) // never let the background flush interfere
        .compaction_threshold_bytes(threshold_bytes)
}

/// Writes `count` entries of `value_len` bytes into namespace `ns`.
fn fill(db: &Db, ns: &str, count: usize, value_len: usize) {
    let value = "v".repeat(value_len);
    for i in 0..count {
        db.put_string(ns, format!("k{i:05}"), &value).unwrap();
    }
}

#[test]
fn dead_ratio_triggers_compaction_below_the_size_threshold() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("adaptive.db");

    let db = Db::open(&path, Some(wal_options(u64::MAX / 2))).unwrap();
    fill(&db, "ns", 300, 10 * 1024);

    let grown = fs::metadata(&path).unwrap().len();
    assert!(
        grown > MIN_ADAPTIVE_SIZE,
        "log should be above the adaptive baseline, got {grown} bytes"
    );

    // Delete 250 of 300 keys: the live payload collapses while the log keeps its
    // size, so the dead ratio crosses 50% long before any size threshold. The
    // trigger fires as soon as the ratio is crossed, so the rewrite happens
    // mid-way through the delete loop and the file ends up around live-size.
    for i in 0..250 {
        assert!(db.delete("ns", format!("k{i:05}")).unwrap());
    }

    let after = fs::metadata(&path).unwrap().len();
    assert!(
        after < MIN_ADAPTIVE_SIZE,
        "dead-ratio compaction should have reclaimed the log: {grown} -> {after} bytes"
    );

    drop(db);

    let reopened = Db::open(&path, Some(wal_options(u64::MAX / 2))).unwrap();
    assert_eq!(reopened.count("ns").unwrap(), 50);
    for i in 250..300 {
        let key = format!("k{i:05}");
        assert_eq!(
            reopened.get_string("ns", &key).unwrap().unwrap().len(),
            10 * 1024
        );
    }
    for i in 0..250 {
        assert!(reopened
            .get_bytes("ns", format!("k{i:05}"))
            .unwrap()
            .is_none());
    }
}

#[test]
fn small_logs_are_not_rewritten_by_dead_ratio() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("small.db");

    let db = Db::open(&path, Some(wal_options(u64::MAX / 2))).unwrap();
    fill(&db, "ns", 50, 1024);

    let grown = fs::metadata(&path).unwrap().len();
    assert!(
        grown < MIN_ADAPTIVE_SIZE,
        "log must stay below the baseline"
    );

    for i in 0..45 {
        db.delete("ns", format!("k{i:05}")).unwrap();
    }

    let after = fs::metadata(&path).unwrap().len();
    assert!(
        after >= grown && after < MIN_ADAPTIVE_SIZE,
        "a sub-baseline log must not be rewritten for being mostly dead: \
         {grown} -> {after} bytes"
    );
    assert_eq!(db.count("ns").unwrap(), 5);
}

#[test]
fn zero_threshold_disables_the_dead_ratio_trigger_too() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("disabled.db");

    let db = Db::open(&path, Some(wal_options(0))).unwrap();
    fill(&db, "ns", 300, 10 * 1024);

    let grown = fs::metadata(&path).unwrap().len();
    for i in 0..270 {
        db.delete("ns", format!("k{i:05}")).unwrap();
    }

    let after = fs::metadata(&path).unwrap().len();
    assert!(
        after > grown && after > MIN_ADAPTIVE_SIZE,
        "compaction_threshold_bytes = 0 must disable automatic compaction entirely: \
         {grown} -> {after} bytes"
    );
    assert_eq!(db.count("ns").unwrap(), 30);
}
