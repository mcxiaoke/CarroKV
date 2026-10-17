//! D2: the compaction-failure contract.
//!
//! A failed automatic compaction must not fail the caller's write. By the time
//! compaction runs, the mutation is already appended and fsynced, so returning an
//! error would invite a retry of a write that succeeded -- and `increment`, being
//! a read-modify-write, would then apply its delta twice. The failure is reported
//! through `onCompactionFailed` and retried later (with a widening backoff, plus
//! one best-effort attempt from `close()`); `strict_compaction` opts into
//! propagating the error instead.
//!
//! Compaction is made to fail with a real filesystem condition rather than a test
//! hook: `commit_flush` writes `<db>.tmp` before rotating, and a *directory* at
//! that path cannot be opened as a file while appends to `<db>` keep working.

use ckv::{Db, DbOptions, StorageEngineMode, SyncMode};
use std::fs;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use tempfile::tempdir;

const TINY_THRESHOLD: u64 = 512;

fn options(warnings: Arc<AtomicU64>) -> DbOptions {
    DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Immediate)
        .compaction_threshold_bytes(TINY_THRESHOLD)
        .on_warning(move |code, _msg| {
            if code == "onCompactionFailed" {
                warnings.fetch_add(1, Ordering::SeqCst);
            }
        })
}

fn tmp_path(db_path: &Path) -> String {
    format!("{}.tmp", db_path.display())
}

fn block_compaction(db_path: &Path) {
    fs::create_dir(tmp_path(db_path)).unwrap();
}

fn unblock_compaction(db_path: &Path) {
    let _ = fs::remove_dir(tmp_path(db_path));
}

/// Seeds the database so the `.tmp` path exists in the same directory before it
/// is blocked (recovery ignores an unremovable `.tmp` when the primary is fine).
fn seed(db_path: &Path, warnings: Arc<AtomicU64>) {
    let db = Db::open(db_path, Some(options(warnings))).unwrap();
    db.put_string("", "seed", "seed_value").unwrap();
    db.close().unwrap();
}

/// D2-1: the write returns success, the failure is warned about, and every write
/// made while compaction was failing is still durable.
#[test]
fn t6_compaction_failure_must_not_fail_the_write() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t6a.db");
    let warnings = Arc::new(AtomicU64::new(0));

    seed(&db_path, warnings.clone());
    block_compaction(&db_path);

    let keys: Vec<(String, String)> = (0..30)
        .map(|i| (format!("k{i:03}"), format!("v{i:03}")))
        .collect();

    {
        let db = Db::open(&db_path, Some(options(warnings.clone()))).unwrap();
        for (key, value) in &keys {
            db.put_string("", key, value)
                .expect("a failed compaction must not fail the write (D2)");
        }
        assert!(
            warnings.load(Ordering::SeqCst) >= 1,
            "the compaction failure must be reported through onCompactionFailed"
        );
        assert!(
            !dir.path().join("t6a.db.bak").exists(),
            "the test must actually have blocked compaction"
        );
        db.close().unwrap();
    }

    // Replay: nothing written while compaction was failing may be lost.
    let db = Db::open(&db_path, Some(options(warnings.clone()))).unwrap();
    assert_eq!(
        db.get_string("", "seed").unwrap(),
        Some("seed_value".to_string())
    );
    for (key, value) in &keys {
        assert_eq!(
            db.get_string("", key).unwrap(),
            Some(value.clone()),
            "key {key} must survive a failed compaction"
        );
    }
    db.close().unwrap();
}

/// D2-4: retries are throttled. With the failure persisting, no retry happens
/// inside the first backoff window (64 writes), and crossing it triggers exactly
/// one retry.
#[test]
fn t6_backoff_must_throttle_retries() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t6b.db");
    let warnings = Arc::new(AtomicU64::new(0));

    seed(&db_path, warnings.clone());
    block_compaction(&db_path);

    let db = Db::open(&db_path, Some(options(warnings.clone()))).unwrap();

    let mut written = 0;
    while warnings.load(Ordering::SeqCst) == 0 {
        db.put_string("", format!("a{written:03}"), "v").unwrap();
        written += 1;
        assert!(written < 500, "the compaction failure was never triggered");
    }
    let first = warnings.load(Ordering::SeqCst);
    assert_eq!(
        first, 1,
        "crossing the threshold triggers exactly one attempt"
    );

    // Still inside the first backoff window (64 writes since the failure).
    for _ in 0..30 {
        db.put_string("", format!("b{written:03}"), "v").unwrap();
        written += 1;
    }
    assert_eq!(
        warnings.load(Ordering::SeqCst),
        first,
        "retries must be suppressed inside the backoff window"
    );

    // Crossing the window attempts exactly one retry (which fails again).
    for _ in 0..40 {
        db.put_string("", format!("c{written:03}"), "v").unwrap();
        written += 1;
    }
    assert_eq!(
        warnings.load(Ordering::SeqCst),
        first + 1,
        "exactly one retry after the backoff window elapses"
    );

    db.close().unwrap();
}

/// D2-3: `close()` gives a pending compaction one more chance, so a transient
/// failure does not outlive the handle.
#[test]
fn t6_close_must_retry_a_pending_compaction() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t6c.db");
    let warnings = Arc::new(AtomicU64::new(0));

    seed(&db_path, warnings.clone());
    block_compaction(&db_path);

    {
        let db = Db::open(&db_path, Some(options(warnings.clone()))).unwrap();
        for i in 0..30 {
            db.put_string("", format!("k{i:03}"), "v").unwrap();
        }
        assert!(warnings.load(Ordering::SeqCst) >= 1);
        assert!(!dir.path().join("t6c.db.bak").exists());

        // Clear the obstacle: the retry from `close()` must now succeed.
        unblock_compaction(&db_path);
        db.close().unwrap();
    }

    assert!(
        dir.path().join("t6c.db.bak").exists(),
        "close() must retry a pending compaction"
    );
}

/// D2-5: `strict_compaction` surfaces the failure to the caller. The write that
/// reports the error is nevertheless durable, which is exactly why the default is
/// not to report it.
#[test]
fn t6_strict_compaction_must_surface_the_error() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t6d.db");
    let warnings = Arc::new(AtomicU64::new(0));

    seed(&db_path, warnings.clone());
    block_compaction(&db_path);

    let mut failing_key = None;
    {
        let db = Db::open(
            &db_path,
            Some(options(warnings.clone()).strict_compaction(true)),
        )
        .unwrap();

        for i in 0..30 {
            let key = format!("k{i:03}");
            if db.put_string("", &key, "v").is_err() {
                failing_key = Some(key);
                break;
            }
        }
        assert!(
            failing_key.is_some(),
            "strict_compaction must surface the compaction failure"
        );
        db.close().unwrap();
    }
    unblock_compaction(&db_path);

    // The documented caveat: an error here means "compaction failed", not "the
    // write was lost" -- the frame was already appended and fsynced.
    let db = Db::open(&db_path, Some(options(warnings.clone()))).unwrap();
    let key = failing_key.unwrap();
    assert_eq!(
        db.get_string("", &key).unwrap(),
        Some("v".to_string()),
        "the write that reported the strict error must still be on disk"
    );
    db.close().unwrap();
}
