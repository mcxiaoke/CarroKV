//! Regression tests for the connection lock (C9, threshold T10).
//!
//! Since C9 the lock is a **kernel** lock on `<db>.lock` (`flock` on Unix,
//! `LockFileEx` on Windows, via `std::fs::File::try_lock`), acquired by `open` and
//! held for the whole lifetime of the connection:
//!
//! * the lock file's *contents* are irrelevant — the old 32-byte
//!   `PID | UUID | CreateTimeMs` record, its clock-based staleness judgement and
//!   the whole "steal the stale lock" dance are gone, so a leftover or malformed
//!   lock file must cost nothing at all;
//! * a live holder can never be mistaken for a dead one (which used to put two
//!   writers on one database);
//! * a crashed holder releases the lock by dying, because the kernel owns it.
//!
//! Writable connections take the lock exclusively, read-only ones share it:
//! readers exclude writers but not each other (bbolt's `flock(db, !readOnly)`).

use ckv::{Db, DbError, DbOptions};
use std::fs::{self, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};
use tempfile::tempdir;

/// A real kernel lock on `<db>.lock`, standing in for another connection.
struct Holder {
    file: std::fs::File,
}

impl Holder {
    fn exclusive(db_path: &Path) -> Self {
        let file = Self::open_lock_file(db_path);
        file.try_lock().expect("holder must be able to lock");
        Holder { file }
    }

    fn shared(db_path: &Path) -> Self {
        let file = Self::open_lock_file(db_path);
        file.try_lock_shared().expect("holder must be able to lock");
        Holder { file }
    }

    fn open_lock_file(db_path: &Path) -> std::fs::File {
        OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(format!("{}.lock", db_path.display()))
            .unwrap()
    }
}

impl Drop for Holder {
    fn drop(&mut self) {
        let _ = self.file.unlock();
    }
}

fn short_timeout() -> DbOptions {
    DbOptions::default().lock_timeout_ms(150)
}

/// The lock file's contents no longer take part in the protocol: a malformed,
/// ancient-looking lock file must be ignored, not waited on, not stolen, and not
/// deleted.
#[test]
fn lock_file_contents_are_irrelevant() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("garbage_lock.db");
    let lock_path = format!("{}.lock", db_path.display());

    let garbage = b"not a 32-byte lock record: no pid, no uuid, no timestamp".to_vec();
    fs::write(&lock_path, &garbage).unwrap();

    let started = Instant::now();
    let db = Db::open(&db_path, None).expect("a leftover lock file must not block a free lock");
    db.put_string("", "k", "v").unwrap();
    assert!(
        started.elapsed() < Duration::from_secs(2),
        "the open took {:?}: the lock file's contents are still being interpreted",
        started.elapsed()
    );

    // The marker file is never deleted (unlinking it would let a second writer lock
    // a different inode while the first still holds the old one).
    drop(db);
    assert!(Path::new(&lock_path).exists(), "the lock file must not be unlinked");
    assert_eq!(fs::read(&lock_path).unwrap(), garbage, "the lock file must not be rewritten");
}

/// A writable connection holds the lock exclusively: neither a second writer nor a
/// reader can open the database while it lives; once it is dropped the lock is free.
#[test]
fn exclusive_lock_excludes_writers_and_readers() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("exclusive.db");
    let holder = Holder::exclusive(&db_path);

    match Db::open(&db_path, Some(short_timeout())) {
        Err(DbError::Locked(_)) => {}
        other => panic!("a second writer must be rejected, got {:?}", other.map(|_| "Ok(db)")),
    }

    let read_only = DbOptions::default().read_only(true).lock_timeout_ms(150);
    match Db::open(&db_path, Some(read_only)) {
        Err(DbError::Locked(_)) => {}
        other => panic!("a reader must be excluded by a writer, got {:?}", other.map(|_| "Ok(db)")),
    }

    drop(holder);
    let db = Db::open(&db_path, None).expect("the lock must be free after the holder is gone");
    db.put_string("", "k", "v").unwrap();
}

/// A read-only connection shares the lock: another reader may open the database, a
/// writer may not.
#[test]
fn shared_lock_allows_readers_but_not_writers() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("shared.db");
    {
        // seed so there is a real database to read
        let db = Db::open(&db_path, None).unwrap();
        db.put_string("", "k", "v").unwrap();
    }

    let holder = Holder::shared(&db_path);

    // A reader coexists with the shared holder and sees... nothing yet: the holder
    // never wrote a database. The point is that the open itself succeeds.
    {
        let reader = Db::open(&db_path, Some(DbOptions::default().read_only(true)))
            .expect("a reader must coexist with another reader");
        // Dropping the reader unregisters it, so the next attempt really re-opens.
        drop(reader);
    }

    match Db::open(&db_path, Some(short_timeout())) {
        Err(DbError::Locked(_)) => {}
        other => panic!("a writer must be excluded by a reader, got {:?}", other.map(|_| "Ok(db)")),
    }

    drop(holder);

    // Negative control: with the holder gone the write succeeds and the seed is intact.
    let db = Db::open(&db_path, None).unwrap();
    assert_eq!(db.get_string("", "k").unwrap(), Some("v".to_string()));
}

// ---------- T10：跨进程 ----------

/// T10: a holder in *another process* cannot be stolen from while it lives, and its
/// lock is released by the operating system when the process dies.
#[test]
fn t10_live_holder_blocks_and_a_killed_holder_frees_the_lock() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t10.db");

    let mut child = spawn_lock_holder(&db_path);
    wait_until_locked(&mut child);

    match Db::open(&db_path, Some(short_timeout())) {
        Err(DbError::Locked(_)) => {}
        other => panic!(
            "a live holder in another process must not be stolen from, got {:?}",
            other.map(|_| "Ok(db)")
        ),
    }

    // Kill the holder: the kernel releases the lock, so the next open succeeds
    // without any staleness heuristic or lock stealing.
    let _ = child.kill();
    let _ = child.wait();

    let started = Instant::now();
    let db = Db::open(&db_path, Some(DbOptions::default().lock_timeout_ms(2_000)))
        .expect("the kernel must release the lock when the holder dies");
    assert!(
        started.elapsed() < Duration::from_secs(2),
        "reopening took {:?}: the lock was not released by the kernel",
        started.elapsed()
    );
    db.put_string("", "k", "v").unwrap();
}

// ---------- 子进程辅助 ----------

/// Child-process half of T10. It is `#[ignore]`d so a normal `cargo test` run skips
/// it, and is executed explicitly by [`spawn_lock_holder`].
#[test]
#[ignore]
fn lock_holder_child() {
    let path = std::env::var("CKV_TEST_LOCK_HOLDER_DB").expect("CKV_TEST_LOCK_HOLDER_DB");
    let _db = Db::open(&path, None).expect("child must be able to open the database");
    println!("LOCKED");
    let _ = std::io::stdout().flush();
    // Hold the lock until the parent kills this process.
    std::thread::sleep(Duration::from_secs(30));
}

fn spawn_lock_holder(db_path: &Path) -> Child {
    let exe = std::env::current_exe().expect("test binary path");
    Command::new(exe)
        .args(["lock_holder_child", "--exact", "--ignored", "--nocapture"])
        .env("CKV_TEST_LOCK_HOLDER_DB", db_path)
        .stdout(Stdio::piped())
        .spawn()
        .expect("spawning the lock holder must work")
}

fn wait_until_locked(child: &mut Child) {
    let stdout = child.stdout.take().expect("child stdout");
    let mut reader = BufReader::new(stdout);
    let mut line = String::new();
    loop {
        line.clear();
        match reader.read_line(&mut line) {
            Ok(0) => panic!("the lock holder exited before taking the lock"),
            Ok(_) if line.contains("LOCKED") => break,
            Ok(_) => continue,
            Err(err) => panic!("reading the lock holder's output failed: {err}"),
        }
    }
    // Keep draining in the background so the child can never block on a full pipe.
    std::thread::spawn(move || {
        let mut sink = String::new();
        loop {
            sink.clear();
            match reader.read_line(&mut sink) {
                Ok(0) | Err(_) => break,
                Ok(_) => {}
            }
        }
    });
}
