//! Regression test for the snapshot rotation (`C5`) at the integration level.
//!
//! The rotation renames the live database onto `.bak` and then the freshly
//! serialised `.tmp` onto the database. `rename` already replaces an existing
//! destination on POSIX and on Windows (Rust passes `MOVEFILE_REPLACE_EXISTING`),
//! so nothing needs to be removed first.
//!
//! The primitive's own invariant -- *never remove the destination before the
//! rename has succeeded* -- is asserted by the unit test
//! `db::rotation_tests::rename_never_removes_the_destination` (it needs control
//! over the source path, which the public API cannot express). What is left here
//! is the end-to-end half: a successful rotation still replaces `.bak` with the
//! previous generation in full, and `.bak` is always a complete, openable
//! database rather than a half-written file (T17).

use ckv::{Db, DbOptions};
use std::fs;
use tempfile::tempdir;

#[test]
fn successful_rotation_replaces_bak_with_a_complete_generation() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("ok.db");
    let bak_path = dir.path().join("ok.db.bak");

    {
        let db = Db::open(&db_path, Some(DbOptions::default())).unwrap();
        db.put_string("", "k", "v1").unwrap();
    }
    let gen1_len = fs::metadata(&db_path).unwrap().len();

    {
        let db = Db::open(&db_path, Some(DbOptions::default())).unwrap();
        db.put_string("", "k", "v2").unwrap();
    }

    assert!(bak_path.exists(), "the rotation must produce `.bak`");
    assert_eq!(
        fs::metadata(&bak_path).unwrap().len(),
        gen1_len,
        "`.bak` must hold the previous generation in full"
    );

    // T17 guard: `.bak` must be a complete, openable database, never a
    // half-written one.
    let copy = dir.path().join("gen1_copy.db");
    fs::copy(&bak_path, &copy).unwrap();
    let restored = Db::open(&copy, Some(DbOptions::default())).unwrap();
    assert_eq!(
        restored.get_string("", "k").unwrap(),
        Some("v1".to_string())
    );
    assert!(
        !dir.path().join("ok.db.bak.stage").exists(),
        "no staging leftovers"
    );

    // The live database keeps the newest generation, and every rotation after this
    // one keeps replacing `.bak` in place: repeated rotations must never leave the
    // directory without a previous generation.
    for i in 3..8 {
        let db = Db::open(&db_path, Some(DbOptions::default())).unwrap();
        db.put_string("", "k", format!("v{i}")).unwrap();
        assert!(bak_path.exists(), "rotation {i} left no `.bak` behind");
    }
}
