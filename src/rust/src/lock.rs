use crate::errors::{DbError, Result};
use crate::options::DbOptions;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process;
use std::sync::atomic::{AtomicU64, Ordering};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

static COUNTER: AtomicU64 = AtomicU64::new(1);

fn generate_uuid() -> [u8; 16] {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let pid = process::id() as u64;
    let cnt = COUNTER.fetch_add(1, Ordering::Relaxed);

    let mut uuid = [0u8; 16];
    uuid[0..8].copy_from_slice(&(now as u64 ^ (pid << 32)).to_le_bytes());
    uuid[8..16].copy_from_slice(&(cnt ^ ((now >> 64) as u64)).to_le_bytes());
    uuid
}

fn current_time_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}

pub struct FileLock {
    path: PathBuf,
    uuid: [u8; 16],
    acquired: bool,
}

impl FileLock {
    pub fn acquire(db_path: &Path, options: &DbOptions) -> Result<Self> {
        let lock_path = PathBuf::from(format!("{}.lock", db_path.display()));
        let mut backoffs = [10, 30, 60, 120];
        let mut total_waited = 0u64;

        loop {
            let uuid = generate_uuid();
            let pid = process::id() as u64;
            let now_ms = current_time_ms();

            let mut content = [0u8; 32];
            content[0..8].copy_from_slice(&pid.to_le_bytes());
            content[8..24].copy_from_slice(&uuid);
            content[24..32].copy_from_slice(&now_ms.to_le_bytes());

            match OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&lock_path)
            {
                Ok(mut file) => {
                    let _ = file.write_all(&content);
                    let _ = file.sync_all();
                    return Ok(FileLock {
                        path: lock_path,
                        uuid,
                        acquired: true,
                    });
                }
                Err(err) if err.kind() == std::io::ErrorKind::AlreadyExists => {
                    // Lock exists, check stale lock
                    let mut lock_time = 0u64;
                    if let Ok(mut f) = File::open(&lock_path) {
                        let mut buf = [0u8; 32];
                        if f.read_exact(&mut buf).is_ok() {
                            lock_time = u64::from_le_bytes(buf[24..32].try_into().unwrap());
                        }
                    }

                    let is_stale = if options.force_unlock {
                        true
                    } else if lock_time > 0 && now_ms >= lock_time {
                        (now_ms - lock_time) >= options.lock_timeout_ms
                    } else {
                        // Fallback to file mtime if read failed
                        if let Ok(meta) = fs::metadata(&lock_path) {
                            if let Ok(modified) = meta.modified() {
                                let mtime_ms = modified
                                    .duration_since(UNIX_EPOCH)
                                    .unwrap_or_default()
                                    .as_millis() as u64;
                                now_ms >= mtime_ms && (now_ms - mtime_ms) >= options.lock_timeout_ms
                            } else {
                                false
                            }
                        } else {
                            false
                        }
                    };

                    if is_stale {
                        // Attempt safe stealing via atomic rename
                        let steal_path = PathBuf::from(format!(
                            "{}.steal.{}",
                            lock_path.display(),
                            hex_str(&generate_uuid())
                        ));
                        match fs::rename(&lock_path, &steal_path) {
                            Ok(_) => {
                                let _ = fs::remove_file(steal_path);
                                continue; // Stolen successfully, retry create_new
                            }
                            Err(e) => {
                                // Windows error code 32 (Sharing violation) or 5 (Access denied)
                                let raw_code = e.raw_os_error().unwrap_or(0);
                                if raw_code == 32 || raw_code == 33 || raw_code == 5 {
                                    // Process is still alive and holding handle, cannot steal!
                                    // Fall through to backoff wait
                                }
                            }
                        }
                    }

                    // Backoff wait
                    let wait_ms = backoffs[0];
                    if backoffs.len() > 1 {
                        backoffs = [backoffs[1], backoffs[2], backoffs[3], backoffs[3]];
                    }
                    thread::sleep(Duration::from_millis(wait_ms));
                    total_waited += wait_ms;

                    if total_waited >= options.lock_timeout_ms {
                        return Err(DbError::Locked(format!(
                            "Failed to acquire lock on '{}' after {} ms",
                            lock_path.display(),
                            total_waited
                        )));
                    }
                }
                Err(err) => return Err(DbError::Io(err)),
            }
        }
    }

    pub fn release(&mut self) {
        if !self.acquired {
            return;
        }
        self.acquired = false;

        // Anti-ABA check: read lock file and verify UUID matches before unlinking
        if let Ok(mut f) = File::open(&self.path) {
            let mut buf = [0u8; 32];
            if f.read_exact(&mut buf).is_ok() {
                let file_uuid = &buf[8..24];
                if file_uuid == self.uuid {
                    let _ = fs::remove_file(&self.path);
                    return;
                }
            }
        }
        // If UUID does not match or file not found, do not remove (was stolen or deleted)
    }
}

impl Drop for FileLock {
    fn drop(&mut self) {
        self.release();
    }
}

fn hex_str(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push_str(&format!("{:02x}", b));
    }
    s
}
