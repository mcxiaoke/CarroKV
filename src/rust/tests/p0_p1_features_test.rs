use carro_kv::{Db, KeyChangeEvent, KeyChangeOp};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use tempfile::tempdir;

#[test]
fn test_p0_p1_apis_and_watchers() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("p0_p1.db");
    let db = Db::open(&db_path, None).unwrap();

    let put_count = Arc::new(AtomicUsize::new(0));
    let del_count = Arc::new(AtomicUsize::new(0));
    let drop_count = Arc::new(AtomicUsize::new(0));
    let clear_count = Arc::new(AtomicUsize::new(0));

    let pc = put_count.clone();
    let dc = del_count.clone();
    let drc = drop_count.clone();
    let cc = clear_count.clone();

    // 注册 Watcher
    db.watch(move |ev: &KeyChangeEvent| match ev.op {
        KeyChangeOp::Put => {
            pc.fetch_add(1, Ordering::SeqCst);
        }
        KeyChangeOp::Delete => {
            dc.fetch_add(1, Ordering::SeqCst);
        }
        KeyChangeOp::DropNamespace => {
            drc.fetch_add(1, Ordering::SeqCst);
        }
        KeyChangeOp::Clear => {
            cc.fetch_add(1, Ordering::SeqCst);
        }
    });

    // 1. contains_key & count
    assert!(!db.contains_key("users", "user1").unwrap());
    assert_eq!(db.count("users").unwrap(), 0);

    db.put_string("users", "user1", "Alice").unwrap();
    assert!(db.contains_key("users", "user1").unwrap());
    assert_eq!(db.count("users").unwrap(), 1);
    assert_eq!(put_count.load(Ordering::SeqCst), 1);

    // 2. increment_i64
    let val1 = db.increment_i64("counters", "page_views", 10).unwrap();
    assert_eq!(val1, 10);
    assert_eq!(db.get_i64("counters", "page_views").unwrap(), Some(10));
    assert_eq!(put_count.load(Ordering::SeqCst), 2);

    let val2 = db.increment_i64("counters", "page_views", 5).unwrap();
    assert_eq!(val2, 15);
    assert_eq!(db.get_i64("counters", "page_views").unwrap(), Some(15));
    assert_eq!(put_count.load(Ordering::SeqCst), 3);

    // 3. increment 类型错误检查
    db.put_string("counters", "not_an_int", "hello").unwrap();
    assert!(db.increment_i64("counters", "not_an_int", 1).is_err());

    // 4. clear_namespace (保留命名空间但清空条目)
    db.put_string("temp_ns", "k1", "v1").unwrap();
    db.put_string("temp_ns", "k2", "v2").unwrap();
    assert_eq!(db.count("temp_ns").unwrap(), 2);

    // 规范 §5.2.3：clear_namespace 发出单个 Clear(4) 事件（Key 为空串），而非逐键 Delete
    assert!(db.clear_namespace("temp_ns").unwrap());
    assert_eq!(db.count("temp_ns").unwrap(), 0);
    assert!(!db.contains_key("temp_ns", "k1").unwrap());
    assert_eq!(clear_count.load(Ordering::SeqCst), 1);
    assert_eq!(del_count.load(Ordering::SeqCst), 0); // 不再逐键发 Delete
                                                     // 重复 clear 空命名空间返回 false
    assert!(!db.clear_namespace("temp_ns").unwrap());

    // 4b. drop_namespace 发出单个 DropNamespace(3) 事件并返回 bool
    db.put_string("drop_ns", "d1", "v1").unwrap();
    assert!(db.drop_namespace("drop_ns").unwrap());
    assert_eq!(drop_count.load(Ordering::SeqCst), 1);
    assert!(!db.drop_namespace("drop_ns").unwrap()); // 不存在返回 false

    // 4c. delete 返回 bool（是否存在并删除）
    db.put_string("users", "u9", "v9").unwrap();
    assert!(db.delete("users", "u9").unwrap());
    assert!(!db.delete("users", "u9").unwrap());
    assert_eq!(del_count.load(Ordering::SeqCst), 1);

    // 5. batch watcher trigger
    let mut batch = db.new_batch();
    batch.put_string("users", "user2", "Bob").unwrap();
    batch.delete("users", "user1").unwrap();
    batch.clear_namespace("temp_ns").unwrap();
    batch.drop_namespace("drop_ns").unwrap();
    db.write_batch(batch).unwrap();

    // 验证 batch 触发了 1 put 和 1 del
    assert_eq!(db.count("users").unwrap(), 1);
    assert!(!db.contains_key("users", "user1").unwrap());
    assert!(db.contains_key("users", "user2").unwrap());
    assert_eq!(del_count.load(Ordering::SeqCst), 2);
    // B3 修复：temp_ns 在前面已被 clear（空命名空间）、drop_ns 已被 drop（已不存在），
    // batch 中的这两步均为 no-op，严禁派发幻影 Clear / DropNamespace 事件。
    assert_eq!(clear_count.load(Ordering::SeqCst), 1);
    assert_eq!(drop_count.load(Ordering::SeqCst), 1);

    // 5b. batch 对真实产生影响的命名空间操作正常派发
    db.put_string("batch_clear_ns", "k", "v").unwrap();
    db.put_string("batch_drop_ns", "k", "v").unwrap();
    let mut batch2 = db.new_batch();
    batch2.clear_namespace("batch_clear_ns").unwrap();
    batch2.drop_namespace("batch_drop_ns").unwrap();
    db.write_batch(batch2).unwrap();
    assert_eq!(clear_count.load(Ordering::SeqCst), 2);
    assert_eq!(drop_count.load(Ordering::SeqCst), 2);
}

#[test]
fn test_static_backup_file() {
    let dir = tempdir().unwrap();
    let src_path = dir.path().join("source.db");
    let dest_path = dir.path().join("dest.db");

    // 1. 创建源数据库并写入数据
    {
        let db = Db::open(&src_path, None).unwrap();
        db.put_string("backup_ns", "key1", "val1").unwrap();
        db.put_i64("backup_ns", "num", 42).unwrap();
        db.close().unwrap();
    }

    // 2. 调用静态备份接口
    Db::backup_file(&src_path, &dest_path, None::<carro_kv::DbOptions>).unwrap();

    // 3. 打开备份出来的数据库并验证数据
    {
        let backup_db = Db::open(&dest_path, None).unwrap();
        assert_eq!(
            backup_db.get_string("backup_ns", "key1").unwrap(),
            Some("val1".to_string())
        );
        assert_eq!(backup_db.get_i64("backup_ns", "num").unwrap(), Some(42));
        assert_eq!(backup_db.count("backup_ns").unwrap(), 2);
        backup_db.close().unwrap();
    }
}
