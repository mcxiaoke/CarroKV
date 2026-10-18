//! M3-11 regression: `backup()` must refuse a destination that collides with
//! the database file itself (or its `.tmp` / `.bak` siblings). Go already
//! rejected these; Rust/.NET/Dart used to happily write the snapshot over the
//! live database.

use carro_kv::{Db, DbOptions};
use tempfile::tempdir;

#[test]
fn backup_rejects_the_database_file_and_its_siblings() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("self.db");

    let db = Db::open(&path, Some(DbOptions::default())).unwrap();
    db.put_string("", "k", "v").unwrap();

    assert!(
        db.backup(&path).is_err(),
        "backup onto the db file must fail"
    );

    let tmp = std::path::PathBuf::from(format!("{}.tmp", path.display()));
    let bak = std::path::PathBuf::from(format!("{}.bak", path.display()));
    assert!(db.backup(&tmp).is_err(), "backup onto .tmp must fail");
    assert!(db.backup(&bak).is_err(), "backup onto .bak must fail");

    // 正常目标仍然可用，且产物可独立打开。
    let dest = dir.path().join("copy.db");
    db.backup(&dest).unwrap();
    db.close().unwrap();

    let copy = Db::open(&dest, None).unwrap();
    assert_eq!(
        copy.get_string("", "k").unwrap(),
        Some("v".to_string()),
        "the backup must be a valid database"
    );
    copy.close().unwrap();
}
