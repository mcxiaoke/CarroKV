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

        let exclusive = !options.read_only;
        let timeout = Duration::from_millis(u64::from(options.lock_timeout_ms));
        let deadline = Instant::now() + timeout;
        let mut backoff = Duration::from_millis(5);

        loop {
            // FIX-02(a): the *open* has to be inside the retry window too.
            //
            // On Windows a holder that opened the lock file with `FileShare.None`
            // (the .NET implementation still does) makes `CreateFile` itself fail
            // with ERROR_SHARING_VIOLATION, so the lock call is never reached.
            // Leaving the open outside the loop bypassed `lock_timeout_ms` entirely
            // and surfaced a misleading `IoError` instead of `Locked`.
            //
            // The lock file is created once and deliberately **never deleted**:
            // unlinking it lets a second connection lock a brand-new inode while the
            // first still holds the old one, i.e. two writers on the same database
            // with no error at all.
            let file = match OpenOptions::new()
                .read(true)
                .write(true)
                .create(true)
                .truncate(false)
                .open(&lock_path)
            {
                Ok(f) => f,
                Err(err) => {
                    if !Self::is_contention(&err) {
                        return Err(DbError::Io(err));
                    }
                    if Instant::now() >= deadline {
                        return Err(Self::timed_out(&lock_path, options));
                    }
                    thread::sleep(backoff);
                    backoff = (backoff * 2).min(Duration::from_millis(100));
                    continue;
                }
            };

            match Self::try_lock_once(&file, exclusive) {
                Ok(()) => {
                    return Ok(FileLock {
                        path: lock_path,
                        file: Some(file),
                    })
                }
                Err(TryLockError::WouldBlock) => {
                    if Instant::now() >= deadline {
                        return Err(Self::timed_out(&lock_path, options));
                    }
                    thread::sleep(backoff);
                    backoff = (backoff * 2).min(Duration::from_millis(100));
                }
                Err(TryLockError::Error(err)) if Self::is_contention(&err) => {
                    if Instant::now() >= deadline {
                        return Err(Self::timed_out(&lock_path, options));
                    }
                    thread::sleep(backoff);
                    backoff = (backoff * 2).min(Duration::from_millis(100));
                }
                Err(TryLockError::Error(err)) => return Err(DbError::Io(err)),
            }
        }
    }

    fn timed_out(lock_path: &Path, options: &DbOptions) -> DbError {
        DbError::Locked(format!(
            "Failed to acquire the file lock on '{}' within {} ms: \
             another connection is using this database",
            lock_path.display(),
            options.lock_timeout_ms
        ))
    }

    /// Whether an I/O error means "someone else holds the database" rather than a
    /// genuine failure. Windows sharing / lock violations are numeric-only; the
    /// Unix numbers 5/32/33 mean something else entirely, so they are gated.
    fn is_contention(err: &std::io::Error) -> bool {
        if err.kind() == std::io::ErrorKind::WouldBlock {
            return true;
        }
        #[cfg(windows)]
        {
            // 5 = ERROR_ACCESS_DENIED, 32 = ERROR_SHARING_VIOLATION,
            // 33 = ERROR_LOCK_VIOLATION.
            if matches!(err.raw_os_error(), Some(5) | Some(32) | Some(33)) {
                return true;
            }
        }
        false
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
