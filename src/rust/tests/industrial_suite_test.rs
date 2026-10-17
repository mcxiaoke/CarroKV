use ckv::Db;
use tempfile::tempdir;

/// 1. fjall & rocksdb 范式：同一 Batch 内对同一 Key 连续多次覆写、删除并重新赋值
#[test]
fn test_batch_same_key_overwrites_and_tombstones() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("batch_overwrite.db");
    let db = Db::open(&db_path, None).unwrap();

    // 预先写入初始值
    db.put_string("ns1", "target_key", "initial_v0").unwrap();
    db.put_string("ns1", "stay_key", "stay_v0").unwrap();

    // 在 batch 中进行一系列覆写和删除
    let mut batch = db.new_batch();
    batch.put_string("ns1", "target_key", "temp_v1").unwrap();
    batch.put_string("ns1", "target_key", "temp_v2").unwrap();
    batch.delete("ns1", "target_key").unwrap();
    batch.put_string("ns1", "target_key", "final_v3").unwrap();

    // 跨命名空间同时操作
    batch.put_string("ns2", "target_key", "ns2_v1").unwrap();
    batch.delete("ns1", "stay_key").unwrap();

    // 此时提交前，db 读到的仍是旧状态
    assert_eq!(
        db.get_string("ns1", "target_key").unwrap(),
        Some("initial_v0".to_string())
    );
    assert_eq!(
        db.get_string("ns1", "stay_key").unwrap(),
        Some("stay_v0".to_string())
    );

    // 提交批处理
    db.write_batch(batch).unwrap();

    // 提交后原子可见
    assert_eq!(
        db.get_string("ns1", "target_key").unwrap(),
        Some("final_v3".to_string())
    );
    assert_eq!(db.get_string("ns1", "stay_key").unwrap(), None);
    assert_eq!(
        db.get_string("ns2", "target_key").unwrap(),
        Some("ns2_v1".to_string())
    );
}

/// 2. hive_ce 范式：极端 Unicode 字符（带 ZWJ 复合 Emoji、多语言文本）、空 Key、空 Value、大 Blob (512KB)
#[test]
fn test_unicode_complex_and_zero_byte_keys() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("unicode_niche.db");
    let db = Db::open(&db_path, None).unwrap();

    // 空字符串作为 Key
    db.put_string("", "", "empty_key_val").unwrap();
    assert_eq!(
        db.get_string("", "").unwrap(),
        Some("empty_key_val".to_string())
    );

    // 空字节作为 Value
    db.put_bytes("", "empty_val_key", []).unwrap();
    assert_eq!(db.get_bytes("", "empty_val_key").unwrap(), Some(vec![]));

    // 复杂的各种语言文本与带零宽连字 (ZWJ) 复合 Emoji
    let complex_unicode_cases = vec![
        ("zwj_family", "👨‍👨‍👧‍👦"),
        ("arabic", " ﻬ ﻭ ﻮ ﻯ ﻰ ﻱ"),
        ("malayalam", "അ ആ ഇ "),
        ("katakana", "ｩ ｪ ｫ ｬ ｭ ｮ ｯ ｰ "),
        ("special_math", "∀x ∈ ℝ: x² ≥ 0"),
        ("surrogate_pairs", "𠮷野家"),
    ];

    for (k, v) in &complex_unicode_cases {
        db.put_string("i18n", k, v).unwrap();
        assert_eq!(db.get_string("i18n", k).unwrap(), Some(v.to_string()));
    }

    // 512KB 大 Blob
    let large_blob = vec![0xABu8; 512 * 1024];
    db.put_bytes("blob_ns", "large_blob_key", &large_blob)
        .unwrap();
    assert_eq!(
        db.get_bytes("blob_ns", "large_blob_key").unwrap(),
        Some(large_blob)
    );
}

/// 3. rocksdb 列族 / redb 表范式：命名空间完全隔离与 Drop 重建
#[test]
fn test_namespace_isolation_and_drop_recreate() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("ns_isolation.db");
    let db = Db::open(&db_path, None).unwrap();

    // 同名 key 分别写入 3 个命名空间（包括默认命名空间）
    db.put_string("", "shared_id", "default_val").unwrap();
    db.put_string("default", "shared_id", "default_val_alias")
        .unwrap(); // 别名测试
    db.put_string("ns_alpha", "shared_id", "alpha_val").unwrap();
    db.put_string("ns_beta", "shared_id", "beta_val").unwrap();

    // 验证默认命名空间与 "" 完全互通
    assert_eq!(
        db.get_string("", "shared_id").unwrap(),
        Some("default_val_alias".to_string())
    );
    assert_eq!(
        db.get_string("default", "shared_id").unwrap(),
        Some("default_val_alias".to_string())
    );

    // 验证各命名空间数据隔离
    assert_eq!(
        db.get_string("ns_alpha", "shared_id").unwrap(),
        Some("alpha_val".to_string())
    );
    assert_eq!(
        db.get_string("ns_beta", "shared_id").unwrap(),
        Some("beta_val".to_string())
    );

    // Drop 掉 ns_alpha
    db.drop_namespace("ns_alpha").unwrap();
    assert_eq!(db.get_string("ns_alpha", "shared_id").unwrap(), None);

    // 其他命名空间毫发无损
    assert_eq!(
        db.get_string("", "shared_id").unwrap(),
        Some("default_val_alias".to_string())
    );
    assert_eq!(
        db.get_string("ns_beta", "shared_id").unwrap(),
        Some("beta_val".to_string())
    );

    // 重建 ns_alpha 再次写入
    db.put_string("ns_alpha", "shared_id", "recreated_alpha_val")
        .unwrap();
    assert_eq!(
        db.get_string("ns_alpha", "shared_id").unwrap(),
        Some("recreated_alpha_val".to_string())
    );
}

/// 4. rust-rocksdb 范式：范围扫描极限边界与 Seek 逻辑
#[test]
fn test_scan_range_advanced_boundaries() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("scan_bounds.db");
    let db = Db::open(&db_path, None).unwrap();

    // 填充有序测试集
    let keys = vec!["apple", "banana", "cherry", "date", "fig", "grape"];
    for k in &keys {
        db.put_string("fruit", k, "ok").unwrap();
    }

    // 1. start > end 空扫描
    let empty_scan = db.scan_range("fruit", Some("fig"), Some("banana")).unwrap();
    assert!(empty_scan.is_empty());

    // 2. start 和 end 完全一致（半开半闭 [start, end) 应该返回空）
    let exact_empty = db
        .scan_range("fruit", Some("cherry"), Some("cherry"))
        .unwrap();
    assert!(exact_empty.is_empty());

    // 3. 不存在的 start 定位（seek_ge 行为）："blueberry" 不存在，应该从 "cherry" 开始扫描
    let seek_scan = db
        .scan_range("fruit", Some("blueberry"), Some("date"))
        .unwrap();
    let seek_keys: Vec<&str> = seek_scan.iter().map(|e| e.key.as_str()).collect();
    assert_eq!(seek_keys, vec!["cherry"]);

    // 4. 超出范围的 end：end = "zebra"
    let all_from_cherry = db
        .scan_range("fruit", Some("cherry"), Some("zebra"))
        .unwrap();
    let all_keys: Vec<&str> = all_from_cherry.iter().map(|e| e.key.as_str()).collect();
    assert_eq!(all_keys, vec!["cherry", "date", "fig", "grape"]);

    // 5. 空命名空间扫描返回空列表
    let non_existent_ns = db.scan_range("non_existent", None, None).unwrap();
    assert!(non_existent_ns.is_empty());
}

/// 5. redb abort_tests 范式：事务批处理异常中断与暂存区丢弃
#[test]
fn test_batch_abort_staging_rollback() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("batch_abort.db");
    let db = Db::open(&db_path, None).unwrap();

    db.put_string("state", "status", "clean_v1").unwrap();

    // 使用 batch 构建修改，但不调用 write_batch（或者 write_batch 失败丢弃）
    let mut batch = db.new_batch();
    batch.put_string("state", "status", "dirty_v2").unwrap();
    batch
        .put_string("state", "new_leak", "should_not_exist")
        .unwrap();
    batch.delete("state", "status").unwrap();

    // 人为模拟业务逻辑中断：直接 drop 掉 batch，不 commit
    drop(batch);

    // 数据库原始状态毫发无损
    assert_eq!(
        db.get_string("state", "status").unwrap(),
        Some("clean_v1".to_string())
    );
    assert_eq!(db.get_string("state", "new_leak").unwrap(), None);

    // 再次正常写入不受任何影响
    db.put_string("state", "status", "clean_v2").unwrap();
    assert_eq!(
        db.get_string("state", "status").unwrap(),
        Some("clean_v2".to_string())
    );
}
