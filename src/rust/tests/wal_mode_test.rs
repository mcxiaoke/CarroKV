use ckv::{Db, DbOptions, StorageEngineMode, SyncMode};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

#[test]
fn test_wal_append_and_persistence() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("wal_test.db");

    let opts = DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Immediate);

    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        db.put_string("", "key1", "val1").unwrap();
        db.put_string("", "key2", "val2").unwrap();
        db.put_string("", "key1", "val1_updated").unwrap();
        db.delete("", "key2").unwrap();
        db.close().unwrap();
    }

    // Reopen and verify final state
    {
        let db = Db::open(&db_path, Some(opts)).unwrap();
        assert_eq!(db.get_string("", "key1").unwrap(), Some("val1_updated".to_string()));
        assert_eq!(db.get_string("", "key2").unwrap(), None);
    }
}

#[test]
fn test_wal_tail_truncation_recovery() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("wal_trunc_test.db");

    let warning_triggered = Arc::new(AtomicBool::new(false));
    let warn_flag = warning_triggered.clone();

    let opts = DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Immediate)
        .on_warning(move |code, _msg| {
            if code == "onTailTruncated" {
                warn_flag.store(true, Ordering::Release);
            }
        });

    // Step 1: Write valid entries
    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        db.put_string("", "healthy_k1", "healthy_v1").unwrap();
        db.put_string("", "healthy_k2", "healthy_v2").unwrap();
        db.close().unwrap();
    }

    let healthy_len = fs::metadata(&db_path).unwrap().len();
    assert!(healthy_len > 32);

    // Step 2: Inject corrupt partial trailing bytes (simulating power failure during write)
    {
        let mut f = OpenOptions::new().append(true).open(&db_path).unwrap();
        // Append 15 bytes of garbage (incomplete frame header)
        f.write_all(b"CORRUPT_TRAIL!!").unwrap();
        f.flush().unwrap();
    }

    let corrupted_len = fs::metadata(&db_path).unwrap().len();
    assert_eq!(corrupted_len, healthy_len + 15);

    // Step 3: Reopen database - should auto-truncate the corrupted tail and heal
    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        assert!(warning_triggered.load(Ordering::Acquire));
        assert_eq!(db.get_string("", "healthy_k1").unwrap(), Some("healthy_v1".to_string()));
        assert_eq!(db.get_string("", "healthy_k2").unwrap(), Some("healthy_v2".to_string()));

        // Verify the file was physically truncated back to healthy size
        let healed_len = fs::metadata(&db_path).unwrap().len();
        assert_eq!(healed_len, healthy_len);

        // Verify we can continue writing new entries after truncation
        db.put_string("", "healthy_k3", "healthy_v3").unwrap();
        assert_eq!(db.get_string("", "healthy_k3").unwrap(), Some("healthy_v3".to_string()));
    }
}

#[test]
fn test_wal_compaction_and_bak_rotation() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("wal_compact_test.db");

    let opts = DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Immediate);

    let db = Db::open(&db_path, Some(opts.clone())).unwrap();

    // Repeated overwrites generate append frames
    for i in 0..100 {
        db.put_string("", "heavy_key", &format!("value_generation_{}", i)).unwrap();
    }

    let bloated_size = fs::metadata(&db_path).unwrap().len();
    assert!(bloated_size > 3000);

    // Compact
    db.compact().unwrap();

    let compacted_size = fs::metadata(&db_path).unwrap().len();
    assert!(compacted_size < bloated_size);

    // .bak should exist and equal bloated_size
    let bak_path = dir.path().join("wal_compact_test.db.bak");
    assert!(bak_path.exists());
    assert_eq!(fs::metadata(&bak_path).unwrap().len(), bloated_size);

    // Verify data integrity after compaction
    assert_eq!(
        db.get_string("", "heavy_key").unwrap(),
        Some("value_generation_99".to_string())
    );
}
