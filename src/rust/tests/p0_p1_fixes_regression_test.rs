//! P0/P1 修复回归测试（与 Go/.NET 同构）：
//! - 墓碑（OpDelete/OpDropNamespace）重放
//! - TxID 单调性 / OpCount 范围 / 未知 OpCode 拒绝
//! - 锁行为已随 C9 迁移至 tests/lock_protocol_test.rs（内核锁）
//! - 写前预检：外部磁盘篡改检测 + B1 空库回归
//! - 快照隔离与类型化 getter 的 TypeMismatch

use ckv::format::{
    BatchFrameWriter, FileHeader, FrameLoader, RawOpEntry, FILE_HEADER_LEN, FRAME_HEADER_LEN,
};
use ckv::types::{
    FRAME_MAGIC, OP_DELETE, OP_DROP_NAMESPACE, OP_PUT, VAL_TYPE_BOOL, VAL_TYPE_INT64,
    VAL_TYPE_STRING,
};
use ckv::{Db, DbError};
use std::fs;
use std::io::{Read, Seek, SeekFrom};
use std::thread;
use std::time::Duration;
use tempfile::tempdir;

// ---------- 测试文件构建工具（按 v3.2 规范手工构造帧） ----------

fn t_write_u32(buf: &mut [u8], off: usize, val: u32) {
    buf[off..off + 4].copy_from_slice(&val.to_le_bytes());
}

fn t_write_u64(buf: &mut [u8], off: usize, val: u64) {
    buf[off..off + 8].copy_from_slice(&val.to_le_bytes());
}

fn t_build_entry(op_code: u8, val_type: u8, ns: &str, key: &[u8], val: &[u8]) -> Vec<u8> {
    let e = RawOpEntry {
        op_code,
        value_type: val_type,
        namespace: ns.as_bytes().to_vec(),
        key: key.to_vec(),
        value: val.to_vec(),
    };
    let mut out = Vec::with_capacity(e.encoded_len());
    e.encode(&mut out);
    out
}

/// 手工构造帧（绕过 BatchFrameWriter 的 OpCount 校验，用于注入非法帧）。
fn t_build_frame_raw(tx_id: u64, op_count: u32, payload: &[u8]) -> Vec<u8> {
    let mut tail20 = [0u8; 20];
    t_write_u64(&mut tail20, 0, tx_id);
    t_write_u32(&mut tail20, 8, op_count);
    t_write_u32(&mut tail20, 12, payload.len() as u32);
    t_write_u32(&mut tail20, 16, ckv::crc32::crc32(payload));

    let mut frame = vec![0u8; FRAME_HEADER_LEN + payload.len()];
    t_write_u32(&mut frame, 0, FRAME_MAGIC);
    t_write_u32(&mut frame, 4, ckv::crc32::crc32(&tail20));
    frame[8..28].copy_from_slice(&tail20);
    frame[28..].copy_from_slice(payload);
    frame
}

fn t_build_db(frames: &[Vec<u8>]) -> Vec<u8> {
    let mut out = FileHeader::create_bytes().to_vec();
    for f in frames {
        out.extend_from_slice(f);
    }
    out
}

// ---------- P0-2：墓碑必须被重放 ----------

#[test]
fn tombstones_are_replayed_on_load() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("tombstone.db");

    // 帧1：put keep/dead（ns1）+ put x（ns_gone）
    // 帧2：delete dead（ns1）
    // 帧3：drop 整个 ns_gone
    // 帧4：put after_delete（验证 delete 之后的 put 生效）
    let frame1 = t_build_frame_raw(
        1,
        3,
        &[
            t_build_entry(OP_PUT, VAL_TYPE_STRING, "ns1", b"keep", b"v_keep"),
            t_build_entry(OP_PUT, VAL_TYPE_STRING, "ns1", b"dead", b"v_dead"),
            t_build_entry(OP_PUT, VAL_TYPE_STRING, "ns_gone", b"x", b"v_x"),
        ]
        .concat(),
    );
    let frame2 = t_build_frame_raw(
        2,
        1,
        &t_build_entry(OP_DELETE, 0, "ns1", b"dead", b""),
    );
    let frame3 = t_build_frame_raw(
        3,
        1,
        &t_build_entry(OP_DROP_NAMESPACE, 0, "ns_gone", b"", b""),
    );
    let frame4 = t_build_frame_raw(
        4,
        1,
        &t_build_entry(OP_PUT, VAL_TYPE_STRING, "ns1", b"after_delete", b"v_ad"),
    );

    fs::write(&db_path, t_build_db(&[frame1, frame2, frame3, frame4])).unwrap();

    let db = Db::open(&db_path, None).unwrap();
    assert!(db.contains_key("ns1", "keep").unwrap());
    assert!(!db.contains_key("ns1", "dead").unwrap());
    assert!(db.contains_key("ns1", "after_delete").unwrap());
    assert_eq!(db.count("ns_gone").unwrap(), 0);
    assert_eq!(db.get_string("ns1", "keep").unwrap().unwrap(), "v_keep");
}

// ---------- P0-3：TxID 单调性 ----------

#[test]
fn non_monotonic_tx_id_is_rejected() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("nonmono.db");

    let f1 = t_build_frame_raw(
        1,
        1,
        &t_build_entry(OP_PUT, VAL_TYPE_STRING, "ns", b"k1", b"v1"),
    );
    let f2 = t_build_frame_raw(
        1, // 相同 TxID
        1,
        &t_build_entry(OP_PUT, VAL_TYPE_STRING, "ns", b"k2", b"v2"),
    );
    fs::write(&db_path, t_build_db(&[f1, f2])).unwrap();

    match Db::open(&db_path, None) {
        Err(DbError::Corrupted(_)) => {}
        other => panic!("Expected Corrupted error for non-monotonic TxID, got {:?}", other.map(|_| "Ok(db)")),
    }
}

#[test]
fn descending_tx_id_is_rejected() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("descending.db");

    let f1 = t_build_frame_raw(
        5,
        1,
        &t_build_entry(OP_PUT, VAL_TYPE_STRING, "ns", b"k1", b"v1"),
    );
    let f2 = t_build_frame_raw(
        3, // TxID 递减
        1,
        &t_build_entry(OP_PUT, VAL_TYPE_STRING, "ns", b"k2", b"v2"),
    );
    fs::write(&db_path, t_build_db(&[f1, f2])).unwrap();

    match Db::open(&db_path, None) {
        Err(DbError::Corrupted(_)) => {}
        other => panic!("Expected Corrupted error for descending TxID, got {:?}", other.map(|_| "Ok(db)")),
    }
}

// ---------- P0-3：OpCount 范围 ----------

#[test]
fn zero_op_count_frame_is_rejected() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("zeroops.db");

    let f1 = t_build_frame_raw(1, 0, &[]); // OpCount = 0
    fs::write(&db_path, t_build_db(&[f1])).unwrap();

    match Db::open(&db_path, None) {
        Err(DbError::Corrupted(_)) => {}
        other => panic!("Expected Corrupted error for zero OpCount, got {:?}", other.map(|_| "Ok(db)")),
    }
}

// ---------- P0-3：未知 OpCode 拒绝（与 Go/.NET 对齐） ----------

#[test]
fn unknown_op_code_is_rejected() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("unknownop.db");

    let f1 = t_build_frame_raw(1, 1, &t_build_entry(0x7F, 0, "ns", b"k", b"")); // 规范外 OpCode
    fs::write(&db_path, t_build_db(&[f1])).unwrap();

    match Db::open(&db_path, None) {
        Err(DbError::Corrupted(_)) => {}
        other => panic!("Expected Corrupted error for unknown op code, got {:?}", other.map(|_| "Ok(db)")),
    }
}

// ---------- C12 / T12：恢复期校验定长类型与保留类型 ----------

#[test]
fn t12_recovery_rejects_invalid_value_type_or_length() {
    let dir = tempdir().unwrap();

    // 1. 保留 value_type = 0x05
    let p_vtype = dir.path().join("reserved_vtype.db");
    let f_vtype = t_build_frame_raw(1, 1, &t_build_entry(OP_PUT, 0x05, "ns", b"k", b"val"));
    fs::write(&p_vtype, t_build_db(&[f_vtype])).unwrap();
    assert!(matches!(Db::open(&p_vtype, None), Err(DbError::Corrupted(_))));

    // 2. int64 value 长度为 7 字节 (!= 8)
    let p_i64 = dir.path().join("bad_i64.db");
    let f_i64 = t_build_frame_raw(1, 1, &t_build_entry(OP_PUT, VAL_TYPE_INT64, "ns", b"k", &[1, 2, 3, 4, 5, 6, 7]));
    fs::write(&p_i64, t_build_db(&[f_i64])).unwrap();
    assert!(matches!(Db::open(&p_i64, None), Err(DbError::Corrupted(_))));

    // 3. bool value 长度为 3 字节 (!= 1)
    let p_bool = dir.path().join("bad_bool.db");
    let f_bool = t_build_frame_raw(1, 1, &t_build_entry(OP_PUT, VAL_TYPE_BOOL, "ns", b"k", &[1, 0, 1]));
    fs::write(&p_bool, t_build_db(&[f_bool])).unwrap();
    assert!(matches!(Db::open(&p_bool, None), Err(DbError::Corrupted(_))));
}

// ---------- P0-8：锁行为（C9 后为内核锁，见 tests/lock_protocol_test.rs） ----------
// 旧实现基于锁文件的 32B 内容（PID | UUID | CreateTimeMs）+ 时间戳判活 + 窃取协议，
// 相关用例已随 C9 一并删除：判活/窃取启发式整体移除，锁改为在 `open` 时获取、持有整个
// 连接生命周期的内核锁。新的门槛用例见 `tests/lock_protocol_test.rs`。

// ---------- P1-3：写前预检 —— 外部磁盘篡改检测 ----------

#[test]
fn external_disk_modification_is_detected() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("tamper.db");

    let db = Db::open(&db_path, None).unwrap();
    db.put_string("ns", "k1", "v1").unwrap();
    db.put_string("ns", "k2", "v2").unwrap();

    // 外部"合法"写入：追加一个 TxID 更高的有效帧，改变 (maxTxID, fileSize) 基线
    {
        let mut f = fs::OpenOptions::new()
            .read(true)
            .append(true)
            .open(&db_path)
            .unwrap();
        BatchFrameWriter::write_frame(
            &mut f,
            999,
            &[RawOpEntry {
                op_code: OP_PUT,
                value_type: VAL_TYPE_STRING,
                namespace: b"ns".to_vec(),
                key: b"evil".to_vec(),
                value: b"x".to_vec(),
            }],
        )
        .unwrap();
    }

    // 下一次写入必须检测到磁盘状态变化
    match db.put_string("ns", "k3", "v3") {
        Err(DbError::ConcurrentModification(_)) => {}
        other => panic!("Expected ConcurrentModification error, got {:?}", other),
    }
}

// ---------- P1-3：B1 空库回归 —— 整库清空后再次写入不得误报 ----------

#[test]
fn write_after_full_clear_does_not_false_positive() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("b1.db");

    {
        let db = Db::open(&db_path, None).unwrap();
        db.put_string("ns1", "k1", "v1").unwrap();
        db.put_string("ns2", "k2", "v2").unwrap();

        // 清空整库：落盘为仅含 32B 文件头的空库
        assert!(db.drop_namespace("ns1").unwrap());
        assert!(db.drop_namespace("ns2").unwrap());

        // B1 回归：空库后再次写入不得误报 ConcurrentModification
        db.put_string("ns1", "k3", "v3").unwrap();
        db.put_string("ns1", "k4", "v4").unwrap();
        db.put_string("ns2", "k5", "v5").unwrap();
        assert_eq!(db.count("ns1").unwrap(), 2);
        assert_eq!(db.count("ns2").unwrap(), 1);
    }

    // 重载验证持久化完整，且重载后继续写入不误报
    let db2 = Db::open(&db_path, None).unwrap();
    assert_eq!(db2.get_string("ns1", "k3").unwrap().unwrap(), "v3");
    assert_eq!(db2.get_string("ns2", "k5").unwrap().unwrap(), "v5");
    db2.put_string("ns1", "k6", "v6").unwrap();
    assert_eq!(db2.count("ns1").unwrap(), 3);
}

// ---------- P1-5：快照隔离（Arc CoW） ----------

#[test]
fn snapshot_isolation_holds() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("snapshot.db");

    let db = Db::open(&db_path, None).unwrap();
    db.put_string("ns", "a", "1").unwrap();
    db.put_string("ns", "b", "2").unwrap();

    let snap = db.snapshot();
    assert_eq!(snap.count("ns").unwrap(), 2);

    // 快照创建后的写入不得影响快照
    db.put_string("ns", "a", "modified").unwrap();
    db.put_string("ns", "c", "3").unwrap();
    db.delete("ns", "b").unwrap();

    assert_eq!(snap.get_string("ns", "a").unwrap().unwrap(), "1");
    assert!(snap.contains_key("ns", "b").unwrap());
    assert!(!snap.contains_key("ns", "c").unwrap());
    assert_eq!(snap.count("ns").unwrap(), 2);

    // 主库看到最新状态
    assert_eq!(db.get_string("ns", "a").unwrap().unwrap(), "modified");
    assert_eq!(db.count("ns").unwrap(), 2); // a + c（b 已删）
}

// ---------- 类型化 getter 的 TypeMismatch ----------

#[test]
fn typed_getters_reject_type_mismatch() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("typemismatch.db");

    let db = Db::open(&db_path, None).unwrap();
    db.put_i64("ns", "num", 42).unwrap();

    // get_string 读到 int64 → TypeMismatch
    match db.get_string("ns", "num") {
        Err(DbError::TypeMismatch(_)) => {}
        other => panic!("Expected TypeMismatch for get_string on int64, got {:?}", other),
    }
    // get_i64 正常
    assert_eq!(db.get_i64("ns", "num").unwrap(), Some(42));

    db.put_string("ns", "text", "hello").unwrap();
    match db.get_i64("ns", "text") {
        Err(DbError::TypeMismatch(_)) => {}
        other => panic!("Expected TypeMismatch for get_i64 on string, got {:?}", other),
    }
}

// ---------- 混排 OpLog 顺序重放 ----------

#[test]
fn mixed_op_log_replays_in_order() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("mixed.db");

    let db = Db::open(&db_path, None).unwrap();
    // 同一键的 put→delete→put 序列，最终值必须来自最后一次 put
    db.put_string("ns", "k", "v1").unwrap();
    db.delete("ns", "k").unwrap();
    db.put_string("ns", "k", "v2").unwrap();
    db.put_string("ns", "k", "v3").unwrap();
    drop(db);

    let db2 = Db::open(&db_path, None).unwrap();
    assert_eq!(db2.get_string("ns", "k").unwrap().unwrap(), "v3");
    assert_eq!(db2.count("ns").unwrap(), 1);
}

// ---------- 帧读取一致性：FrameLoader 与手工构造文件互通 ----------

#[test]
fn frame_loader_reads_manually_built_frames() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("loader.db");

    let f1 = t_build_frame_raw(
        7,
        1,
        &t_build_entry(OP_PUT, VAL_TYPE_STRING, "ns", b"k", b"v"),
    );
    let bytes = t_build_db(&[f1]);
    fs::write(&db_path, &bytes).unwrap();

    let mut f = fs::File::open(&db_path).unwrap();
    let mut header = [0u8; FILE_HEADER_LEN];
    f.read_exact(&mut header).unwrap();
    FileHeader::verify_bytes(&header).unwrap();

    let (entries, max_tx) = FrameLoader::load_all(&mut f, bytes.len() as u64).unwrap();
    assert_eq!(entries.len(), 1);
    assert_eq!(max_tx, 7);

    // scan_max_tx_id 快速扫描得到相同 TxID
    f.seek(SeekFrom::Start(0)).unwrap();
    assert_eq!(
        FrameLoader::scan_max_tx_id(&mut f, bytes.len() as u64).unwrap(),
        7
    );

    // 静默期无副作用：thread 睡眠不改变文件内容
    let before = fs::metadata(&db_path).unwrap().len();
    thread::sleep(Duration::from_millis(5));
    assert_eq!(fs::metadata(&db_path).unwrap().len(), before);
}

// ---------- C17: 重复 open 选项冲突告警 ----------

#[test]
fn t17_reentrant_open_warns_on_options_conflict() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("c17_conflict.db");

    // 首个打开实例：读写模式
    let db1 = Db::open(&db_path, None).unwrap();
    db1.put_string("ns", "k", "v").unwrap();

    // 第二次打开同路径：传入只读与不同的同步模式，并配置 on_warning
    let warnings = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
    let w_clone = warnings.clone();
    let opts2 = ckv::options::DbOptions::new()
        .read_only(true)
        .sync_mode(ckv::SyncMode::Manual)
        .on_warning(move |code, msg| {
            w_clone.lock().unwrap().push((code.to_string(), msg.to_string()));
        });

    let db2 = Db::open(&db_path, Some(opts2)).unwrap();

    // 依然返回同一单例
    assert_eq!(db2.get_string("ns", "k").unwrap().unwrap(), "v");
    // 成功发出 onOptionsConflict 告警
    let captured = warnings.lock().unwrap().clone();
    assert_eq!(captured.len(), 1);
    assert_eq!(captured[0].0, "onOptionsConflict");
    assert!(captured[0].1.contains("read_only"));
}
