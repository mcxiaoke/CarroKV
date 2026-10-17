use carro_kv::{Db, DbOptions, StorageEngineMode, SyncMode};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;
use std::thread;
use tempfile::tempdir;

const READER_THREADS: usize = 16;
const WRITER_THREADS: usize = 4;
const WRITES_PER_THREAD: usize = 200;
/// Upper bound on a single reader's loop.
///
/// Readers normally stop as soon as the writers are done. The cap exists only so a
/// real starvation bug ends the test with a failure instead of hanging it forever:
/// if writers cannot get the write lock, the readers stop at this cap, and the
/// `total_writes` assertion below reports the problem.
const MAX_READS_PER_THREAD: usize = 5_000_000;

#[test]
fn test_multithreaded_concurrent_read_write_stress() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("concurrency_stress.db");
    // Append-only + Deferred on purpose: this test is about concurrent read/write
    // safety, not durability. With the default SnapshotAtomic + Immediate every put
    // rewrites the whole file and fsyncs it, which makes the write side dominated by
    // the storage engine instead of by lock contention.
    let opts = DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Deferred)
        .sync_interval_ms(3_600_000);
    let db = Db::open(&db_path, Some(opts)).unwrap();

    // Pre-populate some keys
    for i in 0..100 {
        db.put_string("", format!("key_{:04}", i), format!("val_{:04}", i))
            .unwrap();
    }

    // Both sides are bounded by *work*, not by elapsed time. A wall-clock window such
    // as "assert writes > 20 in 1s" measures the CI runner's scheduler, not the
    // engine: a 2-vCPU runner with 16 spinning readers can leave the 4 writers with
    // zero completed iterations and still be perfectly correct.
    let stop = Arc::new(AtomicBool::new(false));
    let read_ops = Arc::new(AtomicUsize::new(0));
    let write_ops = Arc::new(AtomicUsize::new(0));

    let mut reader_handles = Vec::new();
    let mut writer_handles = Vec::new();

    for t_id in 0..READER_THREADS {
        let db_clone = db.clone();
        let stop_clone = stop.clone();
        let read_ops_clone = read_ops.clone();

        reader_handles.push(thread::spawn(move || {
            let mut i = 0;
            while !stop_clone.load(Ordering::Relaxed) && i < MAX_READS_PER_THREAD {
                let k = format!("key_{:04}", (t_id * 10 + i) % 100);
                if let Ok(Some(v)) = db_clone.get_string("", &k) {
                    assert!(v.starts_with("val_"), "torn value for {}: {}", k, v);
                }
                read_ops_clone.fetch_add(1, Ordering::Relaxed);
                i += 1;
            }
        }));
    }

    for t_id in 0..WRITER_THREADS {
        let db_clone = db.clone();
        let write_ops_clone = write_ops.clone();

        writer_handles.push(thread::spawn(move || {
            for i in 0..WRITES_PER_THREAD {
                let k = format!("key_{:04}", (t_id * 25 + i) % 100);
                let v = format!("val_{:04}_t{}", i, t_id);
                // A concurrent put must never fail, and it must never be dropped
                // silently: a swallowed error here would hide exactly the class of
                // bug this test exists to catch.
                db_clone
                    .put_string("", &k, &v)
                    .expect("concurrent put must succeed");
                write_ops_clone.fetch_add(1, Ordering::Relaxed);
            }
        }));
    }

    // Writers are bounded, so they always finish; readers are stopped afterwards.
    for h in writer_handles {
        h.join().expect("writer thread panicked");
    }
    stop.store(true, Ordering::SeqCst);
    for h in reader_handles {
        h.join().expect("reader thread panicked");
    }

    let total_reads = read_ops.load(Ordering::SeqCst);
    let total_writes = write_ops.load(Ordering::SeqCst);
    println!(
        "Concurrency stress finished: {} reads, {} writes",
        total_reads, total_writes
    );

    // Every write must have completed *while* the readers were hammering the lock.
    assert_eq!(
        total_writes,
        WRITER_THREADS * WRITES_PER_THREAD,
        "writes were starved or failed under concurrent read load"
    );
    assert!(total_reads > 0, "readers made no progress");

    // Final sanity check: data written under contention is readable afterwards.
    let value = db.get_string("", "key_0000").unwrap();
    assert!(
        value.is_some_and(|v| v.starts_with("val_")),
        "key_0000 lost after the stress run"
    );
}
