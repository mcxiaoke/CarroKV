//! 批次一「数据安全与止血」回归测试（Rust 参考实现）。
//!
//! 覆盖：
//!  - P1-3  加载期校验 `NsLen <= 255`：越界即 `Corrupted`，而不是加载出一个既读不出、
//!    也删不掉的孤儿命名空间（该孤儿还会被每次快照重新序列化进 `.db` / `.bak` /
//!    全部 `backup()` 产物，污染其余四门语言）。
//!  - 条目 11 `read_only` 选项冲突返回 `ConflictingOptionsError`，绝不静默升权。
//!
//! 其余批次一条目（Rust 侧无对应缺陷）由各语言自己的同源套件覆盖：
//! Go `renameWithRetry` / Kotlin 写入口收口 / .NET 注册表与所有权 / Dart 命名空间校验。

use carro_kv::{Db, DbError, DbOptions};
use std::fs;
use tempfile::tempdir;

fn write_u16(buf: &mut [u8], off: usize, v: usize) {
    buf[off] = (v & 0xFF) as u8;
    buf[off + 1] = ((v >> 8) & 0xFF) as u8;
}

fn write_u32(buf: &mut [u8], off: usize, v: u32) {
    buf[off] = (v & 0xFF) as u8;
    buf[off + 1] = ((v >> 8) & 0xFF) as u8;
    buf[off + 2] = ((v >> 16) & 0xFF) as u8;
    buf[off + 3] = ((v >> 24) & 0xFF) as u8;
}

fn write_u64(buf: &mut [u8], off: usize, v: u64) {
    for i in 0..8 {
        buf[off + i] = ((v >> (8 * i)) & 0xFF) as u8;
    }
}

fn file_header() -> Vec<u8> {
    let mut h = vec![0u8; 32];
    h[..8].copy_from_slice(b"UKVDB003");
    write_u16(&mut h, 8, 3);
    write_u16(&mut h, 10, 3);
    write_u32(&mut h, 12, 0);
    let crc = carro_kv::crc32::crc32(&h[..28]);
    write_u32(&mut h, 28, crc);
    h
}

/// 构造一个条目头声明 `NsLen = 300`（规范 §3.2.2 硬上限 255）的合法 CRC 文件。
fn build_db_with_overlong_namespace() -> Vec<u8> {
    let ns = vec![b'n'; 300];
    let key = b"k";
    let value = b"v";

    let mut entry = vec![0u8; 10 + ns.len() + key.len() + value.len()];
    entry[0] = 0x01; // OP_PUT
    entry[1] = 0x01; // VAL_TYPE_STRING
    write_u16(&mut entry, 2, 300);
    write_u16(&mut entry, 4, key.len());
    write_u32(&mut entry, 6, value.len() as u32);
    entry[10..10 + ns.len()].copy_from_slice(&ns);
    entry[10 + ns.len()..10 + ns.len() + key.len()].copy_from_slice(key);
    entry[10 + ns.len() + key.len()..].copy_from_slice(value);

    let mut tail20 = vec![0u8; 20];
    write_u64(&mut tail20, 0, 1); // TxID
    write_u32(&mut tail20, 8, 1); // opCount
    write_u32(&mut tail20, 12, entry.len() as u32);
    write_u32(&mut tail20, 16, carro_kv::crc32::crc32(&entry));

    let mut frame = vec![0u8; 28 + entry.len()];
    write_u32(&mut frame, 0, 0x4D52464B); // 'KFRM'
    write_u32(&mut frame, 4, carro_kv::crc32::crc32(&tail20));
    frame[8..28].copy_from_slice(&tail20);
    frame[28..].copy_from_slice(&entry);

    let mut out = file_header();
    out.extend_from_slice(&frame);
    out
}

// ---------- P1-3：加载期 NsLen 越界 ----------

#[test]
fn load_rejects_a_namespace_longer_than_255_bytes() {
    let dir = tempdir().unwrap();
    let p = dir.path().join("nslen.db");
    fs::write(&p, build_db_with_overlong_namespace()).unwrap();

    match Db::open(&p, None) {
        // 载荷解析器会把单条目错误包装成"载荷偏移 N 解码失败"，因此只断言它被识别为
        // Corrupted —— 关键性质是"坏文件在 open 阶段明确失败"，而不是加载出一个
        // 既读不出也删不掉的孤儿命名空间。
        Err(DbError::Corrupted(_)) => {}
        Err(other) => panic!("expected Corrupted, got {other:?}"),
        Ok(_) => panic!("a file declaring NsLen=300 (>255) must be rejected at load time"),
    }
}

#[test]
fn namespace_of_exactly_255_bytes_is_accepted() {
    let dir = tempdir().unwrap();
    let p = dir.path().join("edge.db");
    let ns = "n".repeat(255);

    let db = Db::open(&p, None).unwrap();
    db.put_string(&ns, "k", "v").unwrap();
    assert_eq!(db.get_string(&ns, "k").unwrap(), Some("v".to_string()));
    // 落盘后重开必须仍能读出（写入侧与加载侧对上限的理解一致）。
    db.close().unwrap();
    let db = Db::open(&p, None).unwrap();
    assert_eq!(db.get_string(&ns, "k").unwrap(), Some("v".to_string()));
}

#[test]
fn a_namespace_longer_than_255_bytes_is_refused_by_the_api() {
    let dir = tempdir().unwrap();
    let p = dir.path().join("api_guard.db");
    let db = Db::open(&p, None).unwrap();

    let long = "n".repeat(300);
    for r in [
        db.put_string(&long, "k", "v").map(|_| ()),
        db.get_string(&long, "k").map(|_| ()),
        db.delete(&long, "k").map(|_| ()),
        db.drop_namespace(&long).map(|_| ()),
        db.clear_namespace(&long).map(|_| ()),
    ] {
        match r {
            Err(DbError::InvalidNamespace(msg)) => {
                assert!(msg.contains("300"), "unexpected message: {msg}");
            }
            other => panic!("expected InvalidNamespace, got {other:?}"),
        }
    }
}

// ---------- 条目 11：read_only 冲突必须抛错 ----------

#[test]
fn reentrant_open_with_read_only_conflict_returns_conflicting_options() {
    let dir = tempdir().unwrap();
    let p = dir.path().join("ro.db");

    let rw = Db::open(&p, Some(DbOptions::new().read_only(false))).unwrap();
    rw.put_string("", "k", "v").unwrap();

    // 以只读意图二次打开：绝不能静默返回读写实例（静默提权）。
    let ro = Db::open(&p, Some(DbOptions::new().read_only(true)));
    match ro {
        Err(DbError::ConflictingOptions(msg)) => {
            assert!(msg.contains("read_only"), "unexpected message: {msg}");
        }
        Err(other) => panic!("expected ConflictingOptions, got {other:?}"),
        Ok(_) => panic!("read_only conflict must be refused, not silently reused"),
    }

    // 反方向同样拒绝：要写却拿到只读实例会让后续写入在远处以 Locked 失败。
    rw.close().unwrap();
    let ro = Db::open(&p, Some(DbOptions::new().read_only(true))).unwrap();
    assert_eq!(ro.get_string("", "k").unwrap(), Some("v".to_string()));
    match Db::open(&p, Some(DbOptions::new().read_only(false))) {
        Err(DbError::ConflictingOptions(_)) => {}
        Err(other) => panic!("expected ConflictingOptions, got {other:?}"),
        Ok(_) => panic!("read_only conflict must be refused in both directions"),
    }
    ro.close().unwrap();
}

#[test]
fn reentrant_open_with_engine_mode_conflict_still_warns() {
    use std::sync::{Arc, Mutex};

    let dir = tempdir().unwrap();
    let p = dir.path().join("engine.db");

    let db = Db::open(
        &p,
        Some(DbOptions::new().engine_mode(carro_kv::StorageEngineMode::SnapshotAtomic)),
    )
    .unwrap();
    db.put_string("", "k", "v").unwrap();

    let warnings = Arc::new(Mutex::new(Vec::new()));
    let w = warnings.clone();
    let same = Db::open(
        &p,
        Some(
            DbOptions::new()
                .engine_mode(carro_kv::StorageEngineMode::AppendOnlyLog)
                .on_warning(move |code, msg| {
                    w.lock().unwrap().push((code.to_string(), msg.to_string()));
                }),
        ),
    )
    .unwrap();

    let captured = warnings.lock().unwrap().clone();
    assert_eq!(captured.len(), 1);
    assert_eq!(captured[0].0, "onOptionsConflict");
    assert!(captured[0].1.contains("engine_mode"));
    // 非权限类冲突仍沿用既有 options，返回同一单例。
    assert_eq!(same.get_string("", "k").unwrap(), Some("v".to_string()));
    db.close().unwrap();
}
