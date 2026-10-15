use ckv::{Db, DbError, DbOptions};
use std::fs::{self, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tempfile::tempdir;

#[test]
fn test_reentrant_open_shares_instance() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("reentrant.db");

    let db1 = Db::open(&db_path, None).unwrap();
    db1.put_string("", "foo", "bar").unwrap();

    // Open same canonical path again from another place
    let db2 = Db::open(&db_path, None).unwrap();
    // Memory state should be immediately visible because they share the same inner Arc
    assert_eq!(db2.get_string("", "foo").unwrap(), Some("bar".to_string()));

    db2.put_string("", "foo", "updated_by_db2").unwrap();
    assert_eq!(db1.get_string("", "foo").unwrap(), Some("updated_by_db2".to_string()));
}

#[test]
fn test_crash_recovery_promote_tmp() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("crash_test.db");
    let tmp_path = dir.path().join("crash_test.db.tmp");

    // Simulate scenario: write finished to .tmp, but process killed before rename to .db
    {
        let db = Db::open(&tmp_path, None).unwrap();
        db.put_string("", "recovered_key", "valid_data").unwrap();
    }
    // Rename tmp_path.db to crash_test.db.tmp
    fs::rename(dir.path().join("crash_test.db.tmp"), &tmp_path).unwrap();
    assert!(!db_path.exists());
    assert!(tmp_path.exists());

    // When opening db_path, it should automatically detect and promote the intact .tmp file
    let db = Db::open(&db_path, None).unwrap();
    assert_eq!(db.get_string("", "recovered_key").unwrap(), Some("valid_data".to_string()));
    assert!(db_path.exists());
    assert!(!tmp_path.exists());
}

#[test]
fn test_bit_rot_heals_from_backup() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("heal_test.db");
    let bak_path = dir.path().join("heal_test.db.bak");

    let warning_triggered = Arc::new(AtomicBool::new(false));
    let warning_clone = warning_triggered.clone();

    let options = DbOptions::new().on_warning(move |err_type, _msg| {
        if err_type == "onRecoveredFromBackup" {
            warning_clone.store(true, Ordering::SeqCst);
        }
    });

    // 1. Write state 1
    {
        let db = Db::open(&db_path, Some(options.clone())).unwrap();
        db.put_string("", "version", "v1").unwrap();
    }

    // 2. Write state 2 (v1 rotates to .bak, v2 becomes primary)
    {
        let db = Db::open(&db_path, Some(options.clone())).unwrap();
        db.put_string("", "version", "v2").unwrap();
    }

    assert!(db_path.exists());
    assert!(bak_path.exists());

    // 3. Deliberately corrupt primary file (flip 1 byte in payload to break CRC)
    {
        let mut file = OpenOptions::new().read(true).write(true).open(&db_path).unwrap();
        let len = file.metadata().unwrap().len();
        file.seek(SeekFrom::Start(len - 2)).unwrap();
        let mut b = [0u8; 1];
        file.read_exact(&mut b).unwrap();
        b[0] ^= 0xFF; // Invert bits
        file.seek(SeekFrom::Start(len - 2)).unwrap();
        file.write_all(&b).unwrap();
    }

    // 4. Opening primary file should fail integrity check, and automatically heal from .bak!
    let db = Db::open(&db_path, Some(options)).unwrap();
    assert!(warning_triggered.load(Ordering::SeqCst));

    // Data should be recovered to v1 (last known good state from .bak)
    assert_eq!(db.get_string("", "version").unwrap(), Some("v1".to_string()));
}

#[test]
fn test_dual_corruption_aborts_without_wiping() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("dual_corrupt.db");
    let bak_path = dir.path().join("dual_corrupt.db.bak");

    // Write state 1 and 2
    {
        let db = Db::open(&db_path, None).unwrap();
        db.put_string("", "user", "Alice").unwrap();
    }
    {
        let db = Db::open(&db_path, None).unwrap();
        db.put_string("", "user", "Bob").unwrap();
    }

    // Corrupt BOTH .db and .bak
    for p in &[&db_path, &bak_path] {
        let mut file = OpenOptions::new().read(true).write(true).open(p).unwrap();
        let len = file.metadata().unwrap().len();
        file.seek(SeekFrom::Start(len - 2)).unwrap();
        file.write_all(&[0x00]).unwrap();
    }

    // Must reject opening and NOT silently overwrite with empty database!
    let res = Db::open(&db_path, None);
    assert!(res.is_err());
    match res.err().unwrap() {
        DbError::Corrupted(_) => {}
        other => panic!("Expected CorruptedError, got {:?}", other),
    }
}
