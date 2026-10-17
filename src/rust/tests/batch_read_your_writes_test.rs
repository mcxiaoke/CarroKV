//! Read-your-own-writes inside `batch()`.
//!
//! The batch sees, for each key: the most recent staged operation if this batch
//! touched the key, otherwise the committed database state. A staged tombstone
//! (delete / clear_namespace / drop_namespace) wins over the committed value.

use ckv::{Batch, Db, DbOptions, StorageEngineMode};
use tempfile::tempdir;

#[test]
fn staged_writes_are_visible_inside_the_batch() -> Result<(), Box<dyn std::error::Error>> {
    let dir = tempdir()?;
    let db = Db::open(dir.path().join("ryow.db"), None)?;

    db.put_string("", "a", "committed")?;
    db.put_string("", "b", "committed-b")?;

    db.batch(|batch| {
        // Untouched key: falls through to the committed state.
        assert_eq!(batch.get_bytes("", b"a")?.unwrap(), b"committed");
        assert_eq!(batch.get_string("", b"b")?.unwrap(), "committed-b");

        // Staged put is visible immediately, and shadows the committed value.
        batch.put_string("", "a", "staged")?;
        assert_eq!(batch.get_string("", b"a")?.unwrap(), "staged");

        // A second staged put wins over the first.
        batch.put_string("", "a", "staged-2")?;
        assert_eq!(batch.get_string("", b"a")?.unwrap(), "staged-2");

        // Staged delete hides the committed value.
        batch.delete("", "b")?;
        assert!(batch.get_bytes("", b"b")?.is_none());

        // A brand new key never committed is visible too.
        batch.put_i64("", "n", 42)?;
        assert_eq!(batch.get_bytes("", b"n")?.unwrap().len(), 8);

        Ok(())
    })?;

    assert_eq!(db.get_string("", "a")?.unwrap(), "staged-2");
    assert!(db.get_bytes("", "b")?.is_none());
    assert_eq!(db.get_i64("", "n")?.unwrap(), 42);
    Ok(())
}

#[test]
fn clear_and_drop_namespace_hide_everything_staged_before_them(
) -> Result<(), Box<dyn std::error::Error>> {
    let dir = tempdir()?;
    let db = Db::open(dir.path().join("ryow_ns.db"), None)?;

    db.put_string("users", "u1", "alice")?;

    db.batch(|batch| {
        // Staged put, then clear: clear wins regardless of operation order.
        batch.put_string("users", "u2", "bob")?;
        assert_eq!(batch.get_string("users", b"u2")?.unwrap(), "bob");

        batch.clear_namespace("users")?;
        assert!(batch.get_bytes("users", b"u1")?.is_none());
        assert!(batch.get_bytes("users", b"u2")?.is_none());

        // A put staged *after* the clear is visible again.
        batch.put_string("users", "u3", "carol")?;
        assert_eq!(batch.get_string("users", b"u3")?.unwrap(), "carol");

        // Other namespaces are unaffected by the clear.
        batch.put_string("other", "k", "v")?;
        assert_eq!(batch.get_string("other", b"k")?.unwrap(), "v");

        Ok(())
    })?;

    assert_eq!(db.count("users")?, 1);
    assert_eq!(db.get_string("users", "u3")?.unwrap(), "carol");
    Ok(())
}

#[test]
fn drop_namespace_hides_staged_and_committed_keys() -> Result<(), Box<dyn std::error::Error>> {
    let dir = tempdir()?;
    let db = Db::open(dir.path().join("ryow_drop.db"), None)?;

    db.batch(|batch| {
        batch.put_string("tmp", "k", "v")?;
        assert_eq!(batch.get_string("tmp", b"k")?.unwrap(), "v");

        batch.drop_namespace("tmp")?;
        assert!(batch.get_bytes("tmp", b"k")?.is_none());
        Ok(())
    })?;

    assert_eq!(db.count("tmp")?, 0);
    Ok(())
}

#[test]
fn standalone_batch_sees_only_its_own_staging() {
    let mut batch = Batch::new();
    batch.put_string("", "k", "v").unwrap();

    assert_eq!(batch.get_string("", b"k").unwrap().unwrap(), "v");
    // No database attached: an untouched key is simply absent.
    assert!(batch.get_bytes("", b"missing").unwrap().is_none());
}

#[test]
fn read_your_own_writes_in_wal_mode() -> Result<(), Box<dyn std::error::Error>> {
    let dir = tempdir()?;
    let options = DbOptions::default().engine_mode(StorageEngineMode::AppendOnlyLog);
    let db = Db::open(dir.path().join("ryow_wal.db"), Some(options))?;

    db.put_string("", "counter", "1")?;

    db.batch(|batch| {
        let current = batch.get_string("", b"counter")?.unwrap();
        assert_eq!(current, "1");
        batch.put_string("", "counter", "2")?;
        assert_eq!(batch.get_string("", b"counter")?.unwrap(), "2");
        Ok(())
    })?;

    assert_eq!(db.get_string("", "counter")?.unwrap(), "2");
    Ok(())
}
