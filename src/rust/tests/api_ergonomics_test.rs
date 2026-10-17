use carro_kv::Db;
use tempfile::tempdir;

#[test]
fn test_fluent_batch_and_iterators() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("ergonomics.db");
    let db = Db::open(&db_path, None).unwrap();

    // 1. Fluent Batch API
    let mut batch = db.new_batch();
    batch
        .put_string("users", "user:1", "Alice")
        .unwrap()
        .put_string("users", "user:2", "Bob")
        .unwrap()
        .put_string("users", "user:3", "Charlie")
        .unwrap()
        .put_string("orders", "order:101", "Book")
        .unwrap();

    db.write_batch(batch).unwrap();

    // 2. contains API
    assert!(db.contains("users", "user:1").unwrap());
    assert!(db.contains("users", "user:2").unwrap());
    assert!(!db.contains("users", "user:999").unwrap());

    // 3. Streaming Iterator API (iter_prefix)
    let mut iter = db.iter_prefix("users", Some("user:")).unwrap();
    assert_eq!(iter.next().unwrap().key, "user:1");
    assert_eq!(iter.next().unwrap().key, "user:2");
    assert_eq!(iter.next().unwrap().key, "user:3");
    assert!(iter.next().is_none());

    // 4. DoubleEndedIterator (reverse iteration)
    let mut rev_iter = db.iter_prefix("users", Some("user:")).unwrap();
    assert_eq!(rev_iter.next_back().unwrap().key, "user:3");
    assert_eq!(rev_iter.next_back().unwrap().key, "user:2");
    assert_eq!(rev_iter.next_back().unwrap().key, "user:1");
    assert!(rev_iter.next_back().is_none());
}
