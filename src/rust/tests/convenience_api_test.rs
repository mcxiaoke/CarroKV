use carro_kv::Db;
use tempfile::tempdir;

#[test]
fn test_rust_namespace_scope_convenience() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("test_convenience.db");

    let db = Db::open(&db_path, None).unwrap();

    // 1. default_ns() 门面
    let def = db.default_ns();
    assert_eq!(def.namespace(), "");

    def.put_string("strKey", "strVal").unwrap();
    assert_eq!(def.get_string("strKey").unwrap().as_deref(), Some("strVal"));
    assert_eq!(
        db.get_string("", "strKey").unwrap().as_deref(),
        Some("strVal")
    );

    def.put_bytes("byteKey", b"byteVal").unwrap();
    assert_eq!(
        def.get_bytes("byteKey").unwrap().as_deref(),
        Some(&b"byteVal"[..])
    );

    def.put_i64("intKey", 42).unwrap();
    assert_eq!(def.get_i64("intKey").unwrap(), Some(42));
    assert_eq!(def.increment_i64("intKey", 8).unwrap(), 50);
    assert_eq!(def.get_i64("intKey").unwrap(), Some(50));

    def.put_f64("floatKey", 123.456).unwrap();
    assert!((def.get_f64("floatKey").unwrap().unwrap() - 123.456).abs() < 1e-4);

    def.put_bool("boolKey", true).unwrap();
    assert_eq!(def.get_bool("boolKey").unwrap(), Some(true));

    assert!(def.contains_key("strKey").unwrap());
    assert_eq!(def.count().unwrap(), 5);

    let keys = def.keys_all().unwrap();
    assert!(keys.contains(&"strKey".to_string()));
    assert!(keys.contains(&"intKey".to_string()));

    let mut prefix_iter = def.iter_prefix(Some("str")).unwrap();
    let first = prefix_iter.next().unwrap();
    assert_eq!(first.key, "strKey");
    assert!(prefix_iter.next().is_none());

    let range_items = def.scan_range(Some("a"), Some("z")).unwrap();
    assert!(!range_items.is_empty());

    assert!(def.delete("strKey").unwrap());
    assert!(!def.contains_key("strKey").unwrap());

    def.clear().unwrap();
    assert_eq!(def.count().unwrap(), 0);

    // 2. 自定义命名空间门面 ns("users")
    let user_scope = db.ns("users");
    user_scope.put_string("user:1", "Alice").unwrap();
    user_scope.put_i64("age:1", 30).unwrap();

    assert_eq!(
        user_scope.get_string("user:1").unwrap().as_deref(),
        Some("Alice")
    );
    assert_eq!(
        db.get_string("users", "user:1").unwrap().as_deref(),
        Some("Alice")
    );
    assert_eq!(user_scope.get_i64("age:1").unwrap(), Some(30));
    assert_eq!(user_scope.count().unwrap(), 2);

    assert!(user_scope.delete("user:1").unwrap());
    assert!(!user_scope.contains_key("user:1").unwrap());

    // 3. Batch 门面
    db.batch(|b| {
        let mut b_scope = b.default_ns();
        b_scope.put_string("batchKey", "batchVal")?;

        // 读己所写
        assert_eq!(b_scope.get_string("batchKey")?.as_deref(), Some("batchVal"));
        Ok(())
    })
    .unwrap();

    assert_eq!(
        db.get_string("", "batchKey").unwrap().as_deref(),
        Some("batchVal")
    );

    // 4. Snapshot 门面
    let snap = db.snapshot();
    let snap_scope = snap.default_ns();
    assert_eq!(
        snap_scope.get_string("batchKey").unwrap().as_deref(),
        Some("batchVal")
    );
    assert_eq!(snap_scope.count().unwrap(), 1);
}
