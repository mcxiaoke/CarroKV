//! API-SPEC §3.3.3：非法 UTF-8 键在扫描接口里必须**跳过**，不得用 lossy 替换成
//! U+FFFD 造出一个指向"别的键"的伪条目。
//!
//! 写 API 的 key 参数都是 `&str`，所以非法键只能来自损坏/外部构造的文件：
//! 这里直接把合法库里 1 字节键改成 0xFF，并重算两级 CRC。

use carro_kv::{Db, DbOptions};
use tempfile::tempdir;

fn corrupt_key_byte(path: &std::path::Path) {
    let mut b = std::fs::read(path).unwrap();
    // 32B 文件头 + 28B 帧头 + 10B 条目定长头 = 70；ns 为空、key 长度 1。
    b[70] = 0xFF;
    let payload = carro_kv::crc32::crc32(&b[60..]);
    b[56..60].copy_from_slice(&payload.to_le_bytes());
    let header = carro_kv::crc32::crc32(&b[40..60]);
    b[36..40].copy_from_slice(&header.to_le_bytes());
    std::fs::write(path, b).unwrap();
}

#[test]
fn scans_skip_keys_that_are_not_valid_utf8() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("bad_key.db");

    {
        let db = Db::open(&path, Some(DbOptions::default())).unwrap();
        db.put_string("", "k", "v").unwrap();
        db.close().unwrap();
    }

    corrupt_key_byte(&path);

    let db = Db::open(&path, Some(DbOptions::default())).unwrap();

    assert!(
        db.keys("", None).unwrap().is_empty(),
        "keys() must not surface an invalid UTF-8 key"
    );
    assert!(
        db.iter_prefix("", None)
            .unwrap()
            .collect::<Vec<_>>()
            .is_empty(),
        "iter_prefix() must skip the invalid key instead of returning U+FFFD"
    );
    // §3.3.3 只约束 keys / iter_prefix；scan_range 按字节序枚举，v1.0 冻结的语义是
    // "条目仍在列表里"（键字符串必然是 lossy 的），这里只锁住条数不被误删。
    assert_eq!(db.scan_range("", None, None).unwrap().len(), 1);
    // 条目本身还在（跳过只发生在"解码成字符串"这一步）。
    assert_eq!(db.count("").unwrap(), 1);

    // 快照是另一套实现，同一份数据必须给出同一结论。
    let snap = db.snapshot();
    assert!(snap.keys("", None).unwrap().is_empty());
    assert!(snap
        .iter_prefix("", None)
        .unwrap()
        .collect::<Vec<_>>()
        .is_empty());
    assert_eq!(snap.scan_range("", None, None).unwrap().len(), 1);
    drop(snap);

    db.close().unwrap();
}
