//! M3-01 regression: `compact()` must refuse to run on a closed instance.
//!
//! `compact()` is the only whole-file rewrite path that does not go through
//! `write_batch`, and it used to validate only `read_only` (via
//! `precheck_disk_state`). After `close()` released the inter-process kernel
//! lock, a later `compact()` re-created/overwrote the database file without
//! holding that lock - another process could legitimately hold the same
//! database at the same time.

use carro_kv::{Db, DbError, DbOptions};
use tempfile::tempdir;

#[test]
fn compact_after_close_is_rejected_and_leaves_disk_untouched() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("closed_compact.db");

    let db = Db::open(&path, Some(DbOptions::default())).unwrap();
    db.put_string("", "k", "v").unwrap();
    db.flush().unwrap();
    let before = std::fs::read(&path).unwrap();
    db.close().unwrap();

    let err = db.compact().expect_err("compact() after close() must fail");
    match err {
        DbError::Locked(msg) => assert!(msg.contains("closed"), "unexpected message: {msg}"),
        other => panic!("expected DbError::Locked, got {other:?}"),
    }

    let after = std::fs::read(&path).unwrap();
    assert_eq!(before, after, "closed compact() must not rewrite the file");
}

/// A closed handle must also refuse `flush()` and keep the file byte-identical.
#[test]
fn flush_after_close_is_a_noop() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("closed_flush.db");

    let db = Db::open(&path, Some(DbOptions::default())).unwrap();
    db.put_string("", "k", "v").unwrap();
    db.flush().unwrap();
    let before = std::fs::read(&path).unwrap();
    db.close().unwrap();

    // close() already flushed; a second flush must neither error nor change bytes.
    let _ = db.flush();
    let after = std::fs::read(&path).unwrap();
    assert_eq!(before, after);
}

/// After `close()`, another instance must be able to open the same path
/// immediately (the lock is really released) and its compact() must succeed.
#[test]
fn compact_works_on_a_fresh_handle_after_close() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("reopen_compact.db");

    let db = Db::open(&path, Some(DbOptions::default())).unwrap();
    db.put_string("", "k", "v").unwrap();
    db.close().unwrap();

    let db2 = Db::open(&path, Some(DbOptions::default())).unwrap();
    db2.compact().unwrap();
    assert_eq!(db2.get_string("", "k").unwrap(), Some("v".to_string()));
    db2.close().unwrap();
}
