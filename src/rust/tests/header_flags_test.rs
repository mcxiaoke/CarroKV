//! M1-01 regression: a non-zero `HeaderFlags` must be rejected on open.
//!
//! 规范 KV-DB-DESIGN-SPEC.md:79 明确 "v3.2 固定填 0；若读取端遇到未知非零必选位
//! 则拒绝打开"。五个实现此前都只校验 magic / readVer / CRC，`12..16` 从未读取，
//! 于是带未知必选位的文件会被按当前语义静默解读。

use carro_kv::{Db, DbError, DbOptions};
use tempfile::tempdir;

fn patch_flags(path: &std::path::Path, flags: u32) {
    let mut bytes = std::fs::read(path).unwrap();
    bytes[12..16].copy_from_slice(&flags.to_le_bytes());
    // 重算头部 CRC，模拟"写入方有意设置未知必选位"的文件；不重算就只是位翻转，
    // 会被正确地判成 Corrupted 而不是 UnsupportedVersion。
    let crc = carro_kv::crc32::crc32(&bytes[..28]);
    bytes[28..32].copy_from_slice(&crc.to_le_bytes());
    std::fs::write(path, bytes).unwrap();
}

#[test]
fn non_zero_header_flags_are_rejected() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("flags.db");

    {
        let db = Db::open(&path, Some(DbOptions::default())).unwrap();
        db.put_string("", "k", "v").unwrap();
        db.close().unwrap();
    }

    patch_flags(&path, 1);

    match Db::open(&path, Some(DbOptions::default())) {
        Err(DbError::UnsupportedVersion(msg)) => {
            assert!(msg.contains("flags"), "unexpected message: {msg}")
        }
        Ok(_) => panic!("expected UnsupportedVersion, but the file opened"),
        Err(other) => panic!("expected UnsupportedVersion, got {other}"),
    }
}

#[test]
fn zero_flags_still_open() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("ok.db");
    {
        let db = Db::open(&path, Some(DbOptions::default())).unwrap();
        db.put_string("", "k", "v").unwrap();
        db.close().unwrap();
    }
    let db = Db::open(&path, Some(DbOptions::default())).unwrap();
    assert_eq!(db.get_string("", "k").unwrap(), Some("v".to_string()));
    db.close().unwrap();
}
