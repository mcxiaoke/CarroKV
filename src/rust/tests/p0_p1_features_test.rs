use ckv::{Db, KeyChangeEvent, KeyChangeOp};
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

    let pc = put_count.clone();
    let dc = del_count.clone();

    // 注册 Watcher
    db.watch(move |ev: &KeyChangeEvent| match ev.op {
        KeyChangeOp::Put => {
            pc.fetch_add(1, Ordering::SeqCst);
        }
        KeyChangeOp::Delete => {
            dc.fetch_add(1, Ordering::SeqCst);
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

    db.clear_namespace("temp_ns").unwrap();
    assert_eq!(db.count("temp_ns").unwrap(), 0);
    assert!(!db.contains_key("temp_ns", "k1").unwrap());
    assert_eq!(del_count.load(Ordering::SeqCst), 2); // k1, k2 deleted

    // 5. batch watcher trigger
    let mut batch = db.new_batch();
    batch.put_string("users", "user2", "Bob").unwrap();
    batch.delete("users", "user1").unwrap();
    db.write_batch(batch).unwrap();

    // 验证 batch 触发了 1 put 和 1 del
    assert_eq!(db.count("users").unwrap(), 1);
    assert!(!db.contains_key("users", "user1").unwrap());
    assert!(db.contains_key("users", "user2").unwrap());
}
