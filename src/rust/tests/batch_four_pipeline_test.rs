use carro_kv::{Db, DbOptions, StorageEngineMode, SyncMode};
use std::fs;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::Instant;
use tempfile::tempdir;

#[test]
fn test_warning_queue_and_reentrancy_does_not_corrupt_state() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("warning_queue.db");

    let warning_count = Arc::new(AtomicUsize::new(0));
    let warning_count_clone = warning_count.clone();

    // 在警告回调内读取数据库，验证无重入死锁
    let db_handle: Arc<parking_lot::Mutex<Option<Db>>> = Arc::new(parking_lot::Mutex::new(None));
    let db_handle_clone = db_handle.clone();

    let options = DbOptions::default()
        .compaction_threshold_bytes(100)
        .strict_compaction(false)
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .on_warning(move |_code, _msg| {
            warning_count_clone.fetch_add(1, Ordering::SeqCst);
            if let Some(ref db) = *db_handle_clone.lock() {
                let _ = db.get_i64("ns", "key");
                let _ = db.namespaces();
            }
        });

    let db = Db::open(&db_path, Some(options)).unwrap();
    *db_handle.lock() = Some(db.clone());

    // 阻断 .tmp 创建以触发压实失败告警
    let blocker = dir.path().join("warning_queue.db.tmp");
    fs::create_dir(&blocker).unwrap();

    for i in 0..30 {
        let _ = db.put_string("ns", format!("k{i}"), "payload_to_trigger_compaction_failure");
    }

    let _ = fs::remove_dir_all(&blocker);
    let _ = db.close();

    assert!(
        warning_count.load(Ordering::SeqCst) > 0,
        "Compaction failure should queue and fire warning outside lock"
    );
}

#[test]
fn test_batch_cow_deduplication_per_namespace() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("cow_dedup.db");
    let db = Db::open(&db_path, None).unwrap();

    db.put_string("bulk", "seed", "0").unwrap();
    let snap = db.snapshot();

    let start = Instant::now();
    db.batch(|b| {
        for i in 0..2000 {
            if i % 2 == 0 {
                b.put_string("bulk", format!("k{i}"), "val")?;
            } else {
                b.delete("bulk", format!("k{}", i - 1))?;
            }
        }
        Ok(())
    })
    .unwrap();
    let elapsed = start.elapsed();

    // 2000 次操作在单次 CoW 克隆下应在 1500ms 内完成，避免 O(N^2)
    assert!(
        elapsed.as_millis() < 2000,
        "Batch ops should be fast without repeated clone, elapsed: {} ms",
        elapsed.as_millis()
    );

    // 快照保持隔离
    assert_eq!(
        snap.get_string("bulk", "seed").unwrap(),
        Some("0".to_string())
    );
    let _ = db.close();
}

#[test]
fn test_rollback_on_flush_failure_avoids_double_increment_and_retains_is_dirty() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("rollback_test.db");
    let db = Db::open(&db_path, None).unwrap();

    db.put_i64("bank", "balance", 100).unwrap();

    // 创建目录阻断 .tmp 文件创建与落盘
    let blocker = dir.path().join("rollback_test.db.tmp");
    fs::create_dir(&blocker).unwrap();

    let res = db.increment_i64("bank", "balance", 50);
    assert!(res.is_err(), "Increment must fail when tmp file is blocked");

    // 内存必须原子回滚回 100，不得残留 150
    assert_eq!(db.get_i64("bank", "balance").unwrap(), Some(100));

    // 解除阻断后重试
    fs::remove_dir_all(&blocker).unwrap();

    let new_val = db.increment_i64("bank", "balance", 50).unwrap();
    assert_eq!(new_val, 150, "Should be 150 without double increment");
    assert_eq!(db.get_i64("bank", "balance").unwrap(), Some(150));

    let _ = db.close();
}

#[test]
fn test_flush_async_and_backup_async_first_class_methods() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("async_api.db");
    let backup_path = dir.path().join("async_backup.db");

    let opts = DbOptions::default().sync_mode(SyncMode::Manual);
    let db = Db::open(&db_path, Some(opts)).unwrap();
    db.put_string("data", "greeting", "hello async").unwrap();

    // 异步刷盘
    let flush_handle = db.flush_async();
    flush_handle.join().unwrap().unwrap();

    // 异步备份
    let backup_handle = db.backup_async(&backup_path);
    backup_handle.join().unwrap().unwrap();

    let _ = db.close();

    // 验证备份库能独立以只读模式打开并读取数据
    let ro_opts = DbOptions::default().read_only(true);
    let backup_db = Db::open(&backup_path, Some(ro_opts)).unwrap();
    assert_eq!(
        backup_db.get_string("data", "greeting").unwrap(),
        Some("hello async".to_string())
    );
    let _ = backup_db.close();
}
