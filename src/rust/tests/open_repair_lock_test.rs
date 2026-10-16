//! Regression tests for the *open* path's two remaining silent-failure modes.
//!
//! Covered defects:
//!
//! * **C10** — tail healing during open used to run without the inter-process
//!   file lock, so a second process could truncate frames that another process
//!   had just appended. Tail repair now happens under `FileLock`, which this
//!   suite proves by making the open *fail* (`Locked`) while a live lock is held,
//!   and by asserting the damaged file was left byte-for-byte untouched.
//! * **D1-7 / D1-8** — a missing primary next to a `.bak` used to be "repaired" by
//!   promoting that (older) generation, and if the `.bak` itself was unreadable
//!   the open silently succeeded as an *empty* database. Both are now refused
//!   unless the caller explicitly opts into `.bak` recovery.

use ckv::{Db, DbError, DbOptions, StorageEngineMode, SyncMode};
use std::fs::{self, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
use tempfile::tempdir;

const FILE_HEADER_LEN: u64 = 32;

/// Bytes appended to simulate a crash in the middle of a frame write: fewer than
/// the 28-byte frame header, so the file ends *inside* the last frame.
const TORN_TAIL: &[u8] = &[0x4B, 0x56, 0x46, 0x01, 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22];

fn wal_options() -> DbOptions {
    DbOptions::default()
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Immediate)
        .compaction_threshold_bytes(u64::MAX - 1)
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as u64
}

/// Stands in for "another process holds the lock": since C9 the lock is a kernel
/// lock (`flock` / `LockFileEx`) held for the lifetime of a connection, so the
/// holder must take a *real* lock on `<db>.lock` rather than merely forge the old
/// 32-byte lock file (which is now just an empty marker file).
struct LiveLockHolder {
    file: std::fs::File,
}

impl LiveLockHolder {
    fn new(db_path: &Path) -> Self {
        let path = format!("{}.lock", db_path.display());
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&path)
            .unwrap();
        file.try_lock()
            .expect("the test itself must be able to take the lock first");
        LiveLockHolder { file }
    }
}

impl Drop for LiveLockHolder {
    fn drop(&mut self) {
        let _ = self.file.unlock();
    }
}

fn file_len(path: &Path) -> u64 {
    fs::metadata(path).unwrap().len()
}

/// Appends a partial frame to a healthy log, producing a genuine torn tail.
fn append_torn_tail(path: &Path) {
    let mut file = OpenOptions::new().append(true).open(path).unwrap();
    file.write_all(TORN_TAIL).unwrap();
    file.sync_all().unwrap();
}

/// Flips one byte at `offset` (simulated media damage).
fn flip_byte(path: &Path, offset: u64) {
    let mut file = OpenOptions::new().read(true).write(true).open(path).unwrap();
    file.seek(SeekFrom::Start(offset)).unwrap();
    let mut byte = [0u8; 1];
    file.read_exact(&mut byte).unwrap();
    byte[0] ^= 0xFF;
    file.seek(SeekFrom::Start(offset)).unwrap();
    file.write_all(&byte).unwrap();
    file.sync_all().unwrap();
}

/// Writes two generations in Snapshot mode so that `.bak` holds the first one.
fn write_two_generations(db_path: &Path) -> DbOptions {
    let opts = DbOptions::default();
    for value in ["v1", "v2"] {
        let db = Db::open(db_path, Some(opts.clone())).unwrap();
        db.put_string("", "version", value).unwrap();
        db.close().unwrap();
    }
    let bak = PathBuf::from(format!("{}.bak", db_path.display()));
    assert!(bak.exists(), "this scenario needs a `.bak` generation");
    opts
}

fn bak_path(db_path: &Path) -> PathBuf {
    PathBuf::from(format!("{}.bak", db_path.display()))
}

// ---------- C10：残尾自愈必须持有文件锁 ----------

/// T11: repairing a torn tail at open must hold the inter-process lock.
///
/// With a live lock held by "another process", the open must fail with `Locked`
/// and — crucially — must **not** have truncated the log. An unlocked repair
/// would silently succeed here and discard whatever a concurrent appender had
/// just written at the tail.
#[test]
fn t11_tail_repair_requires_the_file_lock() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("t11.db");
    let opts = wal_options();

    {
        let db = Db::open(&db_path, Some(opts.clone())).unwrap();
        db.put_string("", "a", "1").unwrap();
        db.put_string("", "b", "2").unwrap();
        db.put_string("", "c", "3").unwrap();
        db.close().unwrap();
    }

    let healthy_len = file_len(&db_path);
    append_torn_tail(&db_path);
    let damaged_len = file_len(&db_path);
    assert_eq!(damaged_len, healthy_len + TORN_TAIL.len() as u64);

    let holder = LiveLockHolder::new(&db_path);
    let lock_timeout = DbOptions::default()
        .lock_timeout_ms(100)
        .engine_mode(StorageEngineMode::AppendOnlyLog)
        .sync_mode(SyncMode::Immediate)
        .compaction_threshold_bytes(u64::MAX - 1);

    let err = Db::open(&db_path, Some(lock_timeout))
        .err()
        .expect("tail repair must not proceed without the file lock (C10)");
    assert!(
        matches!(err, DbError::Locked(_)),
        "expected LockedError while another process holds the lock, got {err:?}"
    );
    assert_eq!(
        file_len(&db_path),
        damaged_len,
        "the refused open must not have truncated the log"
    );

    // Once the holder is gone the same open heals the tail as before.
    drop(holder);
    let _ = fs::remove_file(format!("{}.lock", db_path.display()));

    let db = Db::open(&db_path, Some(opts)).expect("a torn tail must still self-heal");
    assert_eq!(db.get_string("", "a").unwrap(), Some("1".to_string()));
    assert_eq!(db.get_string("", "b").unwrap(), Some("2".to_string()));
    assert_eq!(db.get_string("", "c").unwrap(), Some("3".to_string()));
    assert_eq!(
        file_len(&db_path),
        healthy_len,
        "the healed log must end at the last complete frame"
    );
}

// ---------- D1-7 / D1-8：主库缺失时的分支 ③ ----------

/// A missing primary next to a *healthy* `.bak` is not a fresh database, so the
/// default open refuses it, leaves both files untouched, and names the copy an
/// operator can restore from.
#[test]
fn missing_primary_with_healthy_backup_is_refused_by_default() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("d17.db");
    let opts = write_two_generations(&db_path);
    let bak = bak_path(&db_path);
    let bak_bytes = fs::read(&bak).unwrap();

    fs::remove_file(&db_path).unwrap();

    let err = Db::open(&db_path, Some(opts))
        .err()
        .expect("a missing primary with a `.bak` must not be silently promoted (D1-7)");
    match err {
        DbError::Corrupted(msg) => assert!(
            msg.contains(".bak"),
            "the error must point at the offline copy, got: {msg}"
        ),
        other => panic!("expected CorruptedError, got {other:?}"),
    }

    assert!(!db_path.exists(), "a refused open must not create a database");
    assert_eq!(
        fs::read(&bak).unwrap(),
        bak_bytes,
        "the offline copy must be left untouched"
    );
}

/// The explicit opt-in restores the legacy promotion, with the existing warning
/// code (`onRecoveredFromBackup`).
#[test]
fn missing_primary_with_healthy_backup_is_promoted_when_explicitly_enabled() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("d17_opt_in.db");
    let mut opts = write_two_generations(&db_path);
    let warnings = std::sync::Arc::new(std::sync::Mutex::new(Vec::<String>::new()));
    let sink = warnings.clone();
    opts = opts
        .auto_recover_from_backup(true)
        .on_warning(move |code, _| sink.lock().unwrap().push(code.to_string()));

    fs::remove_file(&db_path).unwrap();

    let db = Db::open(&db_path, Some(opts)).expect("explicit opt-in must recover");
    assert!(db_path.exists(), "the `.bak` must have been promoted");
    assert_eq!(db.get_string("", "version").unwrap(), Some("v1".to_string()));
    assert!(
        warnings
            .lock()
            .unwrap()
            .iter()
            .any(|c| c == "onRecoveredFromBackup"),
        "promotion must keep reporting onRecoveredFromBackup"
    );
}

/// D1-8: a missing primary with an **unreadable** `.bak` used to open as an empty
/// database, hiding the loss of both generations. It must now be refused.
#[test]
fn missing_primary_with_damaged_backup_is_not_opened_as_an_empty_database() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("d18.db");
    let opts = write_two_generations(&db_path);
    let bak = bak_path(&db_path);

    // Damage a frame body inside the `.bak` (past the 32-byte file header).
    flip_byte(&bak, FILE_HEADER_LEN + 12);
    fs::remove_file(&db_path).unwrap();

    let err = Db::open(&db_path, Some(opts))
        .err()
        .expect("an unreadable `.bak` next to a missing primary must be refused (D1-8)");
    match err {
        DbError::Corrupted(msg) => assert!(
            msg.contains("could not be loaded"),
            "the error must explain that the offline copy is unusable, got: {msg}"
        ),
        other => panic!("expected CorruptedError, got {other:?}"),
    }
    assert!(
        !db_path.exists(),
        "a refused open must not leave a fresh empty database behind"
    );
}

/// Negative control: with no `.bak` at all the same path is a genuinely new
/// database and must keep opening as one.
#[test]
fn missing_primary_without_backup_still_opens_as_a_new_database() {
    let dir = tempdir().unwrap();
    let db_path = dir.path().join("fresh.db");
    assert!(!bak_path(&db_path).exists());

    let db = Db::open(&db_path, None).unwrap();
    db.put_string("", "k", "v").unwrap();
    assert_eq!(db.get_string("", "k").unwrap(), Some("v".to_string()));
}
