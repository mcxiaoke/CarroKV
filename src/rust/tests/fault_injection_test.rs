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

/// Writes two generations so that `.bak` holds the first one, then flips a byte
/// inside the last frame of the primary file to break its payload CRC.
fn corrupt_second_generation(db_path: &std::path::Path, options: &DbOptions) {
    for value in ["v1", "v2"] {
        let db = Db::open(db_path, Some(options.clone())).unwrap();
        db.put_string("", "version", value).unwrap();
        db.close().unwrap();
    }

    let mut file = OpenOptions::new().read(true).write(true).open(db_path).unwrap();
    let len = file.metadata().unwrap().len();
    file.seek(SeekFrom::Start(len - 2)).unwrap();
    let mut byte = [0u8; 1];
    file.read_exact(&mut byte).unwrap();
    byte[0] ^= 0xFF; // Invert bits to break the frame CRC
    file.seek(SeekFrom::Start(len - 2)).unwrap();
    file.write_all(&byte).unwrap();
}

/// D1: a corrupt primary file must be reported as corruption by default. Before
/// D1 the engine silently replaced it with `.bak`, i.e. with the generation from
/// *before* the last rotation, discarding every write made since.
#[test]
fn test_bit_rot_is_rejected_by_default() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("bitrot_default.db");
    let bak_path = dir.path().join("bitrot_default.db.bak");

    let recovered = Arc::new(AtomicBool::new(false));
    let flag = recovered.clone();
    let options = DbOptions::new().on_warning(move |err_type, _msg| {
        if err_type == "onRecoveredFromBackup" {
            flag.store(true, Ordering::SeqCst);
        }
    });

    corrupt_second_generation(&db_path, &options);
    assert!(bak_path.exists(), "this test needs an existing previous generation");

    let db_len = fs::metadata(&db_path).unwrap().len();
    let bak_len = fs::metadata(&bak_path).unwrap().len();

    let err = Db::open(&db_path, Some(options))
        .err()
        .expect("a corrupt primary file must be refused by default, not silently replaced");

    match err {
        DbError::Corrupted(message) => assert!(
            message.contains(&bak_path.display().to_string()),
            "the error must point the operator at the recovery copy, got: {message}"
        ),
        other => panic!("Expected CorruptedError, got {:?}", other),
    }

    assert!(
        !recovered.load(Ordering::SeqCst),
        "no automatic recovery may occur while auto_recover_from_backup is disabled"
    );
    // Both generations must be left byte-for-byte as found, so that an operator
    // (or the opt-in flag) can still use them.
    assert_eq!(fs::metadata(&db_path).unwrap().len(), db_len);
    assert_eq!(fs::metadata(&bak_path).unwrap().len(), bak_len);
}

/// D1 escape hatch: with `auto_recover_from_backup = true` the legacy behaviour is
/// restored deliberately, including the `onRecoveredFromBackup` warning.
#[test]
fn test_bit_rot_heals_when_explicitly_enabled() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("bitrot_optin.db");

    let recovered = Arc::new(AtomicBool::new(false));
    let flag = recovered.clone();
    let options = DbOptions::new()
        .auto_recover_from_backup(true)
        .on_warning(move |err_type, _msg| {
            if err_type == "onRecoveredFromBackup" {
                flag.store(true, Ordering::SeqCst);
            }
        });

    corrupt_second_generation(&db_path, &options);

    let db = Db::open(&db_path, Some(options)).expect("opted-in recovery must succeed");
    assert!(recovered.load(Ordering::SeqCst), "the recovery warning must fire");
    // The previous generation held "v1"; the damaged generation held "v2".
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

/// G1: the recovery source must never be tail-truncated while it is being loaded,
/// because truncating it destroys the data it is kept for. A torn `.bak` must
/// therefore surface as an error and leave both generations untouched.
#[test]
fn test_recovery_source_is_not_truncated() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("g1.db");
    let bak_path = dir.path().join("g1.db.bak");
    let options = DbOptions::new().auto_recover_from_backup(true);

    corrupt_second_generation(&db_path, &options);
    assert!(bak_path.exists());

    // Tear the tail of `.bak`, as an interrupted rotation would.
    let torn_len = fs::metadata(&bak_path).unwrap().len() - 4;
    {
        let file = OpenOptions::new().write(true).open(&bak_path).unwrap();
        file.set_len(torn_len).unwrap();
        file.sync_all().unwrap();
    }
    let primary_len = fs::metadata(&db_path).unwrap().len();

    assert!(
        Db::open(&db_path, Some(options)).is_err(),
        "a torn recovery source cannot be used and must not be silently repaired"
    );
    assert_eq!(
        fs::metadata(&bak_path).unwrap().len(),
        torn_len,
        "`.bak` must not be truncated while being loaded (G1)"
    );
    assert_eq!(
        fs::metadata(&db_path).unwrap().len(),
        primary_len,
        "the primary file must be left exactly as found"
    );
}
