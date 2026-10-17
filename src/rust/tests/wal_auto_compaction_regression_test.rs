//! Regression tests for the two WAL paths that previously had **zero** coverage:
//!
//! 1. the *automatic* (threshold-triggered) compaction path — every existing WAL
//!    test either used the default 4 MiB threshold (never reached) or called
//!    `compact()` by hand, which is why C1/C2 slipped through;
//! 2. the classification of a damaged log record — the existing tail test only
//!    appends garbage, it never damages a record that is followed by intact ones.
//!
//! Covered defects:
//!
//! * **C1** — compaction triggered from inside `append_entries` serializes a
//!   snapshot of an in-memory state that does not yet contain the mutation being
//!   written, so that write is appended and then immediately overwritten.
//! * **C2** — compaction performed while the caller still holds the `.db` lock,
//!   so compaction can never succeed (observed on Go/.NET/Dart).
//! * **C4** — damage in the middle of the log (a record that is *followed* by
//!   intact records) was treated as a torn tail and silently truncated away.
//!
//! Every assertion checks the **whole key set** after a **reopen**, and the
//! "damage" is injected by flipping bytes in a real log file rather than by
//! hand-building a synthetic buffer.

use ckv::{Db, DbError, DbOptions, StorageEngineMode, SyncMode};
use std::fs::{self, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::Path;
use std::time::{Duration, Instant};
use tempfile::tempdir;

/// 32-byte file header that precedes the first frame.
const FILE_HEADER_LEN: u64 = 32;

/// Small enough that a handful of writes crosses it, large enough that a single
/// frame does not.
const TINY_THRESHOLD: u64 = 512;

fn wal_options(threshold_bytes: u64) -> DbOptions {
    DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Immediate)
        .compaction_threshold_bytes(threshold_bytes)
}

fn flip_byte(path: &Path, offset: u64) {
    let mut file = OpenOptions::new()
        .read(true)
        .write(true)
        .open(path)
        .unwrap();
    file.seek(SeekFrom::Start(offset)).unwrap();
    let mut byte = [0u8; 1];
    file.read_exact(&mut byte).unwrap();
    byte[0] ^= 0xFF;
    file.seek(SeekFrom::Start(offset)).unwrap();
    file.write_all(&byte).unwrap();
    file.sync_all().unwrap();
}

/// Byte length of the frame produced by exactly one `put_string("", "a", "1")`.
/// Derived empirically so the test does not depend on the on-disk frame layout.
fn probe_frame_len(dir: &Path) -> u64 {
    let probe = dir.join("probe_frame_len.db");
    {
        let db = Db::open(&probe, Some(wal_options(u64::MAX - 1))).unwrap();
        db.put_string("", "a", "1").unwrap();
        db.close().unwrap();
    }
    let total = fs::metadata(&probe).unwrap().len();
    assert!(
        total > FILE_HEADER_LEN,
        "probe database must contain one frame, got {total} bytes"
    );
    total - FILE_HEADER_LEN
}

/// C1: the write that crosses the compaction threshold must survive a reopen,
/// together with every other write, and the log must really have been compacted.
#[test]
fn t1_auto_compaction_must_not_lose_writes() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t1.db");
    let opts = wal_options(TINY_THRESHOLD);

    let expected: Vec<(String, String)> = (0..300)
        .map(|i| (format!("key{i:04}"), format!("value{i:04}")))
        .collect();

    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        for (key, value) in &expected {
            db.put_string("", key, value).unwrap();
        }
        db.close().unwrap();
    }

    // Guard against a false pass: if the threshold was never crossed the test
    // would succeed while proving nothing. Rotation during compaction produces
    // `.bak` on the first successful compaction.
    assert!(
        dir.path().join("t1.db.bak").exists(),
        "auto compaction never ran (threshold {TINY_THRESHOLD} bytes was not reached \
         or the compaction path is dead) -- test is vacuous"
    );

    let db = Db::open(&db_path, Some(opts)).unwrap();
    let mut broken = Vec::new();
    for (key, value) in &expected {
        match db.get_string("", key) {
            Ok(Some(got)) if &got == value => {}
            Ok(other) => broken.push(format!("{key}: expected {value:?}, got {other:?}")),
            Err(err) => broken.push(format!("{key}: read error {err:?}")),
        }
    }
    assert!(
        broken.is_empty(),
        "{} of {} keys did not survive auto compaction: {:?}",
        broken.len(),
        expected.len(),
        &broken[..broken.len().min(5)]
    );
}

/// C2: threshold-triggered compaction must actually complete, reclaim space, and
/// must not stall each write for a lock timeout.
#[test]
fn t2_auto_compaction_must_run_without_lock_stalls() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t2.db");
    let opts = wal_options(TINY_THRESHOLD);

    let value = "x".repeat(64);
    let writes: u64 = 200;
    let mut peak_file_size = 0u64;
    let mut worst_write = Duration::ZERO;

    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        for _ in 0..writes {
            let started = Instant::now();
            db.put_string("", "hot", &value).unwrap();
            worst_write = worst_write.max(started.elapsed());
            peak_file_size = peak_file_size.max(fs::metadata(&db_path).unwrap().len());
        }
        db.close().unwrap();
    }

    // 200 writes of a 64-byte value append far more than 8x the threshold; if
    // compaction never runs the file keeps growing instead.
    assert!(
        peak_file_size <= 8 * TINY_THRESHOLD,
        "file grew to {peak_file_size} bytes: compaction never reclaimed space \
         (threshold {TINY_THRESHOLD})"
    );
    assert!(
        dir.path().join("t2.db.bak").exists(),
        "compaction never produced a `.bak` rotation"
    );
    assert!(
        !dir.path().join("t2.db.tmp").exists(),
        "compaction left a `.tmp` file behind"
    );
    // A nested/colliding lock acquisition would block for `lock_timeout_ms`
    // (10 s by default) on the writes that cross the threshold.
    assert!(
        worst_write < Duration::from_secs(2),
        "a single write took {worst_write:?}: compaction is blocking the write path"
    );
}

/// C4: damage to a record that is followed by intact records cannot be a torn
/// write on an append-only file, so opening must report corruption instead of
/// discarding everything after the damage.
#[test]
fn t4_mid_log_damage_must_be_reported_as_corruption() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t4.db");
    let opts = wal_options(u64::MAX - 1);
    let frame_len = probe_frame_len(dir.path());

    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        // Three same-sized frames, each written by its own call.
        db.put_string("", "a", "1").unwrap();
        db.put_string("", "b", "2").unwrap();
        db.put_string("", "c", "3").unwrap();
        db.close().unwrap();
    }

    // Flip one byte inside the *second* frame; the third frame stays intact.
    flip_byte(&db_path, FILE_HEADER_LEN + frame_len + 8);

    let err = Db::open(&db_path, Some(opts.clone())).err().expect(
        "damage followed by intact records must fail the open, not silently truncate the log",
    );
    assert!(
        matches!(err, DbError::Corrupted(_)),
        "expected CorruptedError, got {err:?}"
    );

    // The damaged file must not have been modified by the failed open attempt.
    assert_eq!(
        fs::metadata(&db_path).unwrap().len(),
        FILE_HEADER_LEN + 3 * frame_len,
        "a refused open must leave the log untouched for offline recovery"
    );
}

/// C4 negative control: a genuine torn write (the file simply ends inside the
/// last frame) must keep healing, otherwise the fix for `t4` would turn every
/// crash into a hard failure.
#[test]
fn t5_torn_tail_must_still_heal() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t5.db");
    let opts = wal_options(u64::MAX - 1);
    let frame_len = probe_frame_len(dir.path());

    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        db.put_string("", "a", "1").unwrap();
        db.put_string("", "b", "2").unwrap();
        db.put_string("", "c", "3").unwrap();
        db.close().unwrap();
    }

    // Simulate a crash in the middle of writing the third frame by cutting the
    // file short: fewer bytes remain than the minimum size of a complete frame.
    let healthy_len = fs::metadata(&db_path).unwrap().len();
    {
        let file = OpenOptions::new().write(true).open(&db_path).unwrap();
        file.set_len(healthy_len - frame_len / 2).unwrap();
        file.sync_all().unwrap();
    }

    let db = Db::open(&db_path, Some(opts)).expect("a torn tail must self-heal");
    assert_eq!(db.get_string("", "a").unwrap(), Some("1".to_string()));
    assert_eq!(db.get_string("", "b").unwrap(), Some("2".to_string()));
    assert_eq!(
        db.get_string("", "c").unwrap(),
        None,
        "the torn frame must be discarded as a whole"
    );
    assert_eq!(
        fs::metadata(&db_path).unwrap().len(),
        FILE_HEADER_LEN + 2 * frame_len,
        "the log must be physically healed back to the last complete frame"
    );
}
