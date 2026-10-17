//! API v1.0 冻结契约回归测试（Rust）
//!
//! 覆盖 [docs/API-V1-FREEZE.md](../../docs/API-V1-FREEZE.md) 中属于 Rust 的条目：
//! 6 个新 API + §3.11 canonicalize_path 规范化 + §3.13 lock_timeout_ms 类型 +
//! §3.14/3.15 文档化语义的行为锁定。
//!
//! Go / .NET 有同构测试，三者必须一致。

use carro_kv::{Db, DbOptions};
use std::fs;
use std::path::Path;
use tempfile::tempdir;

fn temp_db(dir: &Path, name: &str) -> std::path::PathBuf {
    dir.join(name)
}

// §2.1 Namespaces()：按 UTF-8 字节序、含空命名空间、默认 ns 为 ""
#[test]
fn namespaces_lists_all_including_empty_and_default() {
    let dir = tempdir().unwrap();
    let db = Db::open(temp_db(dir.path(), "ns.db"), None).unwrap();

    db.put_string("zeta", "k", "v").unwrap();
    db.put_string("alpha", "k", "v").unwrap();
    db.put_string("", "k", "v").unwrap(); // default namespace
    db.put_string("mid", "k", "v").unwrap();
    assert!(db.clear_namespace("mid").unwrap()); // emptied but still present

    let ns = db.namespaces();
    // UTF-8 unsigned byte order: "" < "alpha" < "mid" < "zeta"
    assert_eq!(ns, vec!["", "alpha", "mid", "zeta"]);

    // Snapshot 同样提供
    let snap = db.snapshot();
    assert_eq!(snap.namespaces(), ns);

    // drop 后消失
    assert!(db.drop_namespace("zeta").unwrap());
    assert_eq!(db.namespaces(), vec!["", "alpha", "mid"]);
}

// §2.2 Unwatch()：显式注销；单纯丢弃句柄不注销
#[test]
fn unwatch_is_explicit_and_dropping_the_handle_keeps_the_subscription() {
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;

    let dir = tempdir().unwrap();
    let db = Db::open(temp_db(dir.path(), "watch.db"), None).unwrap();

    let a = Arc::new(AtomicUsize::new(0));
    let b = Arc::new(AtomicUsize::new(0));

    let ha = {
        let a = a.clone();
        db.watch(move |_| {
            a.fetch_add(1, Ordering::SeqCst);
        })
    };
    {
        let b = b.clone();
        let _hb = db.watch(move |_| {
            b.fetch_add(1, Ordering::SeqCst);
        });
        // _hb 在此处被丢弃：订阅必须仍然有效（v1.0 契约）
    }

    db.put_string("", "k", "1").unwrap();
    assert_eq!(a.load(Ordering::SeqCst), 1);
    assert_eq!(
        b.load(Ordering::SeqCst),
        1,
        "dropping the handle must not unsubscribe"
    );

    // 显式注销 ha
    ha.unwatch();
    db.put_string("", "k", "2").unwrap();
    assert_eq!(
        a.load(Ordering::SeqCst),
        1,
        "unwatch must remove the callback"
    );
    assert_eq!(b.load(Ordering::SeqCst), 2);

    // 幂等
    ha.unwatch();
    db.put_string("", "k", "3").unwrap();
    assert_eq!(a.load(Ordering::SeqCst), 1);
}

// §2.3 Flush()：compact() 的别名；只读模式下返回 LockedError
#[test]
fn flush_is_an_alias_of_compact_and_respects_read_only() {
    let dir = tempdir().unwrap();
    let p = temp_db(dir.path(), "flush.db");
    {
        let db = Db::open(&p, None).unwrap();
        db.put_string("", "k", "v").unwrap();
        db.flush().unwrap();
        db.compact().unwrap();
    }

    let ro = Db::open(&p, Some(DbOptions::default().read_only(true))).unwrap();
    assert!(ro.flush().is_err(), "flush on read-only must fail");
    assert!(ro.compact().is_err(), "compact on read-only must fail");
}

// §2.4 FileSize() / Stats()
#[test]
fn file_size_and_stats_report_live_state() {
    let dir = tempdir().unwrap();
    let p = temp_db(dir.path(), "stats.db");
    let db = Db::open(&p, None).unwrap();

    // 新库尚未落盘
    assert_eq!(db.file_size(), 0);
    let s0 = db.stats();
    assert_eq!(s0.file_size, 0);
    assert_eq!(s0.namespace_count, 0);
    assert_eq!(s0.total_entries, 0);
    assert_eq!(s0.current_max_tx_id, 0);

    db.put_string("a", "k1", "v").unwrap();
    db.put_string("a", "k2", "v").unwrap();
    db.put_string("b", "k3", "v").unwrap();

    let s1 = db.stats();
    assert!(s1.file_size > 32);
    assert_eq!(s1.file_size, db.file_size());
    assert_eq!(s1.namespace_count, 2);
    assert_eq!(s1.total_entries, 3);
    assert!(s1.current_max_tx_id >= 1);

    // stats 不得改变状态
    let s2 = db.stats();
    assert_eq!(s1, s2);

    // 空命名空间也计入 namespace_count
    db.put_string("empty", "x", "y").unwrap();
    assert!(db.clear_namespace("empty").unwrap());
    assert_eq!(db.stats().namespace_count, 3);
    assert_eq!(db.stats().total_entries, 3);
}

// §2.5 Backup()：产出可独立打开的文件，且不动源库 .bak
#[test]
fn backup_produces_a_standalone_openable_file() {
    let dir = tempdir().unwrap();
    let src = temp_db(dir.path(), "src.db");
    let dest = temp_db(dir.path(), "backup.db");

    let db = Db::open(&src, None).unwrap();
    db.put_string("ns", "a", "1").unwrap();
    db.put_string("ns", "b", "2").unwrap();
    db.put_i64("ns", "c", 42).unwrap();
    db.backup(&dest).unwrap();

    assert!(dest.exists(), "backup file must exist");
    assert!(!Path::new(&format!("{}.tmp", dest.display())).exists());

    // 备份可被独立打开且内容一致
    {
        let b = Db::open(&dest, None).unwrap();
        assert_eq!(b.get_string("ns", "a").unwrap(), Some("1".to_string()));
        assert_eq!(b.get_string("ns", "b").unwrap(), Some("2".to_string()));
        assert_eq!(b.get_i64("ns", "c").unwrap(), Some(42));
        assert_eq!(b.count("ns").unwrap(), 3);
    }

    // 源库未受影响
    assert_eq!(db.count("ns").unwrap(), 3);
    assert_eq!(db.get_string("ns", "a").unwrap(), Some("1".to_string()));

    // 覆盖已存在的目标
    db.put_string("ns", "d", "4").unwrap();
    db.backup(&dest).unwrap();
    {
        let b2 = Db::open(&dest, None).unwrap();
        assert_eq!(b2.count("ns").unwrap(), 4);
    }

    // 只读模式下允许备份（不修改源库）
    let ro = Db::open(&src, Some(DbOptions::default().read_only(true))).unwrap();
    let dest2 = temp_db(dir.path(), "backup_ro.db");
    ro.backup(&dest2).unwrap();
    let b3 = Db::open(&dest2, None).unwrap();
    assert_eq!(b3.count("ns").unwrap(), 4);
}

// §3.11 canonicalize_path：相对路径的不同写法必须是同一注册表键
#[test]
fn canonical_path_normalizes_equivalent_relative_forms() {
    let dir = tempdir().unwrap();
    let base = dir.path().to_path_buf();

    // 用 "./x.db" 打开，写入后用 "x.db"（cwd 相对）再打开必须看到同一实例
    let cwd = std::env::current_dir().unwrap();
    std::env::set_current_dir(&base).unwrap();

    let p1 = std::path::PathBuf::from("./canon.db");
    {
        let db = Db::open(&p1, None).unwrap();
        db.put_string("", "k", "v").unwrap();
    }

    let p2 = std::path::PathBuf::from("canon.db");
    let db2 = Db::open(&p2, None).unwrap();
    assert_eq!(
        db2.get_string("", "k").unwrap(),
        Some("v".to_string()),
        "both relative forms must resolve to the same registry entry"
    );

    std::env::set_current_dir(cwd).unwrap();
}

// §3.14 increment_i64 溢出按补码绕回（行为锁定）
#[test]
fn increment_overflow_wraps_in_two_complement() {
    let dir = tempdir().unwrap();
    let db = Db::open(temp_db(dir.path(), "ovf.db"), None).unwrap();
    assert_eq!(db.increment_i64("", "k", i64::MAX).unwrap(), i64::MAX);
    assert_eq!(db.increment_i64("", "k", 1).unwrap(), i64::MIN);
    assert_eq!(db.increment_i64("", "k", -1).unwrap(), i64::MAX);
}

// §3.15（D1-9 修订）readOnly 打开损坏库时不得写盘：拒绝打开而非从 .bak 恢复
#[test]
fn read_only_open_reports_corruption_without_writing() {
    let dir = tempdir().unwrap();
    let p = temp_db(dir.path(), "ro_rec.db");
    let bak = dir.path().join("ro_rec.db.bak");

    {
        let db = Db::open(&p, None).unwrap();
        db.put_string("", "k", "v1").unwrap();
        db.put_string("", "k", "v2").unwrap(); // `.bak` 现为 v1
    }
    // 破坏主库
    fs::write(&p, b"not a valid ckv file at all").unwrap();
    let primary_len = fs::metadata(&p).unwrap().len();
    let bak_len = fs::metadata(&bak).unwrap().len();

    // 默认（auto_recover_from_backup = false）：拒绝打开
    assert!(
        Db::open(&p, Some(DbOptions::default().read_only(true))).is_err(),
        "read-only open of a corrupt database must fail instead of self-healing"
    );

    // 即使显式开启开关，只读模式也不得写盘（D1-9）
    assert!(
        Db::open(
            &p,
            Some(
                DbOptions::default()
                    .read_only(true)
                    .auto_recover_from_backup(true)
            )
        )
        .is_err(),
        "read-only mode must ignore auto_recover_from_backup"
    );

    assert_eq!(
        fs::metadata(&p).unwrap().len(),
        primary_len,
        "read-only must not write"
    );
    assert_eq!(
        fs::metadata(&bak).unwrap().len(),
        bak_len,
        "read-only must not write"
    );
}
