use ckv::Db;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::Duration;
use tempfile::tempdir;

#[test]
fn test_multithreaded_concurrent_read_write_stress() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("concurrency_stress.db");
    let db = Db::open(&db_path, None).unwrap();

    // Pre-populate some keys
    for i in 0..100 {
        db.put_string("", format!("key_{:04}", i), format!("val_{:04}", i)).unwrap();
    }

    let running = Arc::new(AtomicBool::new(true));
    let read_ops = Arc::new(AtomicUsize::new(0));
    let write_ops = Arc::new(AtomicUsize::new(0));

    let mut handles = Vec::new();

    // Spawn 16 reader threads
    for t_id in 0..16 {
        let db_clone = db.clone();
        let running_clone = running.clone();
        let read_ops_clone = read_ops.clone();

        handles.push(thread::spawn(move || {
            let mut i = 0;
            while running_clone.load(Ordering::Relaxed) {
                let k = format!("key_{:04}", (t_id * 10 + i) % 100);
                if let Ok(Some(v)) = db_clone.get_string("", &k) {
                    assert!(v.starts_with("val_"));
                }
                read_ops_clone.fetch_add(1, Ordering::Relaxed);
                i += 1;
            }
        }));
    }

    // Spawn 4 writer threads
    for t_id in 0..4 {
        let db_clone = db.clone();
        let running_clone = running.clone();
        let write_ops_clone = write_ops.clone();

        handles.push(thread::spawn(move || {
            let mut i = 0;
            while running_clone.load(Ordering::Relaxed) {
                let k = format!("key_{:04}", (t_id * 25 + i) % 100);
                let v = format!("val_{:04}_t{}", i, t_id);
                let _ = db_clone.put_string("", &k, &v);
                write_ops_clone.fetch_add(1, Ordering::Relaxed);
                i += 1;
                thread::sleep(Duration::from_millis(2));
            }
        }));
    }

    // Let stress test run for 1 second
    thread::sleep(Duration::from_millis(1000));
    running.store(false, Ordering::SeqCst);

    for h in handles {
        h.join().unwrap();
    }

    let total_reads = read_ops.load(Ordering::SeqCst);
    let total_writes = write_ops.load(Ordering::SeqCst);
    println!("Concurrency stress finished: {} reads, {} writes", total_reads, total_writes);

    assert!(total_reads > 1000);
    assert!(total_writes > 20);

    // Final sanity check
    assert!(db.get_string("", "key_0000").unwrap().is_some());
}
