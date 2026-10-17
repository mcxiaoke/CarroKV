use ckv::{Db, DbError};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread;

#[test]
fn test_snapshot_point_in_time_isolation() -> Result<(), Box<dyn std::error::Error>> {
    let dir = tempfile::tempdir()?;
    let db_path = dir.path().join("snap_iso.db");
    let db = Db::open(&db_path, None)?;

    // 1. Initial dataset
    db.put_string("users", "user:1", "Alice")?;
    db.put_i64("users", "user:1:score", 100)?;
    db.put_string("users", "user:2", "Bob")?;
    db.put_string("orders", "order:100", "Pending")?;

    assert_eq!(db.count("users")?, 3);
    assert_eq!(db.count("orders")?, 1);

    // 2. Create snapshot 1
    let snap1 = db.snapshot();
    let snap1_tx = snap1.tx_id();
    assert!(snap1_tx > 0);

    // 3. Mutate primary database extensively
    // A: Overwrite existing key
    db.put_string("users", "user:1", "Alice_Updated")?;
    // B: Mutate score
    db.put_i64("users", "user:1:score", 999)?;
    // C: Delete key
    db.delete("users", "user:2")?;
    // D: Add new key
    db.put_string("users", "user:3", "Charlie")?;
    // E: Drop namespace orders
    db.drop_namespace("orders")?;

    // Primary db state
    assert_eq!(
        db.get_string("users", "user:1")?,
        Some("Alice_Updated".to_string())
    );
    assert_eq!(db.get_i64("users", "user:1:score")?, Some(999));
    assert_eq!(db.get_string("users", "user:2")?, None);
    assert_eq!(
        db.get_string("users", "user:3")?,
        Some("Charlie".to_string())
    );
    assert_eq!(db.count("orders")?, 0);

    // 4. Verify snapshot 1 remains completely frozen in time
    assert_eq!(
        snap1.get_string("users", "user:1")?,
        Some("Alice".to_string())
    );
    assert_eq!(snap1.get_i64("users", "user:1:score")?, Some(100));
    assert_eq!(
        snap1.get_string("users", "user:2")?,
        Some("Bob".to_string())
    );
    assert_eq!(snap1.get_string("users", "user:3")?, None);
    assert!(snap1.contains_key("users", "user:2")?);
    assert!(!snap1.contains_key("users", "user:3")?);
    assert_eq!(snap1.count("users")?, 3);

    // Namespace orders was dropped in primary, but must exist in snap1
    assert_eq!(snap1.count("orders")?, 1);
    assert_eq!(
        snap1.get_string("orders", "order:100")?,
        Some("Pending".to_string())
    );

    // 5. Create snapshot 2 and verify it sees latest mutations
    let snap2 = db.snapshot();
    assert!(snap2.tx_id() > snap1.tx_id());
    assert_eq!(
        snap2.get_string("users", "user:1")?,
        Some("Alice_Updated".to_string())
    );
    assert_eq!(
        snap2.get_string("users", "user:3")?,
        Some("Charlie".to_string())
    );
    assert_eq!(snap2.count("orders")?, 0);

    Ok(())
}

#[test]
fn test_snapshot_concurrent_readers_and_writers() -> Result<(), Box<dyn std::error::Error>> {
    let dir = tempfile::tempdir()?;
    let db_path = dir.path().join("snap_concur.db");
    let db = Db::open(&db_path, None)?;

    for i in 0..100 {
        db.put_string("data", format!("key:{:04}", i), format!("val:{:04}", i))?;
    }

    let snap = db.snapshot();
    assert_eq!(snap.count("data")?, 100);

    let stop_flag = Arc::new(AtomicBool::new(false));
    let stop_flag_clone = stop_flag.clone();
    let db_clone = db.clone();

    // Background thread continuously writes and mutates data
    let writer_handle = thread::spawn(move || {
        let mut counter = 0;
        while !stop_flag_clone.load(Ordering::Relaxed) {
            let k = format!("key:{:04}", counter % 100);
            let _ = db_clone.put_string("data", &k, format!("mutated:{}", counter));
            counter += 1;
            thread::yield_now();
        }
    });

    // Reader thread repeatedly scans range on snapshot
    for _ in 0..50 {
        let range = snap.scan_range("data", Some("key:0010"), Some("key:0020"))?;
        assert_eq!(range.len(), 10);
        for (i, entry) in range.iter().enumerate() {
            let expected_key = format!("key:{:04}", 10 + i);
            let expected_val = format!("val:{:04}", 10 + i);
            assert_eq!(entry.key, expected_key);
            assert_eq!(entry.as_string()?, expected_val);
        }
    }

    stop_flag.store(true, Ordering::Relaxed);
    writer_handle.join().unwrap();

    // Snapshot remains untouched after writer finishes
    assert_eq!(snap.count("data")?, 100);
    assert_eq!(
        snap.get_string("data", "key:0000")?,
        Some("val:0000".to_string())
    );

    Ok(())
}

#[test]
fn test_snapshot_type_safety_and_all_types() -> Result<(), Box<dyn std::error::Error>> {
    let dir = tempfile::tempdir()?;
    let db_path = dir.path().join("snap_types.db");
    let db = Db::open(&db_path, None)?;

    db.put_bytes("ns", "k_bytes", vec![1, 2, 3, 4])?;
    db.put_string("ns", "k_str", "hello")?;
    db.put_i64("ns", "k_i64", -42)?;
    db.put_f64("ns", "k_f64", std::f32::consts::PI as f64)?;
    db.put_bool("ns", "k_bool", true)?;

    let snap = db.snapshot();

    assert_eq!(snap.get_bytes("ns", "k_bytes")?, Some(vec![1, 2, 3, 4]));
    assert_eq!(snap.get_string("ns", "k_str")?, Some("hello".to_string()));
    assert_eq!(snap.get_i64("ns", "k_i64")?, Some(-42));
    assert_eq!(
        snap.get_f64("ns", "k_f64")?,
        Some(std::f32::consts::PI as f64)
    );
    assert_eq!(snap.get_bool("ns", "k_bool")?, Some(true));

    // Type mismatch intercept
    match snap.get_string("ns", "k_i64") {
        Err(DbError::TypeMismatch(_)) => {}
        other => panic!("Expected TypeMismatch, got {:?}", other),
    }

    Ok(())
}
