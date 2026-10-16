//! 写路径原子性回归测试（B1 / B2 / B3）
//!
//! 覆盖 2026-10-16 修复的三个同根因缺陷：写路径原本"先改内存 → 再 flush → flush
//! 内部才做只读/锁/磁盘预检"，导致被拒绝的写仍会留下已变更的内存状态。
//!
//! - B1：只读实例的写操作返回 LockedError 后，内存必须保持原值
//! - B2：磁盘被外部修改而预检失败时，内存必须保持原值
//! - B3：batch 中对不存在/已空的键与命名空间不得派发幻影 Watcher 事件
//!
//! Go / .NET 有同构测试，三者必须保持一致。

use ckv::{Db, DbOptions, KeyChangeOp};
use std::fs;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use tempfile::tempdir;

fn temp_path(dir: &std::path::Path, name: &str) -> std::path::PathBuf {
    dir.join(name)
}

// B1：只读实例执行任何写操作都必须返回错误，且**不得污染内存**。
#[test]
fn b1_read_only_write_must_not_mutate_memory() {
    let dir = tempdir().unwrap();
    let p = temp_path(dir.path(), "ro.db");

    {
        let db = Db::open(&p, None).unwrap();
        db.put_string("", "k", "old").unwrap();
    }

    let ro = Db::open(
        &p,
        Some(DbOptions::default().read_only(true)),
    )
    .unwrap();

    // 单键写
    assert!(ro.put_string("", "k", "HACKED").is_err());
    assert_eq!(ro.get_string("", "k").unwrap(), Some("old".to_string()));
    assert_eq!(ro.count("").unwrap(), 1);
    assert!(!ro.contains_key("", "brand_new").unwrap());

    // 其它写入口同样不得污染
    assert!(ro.put_bytes("", "raw", b"x".as_slice()).is_err());
    assert!(ro.put_i64("", "n", 1).is_err());
    assert!(ro.put_f64("", "f", 1.0).is_err());
    assert!(ro.put_bool("", "b", true).is_err());
    assert!(ro.increment_i64("", "n", 1).is_err());
    assert!(ro.delete("", "k").is_err());
    assert!(ro.drop_namespace("").is_err());
    assert!(ro.clear_namespace("").is_err());
    assert!(ro.compact().is_err());
    assert!(ro
        .batch(|b| {
            b.put_string("", "k", "HACKED")?;
            Ok(())
        })
        .is_err());

    assert_eq!(ro.count("").unwrap(), 1);
    assert_eq!(ro.get_string("", "k").unwrap(), Some("old".to_string()));

    // 磁盘文件也必须未被改动
    let reopened = Db::open(&p, None).unwrap();
    assert_eq!(reopened.get_string("", "k").unwrap(), Some("old".to_string()));
    assert_eq!(reopened.count("").unwrap(), 1);
}

// B2：磁盘被外部进程改写后，预检必须失败且不得污染内存。
#[test]
fn b2_external_modification_must_not_mutate_memory() {
    let dir = tempdir().unwrap();
    let p = temp_path(dir.path(), "ext.db");
    let other = temp_path(dir.path(), "other.db");

    let db = Db::open(&p, None).unwrap();
    db.put_string("", "a", "1").unwrap();

    // 用另一个数据库实例构造一份"外部写入"的合法文件
    {
        let o = Db::open(&other, None).unwrap();
        o.put_string("", "a", "1").unwrap();
        o.put_string("", "b", "2").unwrap();
        o.put_string("", "c", "3").unwrap();
    }

    // 外部进程直接覆盖磁盘（CKV 平时不持有 .db 句柄，这完全可以发生）
    fs::copy(&other, &p).unwrap();

    let err = db.put_string("", "x", "y").unwrap_err();
    match err {
        ckv::DbError::ConcurrentModification(_) => {}
        other => panic!("expected ConcurrentModificationError, got {:?}", other),
    }

    // 关键断言：写被拒绝，内存不得残留 "x"
    assert!(!db.contains_key("", "x").unwrap());
    assert_eq!(db.count("").unwrap(), 1);
    assert!(db.get_string("", "x").unwrap().is_none());
}

// B2 补充：锁被占用时同样不得污染内存。
#[test]
fn b2_locked_must_not_mutate_memory() {
    let dir = tempdir().unwrap();
    let p = temp_path(dir.path(), "locked.db");

    let db = Db::open(&p, None).unwrap();
    db.put_string("", "k", "old").unwrap();

    // 手工伪造一个新鲜的锁文件，模拟另一个进程正在写
    let lock_path = format!("{}.lock", p.display());
    let mut content = [0u8; 32];
    let now_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_millis() as u64;
    content[24..32].copy_from_slice(&now_ms.to_le_bytes());
    fs::write(&lock_path, content).unwrap();

    let opts = DbOptions::default().lock_timeout_ms(200);
    // 同一路径会命中进程内注册表拿到已打开的实例，这里通过新路径验证错误类型
    let p2 = temp_path(dir.path(), "locked2.db");
    {
        let d2 = Db::open(&p2, None).unwrap();
        d2.put_string("", "k", "old").unwrap();
    }
    let lock_path2 = format!("{}.lock", p2.display());
    fs::write(&lock_path2, content).unwrap();
    let db2 = Db::open(&p2, Some(opts)).unwrap();

    let err = db2.put_string("", "k", "HACKED").unwrap_err();
    match err {
        ckv::DbError::Locked(_) => {}
        other => panic!("expected LockedError, got {:?}", other),
    }
    assert_eq!(db2.get_string("", "k").unwrap(), Some("old".to_string()));
    assert_eq!(db2.count("").unwrap(), 1);

    drop(db);
}

// B3：batch 中对不存在的键/命名空间不得派发幻影事件；对真实生效的操作正常派发。
#[test]
fn b3_batch_must_not_dispatch_phantom_events() {
    let dir = tempdir().unwrap();
    let p = temp_path(dir.path(), "batch.db");
    let db = Db::open(&p, None).unwrap();

    let put_count = Arc::new(AtomicUsize::new(0));
    let del_count = Arc::new(AtomicUsize::new(0));
    let drop_count = Arc::new(AtomicUsize::new(0));
    let clear_count = Arc::new(AtomicUsize::new(0));

    let pc = put_count.clone();
    let dc = del_count.clone();
    let drc = drop_count.clone();
    let cc = clear_count.clone();
    db.watch(move |ev: &ckv::KeyChangeEvent| match ev.op {
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

    // 准备：一个含键的 ns、一个已清空的 ns、一个已删除的 ns
    db.put_string("live", "k", "v").unwrap();
    db.put_string("emptied", "k", "v").unwrap();
    assert!(db.clear_namespace("emptied").unwrap());
    db.put_string("gone", "k", "v").unwrap();
    assert!(db.drop_namespace("gone").unwrap());

    let before = (
        put_count.load(Ordering::SeqCst),
        del_count.load(Ordering::SeqCst),
        drop_count.load(Ordering::SeqCst),
        clear_count.load(Ordering::SeqCst),
    );

    // batch 里全部是 no-op：不得产生任何事件
    db.batch(|b| {
        b.delete("live", "does_not_exist")?;
        b.clear_namespace("emptied")?; // 已空
        b.drop_namespace("gone")?; // 已删
        b.clear_namespace("never_existed")?;
        b.drop_namespace("never_existed")?;
        b.delete("never_existed", "k")?;
        Ok(())
    })
    .unwrap();

    let after = (
        put_count.load(Ordering::SeqCst),
        del_count.load(Ordering::SeqCst),
        drop_count.load(Ordering::SeqCst),
        clear_count.load(Ordering::SeqCst),
    );
    assert_eq!(before, after, "no-op batch ops must not dispatch events");

    // 数据未被破坏
    assert!(db.contains_key("live", "k").unwrap());
    assert_eq!(db.count("emptied").unwrap(), 0);
    assert_eq!(db.count("gone").unwrap(), 0);

    // 真实生效的操作必须正常派发，且与单条路径语义一致
    db.batch(|b| {
        b.delete("live", "k")?;
        b.clear_namespace("emptied")?; // 已有内容
        Ok(())
    })
    .unwrap();

    // emptied 现在还是空的（clear 的是空 ns），补一个键再清
    db.put_string("emptied", "k2", "v").unwrap();
    db.batch(|b| {
        b.clear_namespace("emptied")?;
        Ok(())
    })
    .unwrap();

    let final_counts = (
        put_count.load(Ordering::SeqCst),
        del_count.load(Ordering::SeqCst),
        drop_count.load(Ordering::SeqCst),
        clear_count.load(Ordering::SeqCst),
    );
    // put: k2 = 1; del: live.k = 1; drop: gone 未再触发 = 0; clear: emptied = 1
    assert_eq!(final_counts.1, before.1 + 1, "real delete must dispatch");
    assert_eq!(final_counts.3, before.3 + 1, "real clear must dispatch");
    assert_eq!(final_counts.2, before.2, "no drop happened in effective batch");
}
