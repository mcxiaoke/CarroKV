//! C3: `batch { Put(ns, k); ClearNamespace(ns); }` used to resurrect `k` after a
//! restart.
//!
//! When the batch builds its log entries, `ClearNamespace` was expanded from the
//! in-memory state *as it was before the batch ran*. A key written by a `Put`
//! earlier in the same batch is not in that state yet, so no delete was emitted
//! for it while its `OP_PUT` was already in the log. The in-memory state was then
//! cleared for real, so the divergence only showed up when the log was replayed
//! -- i.e. after a reopen, which is exactly what the existing batch tests never
//! did.
//!
//! The mirrored scenario runs in both engine modes, because the two modes must
//! agree on one write sequence and only the append-only log can diverge here.

use ckv::{Db, DbOptions, StorageEngineMode, SyncMode};
use tempfile::tempdir;

fn options(mode: StorageEngineMode) -> DbOptions {
    DbOptions::default()
        .engine_mode(mode)
        .sync_mode(SyncMode::Immediate)
}

/// `Put` then `ClearNamespace` in one batch: the key must be gone in memory *and*
/// gone after replaying the log from disk.
fn assert_put_then_clear_does_not_resurrect(mode: StorageEngineMode) {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("c3_put_clear.db");
    let opts = options(mode);

    // Pre-existing content, including a key that the clear must delete.
    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        db.put_string("ns", "old", "old_value").unwrap();
        db.put_string("other", "keep", "keep_value").unwrap();
        db.close().unwrap();
    }

    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        db.batch(|b| {
            b.put_string("ns", "fresh", "fresh_value")?;
            b.clear_namespace("ns")?;
            // A different namespace must be unaffected by the clear.
            b.put_string("other", "keep2", "keep2_value")?;
            Ok(())
        })
        .unwrap();

        assert_eq!(
            db.get_string("ns", "fresh").unwrap(),
            None,
            "memory: the key written before the clear must be gone"
        );
        assert_eq!(db.get_string("ns", "old").unwrap(), None);
        db.close().unwrap();
    }

    // Replay from disk: this is where the key used to come back to life.
    let db = Db::open(&db_path, Some(opts)).unwrap();
    assert_eq!(
        db.get_string("ns", "fresh").unwrap(),
        None,
        "replay: 'fresh' was resurrected (C3)"
    );
    assert_eq!(db.get_string("ns", "old").unwrap(), None);
    assert_eq!(
        db.get_string("other", "keep").unwrap(),
        Some("keep_value".to_string()),
        "the clear must not touch other namespaces"
    );
    assert_eq!(
        db.get_string("other", "keep2").unwrap(),
        Some("keep2_value".to_string())
    );
}

#[test]
fn t3_batch_put_then_clear_must_not_resurrect_in_wal_mode() {
    assert_put_then_clear_does_not_resurrect(StorageEngineMode::AppendOnlyLog);
}

#[test]
fn t3_batch_put_then_clear_must_not_resurrect_in_snapshot_mode() {
    assert_put_then_clear_does_not_resurrect(StorageEngineMode::SnapshotAtomic);
}

/// The mirror image: a `Put` issued *after* the clear must survive, otherwise the
/// fix for the case above would be dropping live writes instead of shadowed ones.
fn assert_clear_then_put_survives(mode: StorageEngineMode) {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("c3_clear_put.db");
    let opts = options(mode);

    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        db.put_string("ns", "old", "old_value").unwrap();
        db.close().unwrap();
    }

    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        db.batch(|b| {
            b.clear_namespace("ns")?;
            b.put_string("ns", "fresh", "fresh_value")?;
            Ok(())
        })
        .unwrap();
        db.close().unwrap();
    }

    let db = Db::open(&db_path, Some(opts)).unwrap();
    assert_eq!(db.get_string("ns", "old").unwrap(), None);
    assert_eq!(
        db.get_string("ns", "fresh").unwrap(),
        Some("fresh_value".to_string()),
        "a put issued after the clear must survive the replay"
    );
}

#[test]
fn t3_batch_clear_then_put_must_survive_replay_in_wal_mode() {
    assert_clear_then_put_survives(StorageEngineMode::AppendOnlyLog);
}

#[test]
fn t3_batch_clear_then_put_must_survive_replay_in_snapshot_mode() {
    assert_clear_then_put_survives(StorageEngineMode::SnapshotAtomic);
}
