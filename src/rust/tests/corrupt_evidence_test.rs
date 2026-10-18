//! 规范 §3.4：AOL 尾部自愈截断前必须把整库留证到 `<db>.corrupt.<unix_ms>`，
//! 且只读模式绝不写盘（既不截断也不留证）。

use carro_kv::{Db, DbError, DbOptions, StorageEngineMode};
use std::io::Write;
use tempfile::tempdir;

fn aol(path: &std::path::Path) -> Db {
    let opts = DbOptions::default().engine_mode(StorageEngineMode::AppendOnlyLog);
    Db::open(path, Some(opts)).unwrap()
}

fn append_garbage(path: &std::path::Path) {
    let mut f = std::fs::OpenOptions::new().append(true).open(path).unwrap();
    f.write_all(&[0xFFu8; 10]).unwrap(); // < 28B：走"尾帧头不完整"的自愈分支
    f.sync_all().unwrap();
}

fn corrupt_files(path: &std::path::Path) -> Vec<std::path::PathBuf> {
    let dir = path.parent().unwrap();
    let prefix = format!("{}.corrupt.", path.file_name().unwrap().to_string_lossy());
    std::fs::read_dir(dir)
        .unwrap()
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| {
            p.file_name()
                .map(|n| n.to_string_lossy().starts_with(&prefix))
                .unwrap_or(false)
        })
        .collect()
}

#[test]
fn torn_tail_is_backed_up_before_truncation() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("a.db");

    {
        let db = aol(&path);
        db.put_string("", "k", "v").unwrap();
        db.close().unwrap();
    }
    let intact_len = std::fs::metadata(&path).unwrap().len();
    append_garbage(&path);
    let damaged = std::fs::read(&path).unwrap();
    assert_eq!(damaged.len() as u64, intact_len + 10);

    let db = aol(&path);
    assert_eq!(db.get_string("", "k").unwrap(), Some("v".to_string()));
    db.close().unwrap();

    let backups = corrupt_files(&path);
    assert_eq!(
        backups.len(),
        1,
        "exactly one .corrupt evidence file expected"
    );
    let saved = std::fs::read(&backups[0]).unwrap();
    assert_eq!(
        saved,
        damaged,
        "the evidence must be the pre-truncation content ({} bytes)",
        damaged.len()
    );
    assert!(
        std::fs::metadata(&path).unwrap().len() < saved.len() as u64,
        "the live file must have been truncated"
    );
}

#[test]
fn readonly_never_writes_evidence() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("ro.db");

    {
        let db = aol(&path);
        db.put_string("", "k", "v").unwrap();
        db.close().unwrap();
    }
    append_garbage(&path);
    let before = std::fs::read(&path).unwrap();

    let opts = DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .read_only(true);
    // 只读不做自愈（allow_tail_truncation=false），残尾被判成 Corrupted 是既有设计；
    // 关键是它**既不留证也不截断**。
    let err = match Db::open(&path, Some(opts)) {
        Ok(_) => panic!("read-only must reject a torn tail"),
        Err(e) => e,
    };
    assert!(matches!(err, DbError::Corrupted(_)));

    assert!(
        corrupt_files(&path).is_empty(),
        "read-only must not write evidence"
    );
    assert_eq!(
        std::fs::read(&path).unwrap(),
        before,
        "read-only must not truncate"
    );
}
