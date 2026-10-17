//! FIX-01 regression: the background flush and the explicit flush paths must not
//! write the shared `<db>.tmp` at the same time, and a background snapshot that
//! becomes stale must never be rotated over newer data.
//!
//! Both variants of the bug are exercised through their observable consequence:
//! a confirmed write that disappears after `close()` + reopen. A large value is
//! used to widen the serialisation window so the interleaving happens on a normal
//! machine without any test-only hooks.

use ckv::{Db, DbOptions, SyncMode};
use std::sync::Arc;
use std::thread;
use std::time::Duration;
use tempfile::tempdir;

fn deferred(interval_ms: u32) -> DbOptions {
    DbOptions::default()
        .sync_mode(SyncMode::Deferred)
        .sync_interval_ms(interval_ms)
}

#[test]
fn explicit_flush_during_background_flush_keeps_both_generations() {
    for round in 0..5 {
        let dir = tempdir().unwrap();
        let path = dir.path().join(format!("race{round}.db"));

        let db = Db::open(&path, Some(deferred(1))).unwrap();

        // A big live snapshot makes the background serialisation take tens of
        // milliseconds, which is the window we need.
        let big = "x".repeat(15 * 1024 * 1024);
        db.put_string("", "big", &big).unwrap();

        // Let the worker clone the snapshot and start serialising it.
        thread::sleep(Duration::from_millis(5));

        // Drop the big value so the explicit flush below writes a *tiny* snapshot:
        // it finishes long before the worker's 15 MB write. Without the generation
        // check the worker then rotates its stale snapshot over the flushed one and
        // the confirmed write disappears.
        db.delete("", "big").unwrap();
        db.put_string("", "w", "after").unwrap();
        db.flush().unwrap();

        // Give a stale worker all the time it needs to rotate.
        thread::sleep(Duration::from_millis(150));
        db.close().unwrap();

        let reopened = Db::open(&path, None).unwrap();
        assert_eq!(
            reopened.get_string("", "w").unwrap(),
            Some("after".to_string()),
            "round {round}: an explicitly flushed write was overwritten by a stale \
             background snapshot"
        );
        assert_eq!(
            reopened.get_string("", "big").unwrap(),
            None,
            "round {round}: the deleted value came back from a stale snapshot"
        );
        reopened.close().unwrap();
    }
}

#[test]
fn rapid_writes_with_background_flush_are_not_silently_dropped() {
    for round in 0..3 {
        let dir = tempdir().unwrap();
        let path = dir.path().join(format!("rapid{round}.db"));

        let db = Db::open(&path, Some(deferred(1))).unwrap();
        for i in 0..200 {
            db.put_string("ns", format!("k{i:04}"), format!("value-{i}"))
                .unwrap();
        }
        db.close().unwrap();

        let reopened = Db::open(&path, None).unwrap();
        let count = reopened.count("ns").unwrap();
        assert_eq!(count, 200, "round {round}: lost writes after close()");
        for i in [0usize, 87, 199] {
            assert_eq!(
                reopened
                    .get_string("ns", format!("k{i:04}"))
                    .unwrap()
                    .as_deref(),
                Some(format!("value-{i}").as_str()),
                "round {round}: key k{i:04} missing"
            );
        }
        reopened.close().unwrap();
    }
}

/// Writers + an explicit flush loop + the background worker: no deadlock (the test
/// completing is the assertion) and no lost writes.
#[test]
fn concurrent_writers_flushers_and_worker_do_not_deadlock_or_lose_data() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("concurrent.db");
    let db = Arc::new(Db::open(&path, Some(deferred(2))).unwrap());

    let mut handles = Vec::new();
    for t in 0..3 {
        let db = Arc::clone(&db);
        handles.push(thread::spawn(move || {
            for i in 0..100 {
                db.put_string("ns", format!("t{t}-k{i:04}"), "v").unwrap();
            }
        }));
    }
    {
        let db = Arc::clone(&db);
        handles.push(thread::spawn(move || {
            for _ in 0..30 {
                db.flush().unwrap();
                thread::sleep(Duration::from_millis(1));
            }
        }));
    }
    for h in handles {
        h.join().unwrap();
    }

    Arc::try_unwrap(db)
        .map(|db| db.close().unwrap())
        .unwrap_or_else(|arc| arc.close().unwrap());

    let reopened = Db::open(&path, None).unwrap();
    assert_eq!(reopened.count("ns").unwrap(), 300, "writes were lost");
    reopened.close().unwrap();
}
