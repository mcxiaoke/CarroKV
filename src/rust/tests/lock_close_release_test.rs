//! FIX-02 regression: the connection lock must behave like a *connection*
//! resource, not like a handle-lifetime resource.
//!
//! Two defects are covered:
//!
//! * `close()` used to leave the kernel lock held until the last `Db` handle was
//!   dropped, so a caller that closed the database and immediately reopened it
//!   waited for the full `lock_timeout_ms` (and in practice was handed back the
//!   same closed instance by the in-process registry);
//! * on Windows, a holder that opened `<db>.lock` with `FileShare.None` made
//!   `CreateFile` itself fail, and because the open happened outside the retry
//!   loop the caller got an `IoError` immediately instead of `Locked` after the
//!   timeout.

use carro_kv::{Db, DbError, DbOptions};
use std::fs::OpenOptions;
use std::time::{Duration, Instant};
use tempfile::tempdir;

fn short_timeout(ms: u32) -> DbOptions {
    DbOptions::default().lock_timeout_ms(ms)
}

fn lock_file(db_path: &std::path::Path) -> std::path::PathBuf {
    std::path::PathBuf::from(format!("{}.lock", db_path.display()))
}

#[test]
fn close_releases_the_kernel_lock_without_dropping_the_handle() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("close.db");

    let db = Db::open(&path, None).unwrap();
    db.put_string("", "k", "v").unwrap();
    db.close().unwrap();

    // `db` is deliberately kept alive: the lock must be released by close(), not by
    // Drop. A foreign handle that manages to lock the file proves the kernel lock is
    // gone.
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .open(lock_file(&path))
        .unwrap();
    file.try_lock()
        .expect("close() must release the inter-process lock");

    // Writes through the closed handle are refused instead of silently writing to a
    // database whose lock has been handed over.
    let err = db.put_string("", "k2", "v2").unwrap_err();
    assert!(matches!(err, DbError::Locked(_)), "got {err:?}");
    // close() is idempotent.
    db.close().unwrap();
}

#[test]
fn reopen_after_close_succeeds_immediately_and_persists() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("reopen.db");

    let db = Db::open(&path, None).unwrap();
    db.put_string("", "before", "1").unwrap();
    db.close().unwrap();

    // Short timeout: if close() had leaked the lock (or the registry handed back the
    // closed instance) this open would take ~2s and/or fail.
    let started = Instant::now();
    let reopened = Db::open(&path, Some(short_timeout(2_000))).unwrap();
    assert!(
        started.elapsed() < Duration::from_millis(500),
        "reopen after close took {:?}",
        started.elapsed()
    );

    reopened.put_string("", "after", "2").unwrap();
    reopened.close().unwrap();

    let verify = Db::open(&path, None).unwrap();
    assert_eq!(verify.get_string("", "before").unwrap(), Some("1".into()));
    assert_eq!(verify.get_string("", "after").unwrap(), Some("2".into()));
}

/// Windows-only: `.NET` holds `<db>.lock` with `FileShare.None`, which makes the
/// open itself fail with `ERROR_SHARING_VIOLATION` (32). That must surface as
/// `Locked` after `lock_timeout_ms`, not as an immediate `IoError`.
#[cfg(windows)]
#[test]
fn windows_sharing_violation_is_reported_as_locked_after_the_timeout() {
    use std::os::windows::fs::OpenOptionsExt;

    let dir = tempdir().unwrap();
    let path = dir.path().join("share.db_share_violation.db");
    let holder = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .share_mode(0) // deny every other open, like FileShare.None
        .open(lock_file(&path))
        .unwrap();

    let started = Instant::now();
    let err = match Db::open(&path, Some(short_timeout(200))) {
        Ok(_) => panic!("open must fail while another handle denies sharing"),
        Err(err) => err,
    };
    let elapsed = started.elapsed();

    assert!(
        matches!(err, DbError::Locked(_)),
        "expected Locked after the timeout, got {err:?}"
    );
    assert!(
        elapsed >= Duration::from_millis(150),
        "lock_timeout_ms was bypassed: failed after {elapsed:?}"
    );

    // Once the holder goes away the database opens normally.
    drop(holder);
    Db::open(&path, Some(short_timeout(2_000)))
        .expect("open must succeed once the sharing violation is gone")
        .close()
        .unwrap();
}
