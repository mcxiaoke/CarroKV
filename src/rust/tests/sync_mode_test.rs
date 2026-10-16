use ckv::{Db, DbOptions, SyncMode};
use std::fs;
use std::path::{Path, PathBuf};
use std::thread;
use std::time::Duration;
use tempfile::tempdir;

fn temp_db(dir: &Path, name: &str) -> PathBuf {
    dir.join(name)
}

#[test]
fn test_sync_mode_manual_flush_and_close() {
    let dir = tempdir().unwrap();
    let p = temp_db(dir.path(), "manual.db");

    // 1. 打开 Manual 模式
    {
        let opts = DbOptions::default().sync_mode(SyncMode::Manual);
        let db = Db::open(&p, Some(opts)).unwrap();

        // 写入数据
        db.put_string("", "k1", "v1").unwrap();
        db.put_string("", "k2", "v2").unwrap();

        // 内存中立即可读
        assert_eq!(db.get_string("", "k1").unwrap(), Some("v1".to_string()));
        assert_eq!(db.get_string("", "k2").unwrap(), Some("v2".to_string()));

        // 显式 flush
        db.flush().unwrap();
        assert!(p.exists());
    }

    // 2. 重新打开验证持久化
    {
        let db = Db::open(&p, None).unwrap();
        assert_eq!(db.get_string("", "k1").unwrap(), Some("v1".to_string()));
        assert_eq!(db.get_string("", "k2").unwrap(), Some("v2".to_string()));
    }

    // 3. 测试无需显式 flush，close / drop 自动刷盘
    let p2 = temp_db(dir.path(), "manual_close.db");
    {
        let opts = DbOptions::default().sync_mode(SyncMode::Manual);
        let db = Db::open(&p2, Some(opts)).unwrap();
        db.put_string("", "k_auto", "v_auto").unwrap();
        db.close().unwrap();
    }
    {
        let db = Db::open(&p2, None).unwrap();
        assert_eq!(db.get_string("", "k_auto").unwrap(), Some("v_auto".to_string()));
    }
}

#[test]
fn test_sync_mode_deferred_background_flush() {
    let dir = tempdir().unwrap();
    let p = temp_db(dir.path(), "deferred.db");

    {
        // 50ms 刷新周期
        let opts = DbOptions::default()
            .sync_mode(SyncMode::Deferred)
            .sync_interval_ms(50);
        let db = Db::open(&p, Some(opts)).unwrap();

        db.put_string("", "key_async", "val_async").unwrap();
        assert_eq!(db.get_string("", "key_async").unwrap(), Some("val_async".to_string()));

        // 等待后台定时器刷盘 (至少 100ms)
        thread::sleep(Duration::from_millis(150));

        // 此时磁盘文件应该已生成
        assert!(p.exists());
    }

    // 验证新实例读取
    {
        let db = Db::open(&p, None).unwrap();
        assert_eq!(db.get_string("", "key_async").unwrap(), Some("val_async".to_string()));
    }
}

#[test]
fn test_sync_mode_high_frequency_write_coalescing() {
    let dir = tempdir().unwrap();
    let p = temp_db(dir.path(), "coalesce.db");

    let opts = DbOptions::default().sync_mode(SyncMode::Manual);
    let db = Db::open(&p, Some(opts)).unwrap();

    // 快速写入 1000 条，不落盘
    for i in 0..1000 {
        db.put_i64("", format!("num_{}", i), i).unwrap();
    }

    assert_eq!(db.count("").unwrap(), 1000);

    // 单次 flush 统一落盘
    db.flush().unwrap();

    let meta = fs::metadata(&p).unwrap();
    assert!(meta.len() > 32);

    drop(db);

    let db2 = Db::open(&p, None).unwrap();
    assert_eq!(db2.count("").unwrap(), 1000);
    assert_eq!(db2.get_i64("", "num_999").unwrap(), Some(999));
}
