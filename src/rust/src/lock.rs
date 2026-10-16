use crate::errors::{DbError, Result};
use crate::options::DbOptions;
use std::fs::{File, OpenOptions, TryLockError};
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant};

/// An inter-process lock on a database file, held for the **whole lifetime of the
/// connection** (C9).
///
/// The lock is a *kernel* lock on `<db>.lock` — `flock(2)` on Unix, `LockFileEx` on
/// Windows, both reached through [`std::fs::File::try_lock`] — rather than a
/// protocol built on the lock file's contents. That removes two whole classes of
/// failure the old 32-byte protocol had:
///
/// * a clock-based staleness judgement could declare a *live* writer dead and steal
///   its lock (a long GC pause, swapping or a big snapshot was enough), putting two
///   writers on one database;
/// * a crashed writer could leave a lock file behind that the next process had to
///   reason about.
///
/// The kernel releases the lock when the process exits, so a crashed writer can
/// never keep the database locked, and a live writer can never be mistaken for a
/// dead one. This is what bbolt (`flock(db, !readOnly, timeout)` at `Open`, held for
/// the connection) and LMDB (advisory `fcntl` lock, released by the OS) do; neither
/// has any liveness probing or lock stealing.
///
/// Writable connections take the lock exclusively, read-only connections share it:
/// readers exclude writers but not each other.
pub struct FileLock {
    path: PathBuf,
    file: Option<File>,
}

impl FileLock {
    /// Acquires the lock, waiting up to `options.lock_timeout_ms` for another
    /// connection to release it.
    pub fn acquire(db_path: &Path, options: &DbOptions) -> Result<Self> {
        let lock_path = PathBuf::from(format!("{}.lock", db_path.display()));

        // The lock file is created once and deliberately **never deleted**:
        // unlinking it lets a second connection lock a brand-new inode while the
        // first still holds the old one, i.e. two writers on the same database with
        // no error at all.
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&lock_path)?;

        let exclusive = !options.read_only;
        let timeout = Duration::from_millis(u64::from(options.lock_timeout_ms));
        let deadline = Instant::now() + timeout;
        let mut backoff = Duration::from_millis(5);

        loop {
            match Self::try_lock_once(&file, exclusive) {
                Ok(()) => {
                    return Ok(FileLock {
                        path: lock_path,
                        file: Some(file),
                    })
                }
                Err(TryLockError::WouldBlock) => {
                    if Instant::now() >= deadline {
                        return Err(DbError::Locked(format!(
                            "Failed to acquire the file lock on '{}' within {} ms: \
                             another connection is using this database",
                            lock_path.display(),
                            options.lock_timeout_ms
                        )));
                    }
                    thread::sleep(backoff);
                    backoff = (backoff * 2).min(Duration::from_millis(100));
                }
                Err(TryLockError::Error(err)) => return Err(DbError::Io(err)),
            }
        }
    }

    fn try_lock_once(file: &File, exclusive: bool) -> std::result::Result<(), TryLockError> {
        if exclusive {
            file.try_lock()
        } else {
            file.try_lock_shared()
        }
    }

    /// Releases the lock. Idempotent.
    pub fn release(&mut self) {
        if let Some(file) = self.file.take() {
            let _ = file.unlock();
        }
    }

    /// Path of the lock file, for diagnostics.
    pub fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for FileLock {
    fn drop(&mut self) {
        self.release();
    }
}
