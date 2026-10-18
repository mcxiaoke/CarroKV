use crate::errors::{DbError, Result};
use crate::format::{BatchFrameWriter, FileHeader, FrameLoader, RawOpEntry, FILE_HEADER_LEN};
use crate::lock::FileLock;
use crate::options::DbOptions;
use crate::types::*;
use parking_lot::{Condvar, Mutex, RwLock};
use std::collections::{BTreeMap, HashMap};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Weak};
use std::thread;
use std::time::Duration;

// Global process-wide registry to prevent duplicate openings of the same canonical path
static SHARED_REGISTRY: Mutex<Option<HashMap<PathBuf, Weak<RwLock<DbInner>>>>> = Mutex::new(None);

fn canonicalize_path(p: &Path) -> PathBuf {
    if let Ok(canon) = fs::canonicalize(p) {
        return canon;
    }
    // File does not exist yet: canonicalize the parent and re-attach the file name.
    // `Path::new("t.db").parent()` is `Some("")`, and `canonicalize("")` always fails,
    // which used to produce a *relative* key while "./t.db" produced an absolute one -
    // two different registry keys for the same file. Normalise an empty/relative
    // parent to "." so both forms collapse to the same canonical key.
    let (parent, file_name) = match (p.parent(), p.file_name()) {
        (Some(parent), Some(name)) => (parent, name),
        _ => return p.to_path_buf(),
    };
    let parent = if parent.as_os_str().is_empty() {
        Path::new(".")
    } else {
        parent
    };
    let canon_parent = fs::canonicalize(parent).unwrap_or_else(|_| parent.to_path_buf());
    canon_parent.join(file_name)
}

/// A registered watcher slot. The id is what makes `unwatch` possible:
/// without it a callback could never be removed from the list.
type WatcherSlot = (u64, Arc<dyn Fn(&KeyChangeEvent) + Send + Sync>);

/// Handle returned by [`Db::watch`]. Call [`WatcherHandle::unwatch`] to remove the
/// callback from the database.
///
/// Deliberately **not** `Drop`: dropping the handle does *not* unsubscribe, so the
/// common `db.watch(cb);` keeps working exactly as it did before API v1.0.
/// Unsubscription is always explicit. This matches Go (`Unwatch()`) and .NET
/// (`WatcherHandle.Dispose()`), where only an explicit call removes the callback.
pub struct WatcherHandle {
    inner: Weak<RwLock<DbInner>>,
    id: u64,
}

impl WatcherHandle {
    /// Remove the associated watcher. Idempotent.
    pub fn unwatch(&self) {
        if let Some(inner) = self.inner.upgrade() {
            let mut guard = inner.write();
            guard.watchers.retain(|(id, _)| *id != self.id);
        }
    }

    /// The id assigned to this watcher within its database instance.
    pub fn id(&self) -> u64 {
        self.id
    }
}

/// Aggregate statistics returned by [`Db::stats`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DbStats {
    /// Physical size of the database file in bytes; 0 if nothing has been flushed yet.
    pub file_size: u64,
    /// Number of namespaces currently present (including empty ones).
    pub namespace_count: usize,
    /// Total number of live entries across all namespaces.
    pub total_entries: usize,
    /// The instance's current max TxID.
    pub current_max_tx_id: u64,
}

/// One namespace: key bytes -> (value type, value bytes).
type NamespaceMap = BTreeMap<Vec<u8>, (u8, Vec<u8>)>;
/// Whole in-memory state: namespace bytes -> shared namespace map (the `Arc` is what
/// makes snapshot isolation O(1): a snapshot clones the outer map only).
type DataMap = BTreeMap<Vec<u8>, Arc<NamespaceMap>>;

pub struct DbInner {
    canonical_path: PathBuf,
    path: PathBuf,
    options: DbOptions,
    // Outer key: namespace bytes; Inner key: key bytes -> (value_type, value_bytes) wrapped in Arc for zero-copy CoW snapshots
    data: DataMap,
    /// Live payload bytes across all namespaces: Σ (key.len + value.len).
    ///
    /// Maintained incrementally by `mem_put`/`mem_delete`/`mem_drop`/`mem_clear`
    /// so the dead-ratio compaction trigger stays O(1) per mutation; recomputing it
    /// by walking `data` on every write would be O(N) once the log passes the
    /// adaptive baseline.
    live_payload_bytes: u64,
    current_max_tx_id: u64,
    last_known_disk_state: (u64, u64), // (max_tx_id, file_size)
    registered: bool,
    watchers: Vec<WatcherSlot>,
    next_watcher_id: u64,
    is_dirty: bool,
    bg_shutdown: Option<Arc<(Mutex<bool>, Condvar)>>,
    bg_thread: Option<thread::JoinHandle<()>>,
    /// Set when an automatic compaction attempt failed and a retry is pending
    /// (decision D2). The mutation that triggered it is already durable.
    needs_compaction: bool,
    /// Consecutive automatic compaction failures, used to widen the backoff.
    compaction_failures: u32,
    /// Writes observed since the last automatic compaction attempt.
    writes_since_compaction_attempt: u64,
    /// The inter-process lock, acquired by `open` and held for the whole lifetime of
    /// this connection (C9). Dropping the instance releases it; `close()` releases it
    /// explicitly (FIX-02c).
    lock: Option<FileLock>,
    /// Set by `close()`. Writes (and flushes) are refused afterwards so a handle kept
    /// past `close()` can never write to a database whose lock has been released.
    /// In-memory reads still work; they are no longer guaranteed to match the file.
    closed: bool,
    /// FIX-01: serialises every producer of the shared `<db>.tmp` file.
    ///
    /// Both the background flush (C16 two-phase: clone under the read lock,
    /// serialise outside it) and the explicit paths (`flush`/`close`/`compact`)
    /// write the *same* `.tmp`. Without this mutex two of them truncate and rewrite
    /// each other's file. Lock order is always `flush_mutex` -> `RwLock`: no path
    /// may acquire it while already holding the write lock.
    flush_mutex: Arc<Mutex<()>>,
    /// FIX-01: bumped on every committed mutation. The background flush records the
    /// generation together with its snapshot and only rotates if it is unchanged, so
    /// a stale snapshot can neither overwrite a newer confirmed write nor clear someone
    /// else's dirty flag.
    dirty_generation: u64,
    /// Pending warning events queued during write operations to be dispatched outside locks.
    pending_warnings: Vec<(&'static str, String)>,
}

/// Smallest physical log size at which the dead-ratio trigger is considered.
/// A 50%-dead 300 KB log costs a few hundred KB to rewrite, which is cheaper to
/// ignore than to reclaim eagerly.
const ADAPTIVE_MIN_FILE_SIZE: u64 = 2 * 1024 * 1024;

/// Dead-payload share (percent) at which an under-threshold log is compacted.
/// Compaction rewrites all live data, so it only pays for itself once at least
/// half the file is garbage.
const ADAPTIVE_DEAD_RATIO_PERCENT: u64 = 50;

impl DbInner {
    pub fn check_writable(&self) -> Result<()> {
        if self.closed {
            return Err(DbError::Locked(
                "Database is closed: writes are refused after close()".to_string(),
            ));
        }
        if self.options.read_only {
            return Err(DbError::Locked(
                "Database is opened in read-only mode".to_string(),
            ));
        }
        Ok(())
    }

    /// N-X2 / M2-02: the AOL background step only fsyncs, so it has no snapshot
    /// to discard -- but it must still refuse to clear `is_dirty` when a writer
    /// appended frames after the generation it sampled. Clearing it would leave
    /// those frames permanently unfsynced (`close()` would skip them too).
    fn aol_flush_should_clear_dirty(&self, sampled_generation: u64) -> bool {
        self.options.engine_mode == StorageEngineMode::AppendOnlyLog
            && self.is_dirty
            && !self.closed
            && self.dirty_generation == sampled_generation
    }

    pub fn queue_warning(&mut self, code: &'static str, message: String) {
        if self.options.on_warning.is_some() {
            self.pending_warnings.push((code, message));
        }
    }

    pub fn take_pending_warnings(&mut self) -> Vec<(&'static str, String)> {
        std::mem::take(&mut self.pending_warnings)
    }

    pub fn rollback_mutation(
        &mut self,
        rollback_stores: HashMap<Vec<u8>, Option<Arc<NamespaceMap>>>,
        old_live_payload_bytes: u64,
    ) {
        for (ns, old_store) in rollback_stores {
            match old_store {
                Some(store) => {
                    self.data.insert(ns, store);
                }
                None => {
                    self.data.remove(&ns);
                }
            }
        }
        self.live_payload_bytes = old_live_payload_bytes;
    }

    /// Total live payload bytes across all namespaces (O(1)).
    fn live_payload_total(&self) -> u64 {
        self.live_payload_bytes
    }

    /// Inserts a value in memory and keeps the live-payload ledger in sync.
    ///
    /// Every in-memory mutation in this module must go through these four
    /// methods (`mem_put`/`mem_delete`/`mem_drop`/`mem_clear`); touching `data`
    /// directly would silently desynchronise the ledger and therefore the
    /// dead-ratio compaction trigger.
    fn mem_put(&mut self, namespace: &[u8], key: Vec<u8>, value_type: u8, value: Vec<u8>) {
        let ns_arc = self.data.entry(namespace.to_vec()).or_default();
        let ns_map = Arc::make_mut(ns_arc);
        let new_total = (key.len() + value.len()) as u64;
        let old_total = ns_map
            .get(&key)
            .map(|(_, old)| (key.len() + old.len()) as u64)
            .unwrap_or(0);
        ns_map.insert(key, (value_type, value));
        self.live_payload_bytes = self.live_payload_bytes + new_total - old_total;
    }

    /// Removes a key from memory; returns whether it existed.
    fn mem_delete(&mut self, namespace: &[u8], key: &[u8]) -> bool {
        if let Some(ns_arc) = self.data.get_mut(namespace) {
            let ns_map = Arc::make_mut(ns_arc);
            if let Some((_, old)) = ns_map.remove(key) {
                self.live_payload_bytes = self
                    .live_payload_bytes
                    .saturating_sub((key.len() + old.len()) as u64);
                return true;
            }
        }
        false
    }

    /// Removes a whole namespace from memory; returns whether it existed.
    fn mem_drop(&mut self, namespace: &[u8]) -> bool {
        match self.data.remove(namespace) {
            Some(ns_arc) => {
                let total: u64 = ns_arc
                    .iter()
                    .map(|(k, (_, v))| (k.len() + v.len()) as u64)
                    .sum();
                self.live_payload_bytes = self.live_payload_bytes.saturating_sub(total);
                true
            }
            None => false,
        }
    }

    /// Keeps the namespace but removes every key; returns whether it had entries.
    fn mem_clear(&mut self, namespace: &[u8]) -> bool {
        if let Some(ns_arc) = self.data.get_mut(namespace) {
            let ns_map = Arc::make_mut(ns_arc);
            if ns_map.is_empty() {
                return false;
            }
            let total: u64 = ns_map
                .iter()
                .map(|(k, (_, v))| (k.len() + v.len()) as u64)
                .sum();
            ns_map.clear();
            self.live_payload_bytes = self.live_payload_bytes.saturating_sub(total);
            return true;
        }
        false
    }
}

impl Drop for DbInner {
    fn drop(&mut self) {
        if let Some(shutdown) = self.bg_shutdown.take() {
            let (lock, cvar) = &*shutdown;
            *lock.lock() = true;
            cvar.notify_all();
        }
        if let Some(handle) = self.bg_thread.take() {
            if handle.thread().id() != thread::current().id() {
                let _ = handle.join();
            }
        }
        if self.is_dirty && !self.options.read_only {
            if self.options.engine_mode == StorageEngineMode::AppendOnlyLog {
                if self.path.exists() {
                    if let Ok(f) = OpenOptions::new().write(true).open(&self.path) {
                        let _ = f.sync_all();
                    }
                }
                self.is_dirty = false;
            } else if let Ok(disk_max_tx_id) = self.precheck_disk_state() {
                let _ = self.commit_flush(disk_max_tx_id);
            }
        }
        if self.registered {
            let mut guard = SHARED_REGISTRY.lock();
            if let Some(map) = guard.as_mut() {
                map.remove(&self.canonical_path);
            }
        }
    }
}

/// An industrial-grade, cross-platform, pure-native embedded Key-Value database instance.
///
/// Thread-safe (`Send + Sync + Clone`). Multiple threads can read concurrently without blocking.
/// Write operations and snapshot compaction are mutually exclusive.
///
/// # Examples
///
/// ```rust
/// use carro_kv::Db;
///
/// # fn main() -> Result<(), Box<dyn std::error::Error>> {
/// let dir = tempfile::tempdir()?;
/// let db_path = dir.path().join("my_app.db");
///
/// let db = Db::open(&db_path, None)?;
/// db.put_string("", "username", "alice")?;
/// assert_eq!(db.get_string("", "username")?, Some("alice".to_string()));
/// # Ok(())
/// # }
/// ```
#[derive(Clone)]
pub struct Db {
    inner: Arc<RwLock<DbInner>>,
}

impl Db {
    /// Open or create a database at the specified path.
    ///
    /// Automatically performs crash self-healing (promoting `.tmp` or recovering from `.bak`)
    /// and verifies full-file frame integrity.
    ///
    /// If the same canonical path is already open within the current process, this method
    /// safely returns a clone of the existing thread-safe instance.
    pub fn open<P: AsRef<Path>>(path: P, options: Option<DbOptions>) -> Result<Self> {
        let path = path.as_ref().to_path_buf();
        let canon_path = canonicalize_path(&path);
        let options = options.unwrap_or_default();
        Self::open_internal(path, canon_path, options)
    }

    /// §7.4 静态备份接口：以只读模式打开 source 数据库，将其一致性快照原子备份到 dest，完成后自动关闭连接。
    pub fn backup_file<P1: AsRef<Path>, P2: AsRef<Path>>(
        source: P1,
        dest: P2,
        options: Option<DbOptions>,
    ) -> Result<()> {
        let mut opts = options.unwrap_or_default();
        opts.read_only = true;
        let db = Self::open(source, Some(opts))?;
        db.backup(dest.as_ref())
    }

    /// Reentrant-open 选项冲突检查（§3.1 冻结）。
    ///
    /// **`read_only` 冲突必须抛错，不得静默复用。** 命中单例时若只发一条 `on_warning`
    /// 就返回原实例，调用方以 `read_only = true` 打开却拿到一个读写实例——那是静默提权；
    /// 而 `on_warning` 默认是 `None`，默认配置下连告警都看不见。反方向（要写拿到只读实例）
    /// 同样会让后续写入以 `Locked` 的形式在远处失败、误导排查。两个方向都拒绝。
    ///
    /// `engine_mode` / `sync_mode` 冲突维持告警：它们只造成性能与落盘语义差异，
    /// 不构成权限问题。
    fn check_options_conflict(existing: &DbOptions, new_opts: &DbOptions) -> Result<()> {
        if existing.read_only != new_opts.read_only {
            return Err(DbError::ConflictingOptions(format!(
                "read_only conflict on reentrant open: existing={}, requested={}. \
                 The in-process singleton is returned unchanged by contract, so it cannot honour \
                 the requested permission. Drop the other handle first, or open a different \
                 canonical path.",
                existing.read_only, new_opts.read_only
            )));
        }

        let mut conflicts = Vec::new();
        if existing.engine_mode != new_opts.engine_mode {
            conflicts.push(format!(
                "engine_mode (existing={:?}, requested={:?})",
                existing.engine_mode, new_opts.engine_mode
            ));
        }
        if existing.sync_mode != new_opts.sync_mode {
            conflicts.push(format!(
                "sync_mode (existing={:?}, requested={:?})",
                existing.sync_mode, new_opts.sync_mode
            ));
        }
        if !conflicts.is_empty() {
            let msg = format!(
                "Options conflict on reentrant open: {}. Using existing singleton options.",
                conflicts.join(", ")
            );
            if let Some(cb) = &new_opts.on_warning {
                cb("onOptionsConflict", &msg);
            } else if let Some(cb) = &existing.on_warning {
                cb("onOptionsConflict", &msg);
            }
        }
        Ok(())
    }

    fn open_internal(path: PathBuf, canon_path: PathBuf, options: DbOptions) -> Result<Self> {
        // Step 1: Check existing instance under short lock
        {
            let mut guard = SHARED_REGISTRY.lock();
            let map = guard.get_or_insert_with(HashMap::new);
            if let Some(weak) = map.get(&canon_path) {
                if let Some(arc) = weak.upgrade() {
                    let existing_opts = arc.read().options.clone();
                    Self::check_options_conflict(&existing_opts, &options)?;
                    return Ok(Db { inner: arc });
                }
            }
        }

        // Step 2: Perform disk I/O and recovery without holding any global lock
        let mut inner = DbInner {
            canonical_path: canon_path.clone(),
            path,
            options: options.clone(),
            data: BTreeMap::new(),
            live_payload_bytes: 0,
            current_max_tx_id: 0,
            last_known_disk_state: (0, 0),
            registered: false,
            watchers: Vec::new(),
            next_watcher_id: 1,
            is_dirty: false,
            bg_shutdown: None,
            bg_thread: None,
            needs_compaction: false,
            compaction_failures: 0,
            writes_since_compaction_attempt: 0,
            lock: None,
            closed: false,
            flush_mutex: Arc::new(Mutex::new(())),
            dirty_generation: 0,
            pending_warnings: Vec::new(),
        };

        // C9: the inter-process lock is taken *before* anything touches the disk and
        // held for the whole lifetime of the connection. `init_and_recover` writes to
        // the database files (tail healing, `.tmp` promotion, `.bak` recovery), and
        // since the lock is already held that repair can no longer truncate frames a
        // concurrent appender just wrote (C10).
        inner.lock = Some(FileLock::acquire(&inner.path, &inner.options)?);
        inner.init_and_recover()?;

        // Step 3: Register under short lock
        let mut guard = SHARED_REGISTRY.lock();
        let map = guard.get_or_insert_with(HashMap::new);

        // Double check if another thread opened it concurrently
        if let Some(weak) = map.get(&canon_path) {
            if let Some(arc) = weak.upgrade() {
                let existing_opts = arc.read().options.clone();
                Self::check_options_conflict(&existing_opts, &options)?;
                return Ok(Db { inner: arc });
            }
        }

        inner.registered = true;
        let arc = Arc::new(RwLock::new(inner));
        map.insert(canon_path, Arc::downgrade(&arc));

        if options.sync_mode == SyncMode::Deferred && !options.read_only {
            let weak = Arc::downgrade(&arc);
            let shutdown_pair = Arc::new((Mutex::new(false), Condvar::new()));
            let shutdown_clone = shutdown_pair.clone();
            let interval_ms = options.sync_interval_ms as u64;
            let handle = thread::Builder::new()
                .name("ckv-sync-worker".to_string())
                .spawn(move || {
                    let (lock, cvar) = &*shutdown_clone;
                    loop {
                        {
                            let mut guard = lock.lock();
                            // Check the flag *before* waiting (C16 shutdown handshake).
                            // Without this, a `close()` that sets the flag and notifies
                            // while the worker is between two waits loses the wakeup and
                            // `join()` blocks for the whole interval -- an hour for a
                            // large `sync_interval_ms`.
                            if *guard {
                                break;
                            }
                            cvar.wait_for(&mut guard, Duration::from_millis(interval_ms));
                            if *guard {
                                break;
                            }
                        }
                        if let Some(inner_arc) = weak.upgrade() {
                            Self::bg_flush_step(&inner_arc);
                        } else {
                            break;
                        }
                    }
                })
                .ok();
            let mut guard = arc.write();
            guard.bg_shutdown = Some(shutdown_pair);
            guard.bg_thread = handle;
        }

        Ok(Db { inner: arc })
    }

    /// Background flush step executed outside long write locks (C16).
    /// Readers are not blocked during snapshot serialization or fsync.
    fn bg_flush_step(inner_arc: &Arc<RwLock<DbInner>>) {
        // FIX-01: every writer of `<db>.tmp` must hold this mutex. SnapshotAtomic is
        // the only mode whose background step writes `.tmp`; AOL only fsyncs, and the
        // AOL compaction path runs entirely under the write lock (it never takes this
        // mutex, which is what keeps the lock order acyclic).
        let (flush_mutex, snapshot_mode) = {
            let inner = inner_arc.read();
            (
                inner.flush_mutex.clone(),
                inner.options.engine_mode == StorageEngineMode::SnapshotAtomic,
            )
        };
        let _flush_guard = if snapshot_mode {
            Some(flush_mutex.lock())
        } else {
            None
        };

        let plan = {
            let inner = inner_arc.read();
            if !inner.is_dirty || inner.options.read_only || inner.closed {
                None
            } else if inner.options.engine_mode == StorageEngineMode::AppendOnlyLog {
                // N-X2: the AOL branch only fsyncs, so it has no snapshot to
                // discard -- but it still must sample the generation, otherwise
                // a frame appended between our `sync_all()` and the moment we
                // clear `is_dirty` would never be fsynced (and `close()` would
                // skip it because the flag already says "clean").
                Some((
                    StorageEngineMode::AppendOnlyLog,
                    inner.path.clone(),
                    None,
                    0,
                    inner.dirty_generation,
                ))
            } else {
                match inner.precheck_disk_state() {
                    Ok(disk_max) => {
                        let start_tx = inner.current_max_tx_id.max(disk_max);
                        Some((
                            StorageEngineMode::SnapshotAtomic,
                            inner.path.clone(),
                            Some(inner.data.clone()),
                            start_tx,
                            inner.dirty_generation,
                        ))
                    }
                    Err(_) => None,
                }
            }
        };

        let (engine, path, data_opt, start_tx, generation) = match plan {
            Some(p) => p,
            None => return,
        };

        match engine {
            StorageEngineMode::AppendOnlyLog => {
                // M2-02: open failures and fsync failures must leave the dirty
                // flag set -- previously both were swallowed and the flag was
                // cleared anyway, so a failed fsync looked like a durable write.
                let mut synced = false;
                if let Ok(f) = OpenOptions::new().write(true).open(&path) {
                    synced = f.sync_all().is_ok();
                }
                if !synced {
                    return;
                }
                let mut inner = inner_arc.write();
                if inner.aol_flush_should_clear_dirty(generation) {
                    inner.is_dirty = false;
                }
            }
            StorageEngineMode::SnapshotAtomic => {
                let data = match data_opt {
                    Some(d) => d,
                    None => return,
                };
                let tmp_path = PathBuf::from(format!("{}.tmp", path.display()));
                let bak_path = PathBuf::from(format!("{}.bak", path.display()));

                let final_max_tx_id =
                    match DbInner::serialize_snapshot_to_tmp(&tmp_path, &data, start_tx) {
                        Ok(tx) => tx,
                        Err(_) => return,
                    };

                let mut inner = inner_arc.write();

                // FIX-01: discard our snapshot when it is stale. Rotating it would
                // overwrite a newer generation that an explicit `flush()` (or another
                // commit) already persisted, and clearing `is_dirty` would then hide
                // those writes from the next flush entirely.
                if !inner.is_dirty
                    || inner.options.read_only
                    || inner.closed
                    || inner.dirty_generation != generation
                {
                    let _ = fs::remove_file(&tmp_path);
                    return;
                }

                if path.exists() && rename_with_retry(&path, &bak_path).is_err() {
                    let _ = fs::remove_file(&tmp_path);
                    return;
                }
                if rename_with_retry(&tmp_path, &path).is_err() {
                    return;
                }
                fsync_parent_dir(&path);
                inner.current_max_tx_id = final_max_tx_id;
                if let Ok(meta) = fs::metadata(&path) {
                    inner.last_known_disk_state = (final_max_tx_id, meta.len());
                }
                inner.is_dirty = false;
            }
        }
    }

    /// Returns whether this database instance has been closed.
    pub fn is_closed(&self) -> bool {
        self.inner.read().closed
    }

    /// Close the database instance.
    /// Flushes any pending dirty state if in Deferred/Manual sync mode and shuts down background workers.
    pub fn close(&self) -> Result<()> {
        let flush_mutex = {
            let inner = self.inner.read();
            if inner.closed {
                return Ok(());
            }
            inner.flush_mutex.clone()
        };

        {
            let mut inner = self.inner.write();
            if let Some(shutdown) = inner.bg_shutdown.take() {
                let (lock, cvar) = &*shutdown;
                *lock.lock() = true;
                cvar.notify_all();
            }
        }
        let handle = { self.inner.write().bg_thread.take() };

        // Join *before* taking `flush_mutex`: a worker that is between "woke up" and
        // "acquired flush_mutex" would otherwise wait for a mutex that close() holds
        // while join() waits for the worker. The shutdown flag above makes the worker
        // exit without starting a new flush step.
        if let Some(h) = handle {
            if h.thread().id() != thread::current().id() {
                let _ = h.join();
            }
        }

        // No background producer is left, so this is the only writer of `.tmp` now.
        // Taking the mutex also excludes an explicit `flush()` from another thread.
        let _flush_guard = flush_mutex.lock();
        let mut inner = self.inner.write();

        if inner.is_dirty && !inner.options.read_only {
            if inner.options.engine_mode == StorageEngineMode::AppendOnlyLog {
                if inner.path.exists() {
                    let f = OpenOptions::new().write(true).open(&inner.path)?;
                    f.sync_all()?;
                }
                inner.is_dirty = false;
            } else {
                let disk_max_tx_id = inner.precheck_disk_state()?;
                inner.commit_flush(disk_max_tx_id)?;
            }
        }

        // D2: give a compaction that failed earlier one more chance, so a
        // transient failure does not outlive the handle.
        inner.retry_pending_compaction();

        // FIX-02(c): `close()` releases the inter-process lock, as the frozen contract
        // requires. Before this it stayed held until the last `Db` handle was dropped,
        // so a caller that closed the database and immediately reopened it (or started
        // a CLI/migration process) waited for the whole lock timeout.
        //
        // The instance also unregisters itself: otherwise a same-process `open()`
        // would hand back this closed instance from `SHARED_REGISTRY` instead of
        // taking the (now free) lock.
        inner.closed = true;
        inner.lock.take();
        inner.registered = false;
        let canon_path = inner.canonical_path.clone();
        drop(inner);

        // Registry is acquired *after* releasing the write lock to keep the global
        // lock order (`registry` -> `inner`) that `open_internal` relies on.
        let mut guard = SHARED_REGISTRY.lock();
        if let Some(map) = guard.as_mut() {
            map.remove(&canon_path);
        }

        Ok(())
    }

    /// 获取绑定到特定命名空间的便利门面（Namespace Scope）。
    pub fn ns<'a>(&'a self, ns: &'a str) -> NamespaceScope<'a> {
        NamespaceScope { db: self, ns }
    }

    /// 获取默认命名空间的便利门面。
    pub fn default_ns(&self) -> NamespaceScope<'_> {
        self.ns("")
    }

    /// Check if a key exists in the specified namespace.
    pub fn contains<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<bool> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read();
        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            return Ok(ns_map.contains_key(key.as_ref()));
        }
        Ok(false)
    }

    /// Get raw bytes for a key in the specified namespace.
    ///
    /// Regardless of whether the entry was saved as Bytes or Utf8String, the raw bytes are returned.
    pub fn get_bytes<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<Vec<u8>>> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read();
        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            if let Some((_, val)) = ns_map.get(key.as_ref()) {
                return Ok(Some(val.clone()));
            }
        }
        Ok(None)
    }

    /// Get a UTF-8 string value for a key in the specified namespace.
    ///
    /// # Errors
    /// Returns `DbError::TypeMismatch` if the stored value is not a UTF-8 string.
    /// Returns `DbError::InvalidUtf8` if the underlying bytes are not valid UTF-8.
    pub fn get_string<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<String>> {
        let norm_ns = normalize_namespace(ns)?;
        let key_ref = key.as_ref();
        let inner = self.inner.read();
        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            if let Some((vtype, val)) = ns_map.get(key_ref) {
                if *vtype != VAL_TYPE_STRING {
                    let key_disp = String::from_utf8_lossy(key_ref);
                    return Err(DbError::TypeMismatch(format!(
                        "Key '{}' has value_type {}, expected string ({})",
                        key_disp, vtype, VAL_TYPE_STRING
                    )));
                }
                let key_disp = String::from_utf8_lossy(key_ref);
                return Ok(Some(String::from_utf8(val.clone()).map_err(|e| {
                    DbError::InvalidUtf8(format!("Invalid UTF-8 in key '{}': {}", key_disp, e))
                })?));
            }
        }
        Ok(None)
    }

    /// Check if a key exists in the specified namespace.
    pub fn contains_key<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<bool> {
        self.contains(ns, key)
    }

    /// Return total alive key count in the specified namespace.
    pub fn count(&self, ns: &str) -> Result<usize> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read();
        Ok(inner
            .data
            .get(norm_ns.as_bytes())
            .map(|m| m.len())
            .unwrap_or(0))
    }

    /// Create a point-in-time immutable snapshot of the database.
    ///
    /// The snapshot provides read-only isolation: readers holding this snapshot
    /// never block concurrent writes, and concurrent writes never affect this snapshot.
    pub fn snapshot(&self) -> Snapshot {
        let inner = self.inner.read();
        Snapshot {
            tx_id: inner.current_max_tx_id,
            data: inner.data.clone(),
        }
    }

    fn execute_mutation<T, F>(&self, mutation_fn: F) -> Result<T>
    where
        F: FnOnce(
            &mut DbInner,
            &mut HashMap<Vec<u8>, Option<Arc<NamespaceMap>>>,
        ) -> Result<(Vec<RawOpEntry>, Vec<KeyChangeEvent>, bool, T)>,
    {
        let (engine_mode, sync_mode) = {
            let inner = self.inner.read();
            inner.check_writable()?;
            (inner.options.engine_mode, inner.options.sync_mode)
        };

        let mut rollback_stores: HashMap<Vec<u8>, Option<Arc<NamespaceMap>>> = HashMap::new();
        let (res, watchers, warnings, events) = if engine_mode == StorageEngineMode::AppendOnlyLog {
            let mut inner = self.inner.write();
            inner.check_writable()?;
            let old_payload = inner.live_payload_bytes;

            let (entries, events, modified, ret) =
                match mutation_fn(&mut inner, &mut rollback_stores) {
                    Ok(v) => v,
                    Err(e) => {
                        inner.rollback_mutation(rollback_stores, old_payload);
                        return Err(e);
                    }
                };

            if modified && !entries.is_empty() {
                let new_file_size = match inner.append_entries(&entries) {
                    Ok(sz) => sz,
                    Err(e) => {
                        inner.rollback_mutation(rollback_stores, old_payload);
                        inner.is_dirty = true;
                        return Err(e);
                    }
                };
                inner.dirty_generation = inner.dirty_generation.wrapping_add(1);
                if let Err(e) = inner.maybe_compact_after_write(new_file_size) {
                    if inner.options.strict_compaction {
                        let warnings = inner.take_pending_warnings();
                        drop(inner);
                        let on_warning = self.inner.read().options.on_warning.clone();
                        if let Some(cb) = on_warning {
                            for (code, msg) in warnings {
                                cb(code, &msg);
                            }
                        }
                        return Err(e);
                    }
                }
            }

            let watchers = inner.watchers.clone();
            let warnings = inner.take_pending_warnings();
            (ret, watchers, warnings, events)
        } else if sync_mode == SyncMode::Deferred || sync_mode == SyncMode::Manual {
            let mut inner = self.inner.write();
            inner.check_writable()?;
            let old_payload = inner.live_payload_bytes;

            let (_entries, events, modified, ret) =
                match mutation_fn(&mut inner, &mut rollback_stores) {
                    Ok(v) => v,
                    Err(e) => {
                        inner.rollback_mutation(rollback_stores, old_payload);
                        return Err(e);
                    }
                };

            if modified {
                inner.current_max_tx_id += 1;
                inner.is_dirty = true;
                inner.dirty_generation = inner.dirty_generation.wrapping_add(1);
            }

            let watchers = inner.watchers.clone();
            let warnings = inner.take_pending_warnings();
            (ret, watchers, warnings, events)
        } else {
            // SNAPSHOT 引擎 + IMMEDIATE 模式：两阶段提交（短写锁内存快照 → 锁外序列化落盘与 fsync → 短写锁原子轮转）
            let flush_mutex = {
                let inner = self.inner.read();
                inner.flush_mutex.clone()
            };
            let _flush_guard = flush_mutex.lock();

            let (
                snap_data,
                path,
                start_tx,
                old_payload,
                modified,
                ret,
                watchers,
                mut warnings,
                events,
            ) = {
                let mut inner = self.inner.write();
                inner.check_writable()?;
                let disk_max_tx_id = inner.precheck_disk_state()?;
                let old_payload = inner.live_payload_bytes;

                let (_entries, events, modified, ret) =
                    match mutation_fn(&mut inner, &mut rollback_stores) {
                        Ok(v) => v,
                        Err(e) => {
                            inner.rollback_mutation(rollback_stores, old_payload);
                            return Err(e);
                        }
                    };

                let snap_data = if modified {
                    Some(inner.data.clone())
                } else {
                    None
                };
                let start_tx = inner.current_max_tx_id.max(disk_max_tx_id);
                let path = inner.path.clone();
                let watchers = inner.watchers.clone();
                let warnings = inner.take_pending_warnings();
                (
                    snap_data,
                    path,
                    start_tx,
                    old_payload,
                    modified,
                    ret,
                    watchers,
                    warnings,
                    events,
                )
            };

            if modified {
                let snap_data = snap_data.unwrap();
                let tmp_path = PathBuf::from(format!("{}.tmp", path.display()));
                let bak_path = PathBuf::from(format!("{}.bak", path.display()));

                // Phase 2: 锁外序列化落盘与 fsync（并发读零阻塞）
                let final_max_tx_id =
                    match DbInner::serialize_snapshot_to_tmp(&tmp_path, &snap_data, start_tx) {
                        Ok(tx) => tx,
                        Err(e) => {
                            let mut inner = self.inner.write();
                            inner.rollback_mutation(rollback_stores, old_payload);
                            inner.is_dirty = true;
                            let _ = fs::remove_file(&tmp_path);
                            return Err(e);
                        }
                    };

                // Phase 3: 短写锁完成文件原子轮转与元数据更新
                {
                    let mut inner = self.inner.write();
                    if path.exists() {
                        if let Err(e) = rename_with_retry(&path, &bak_path) {
                            inner.rollback_mutation(rollback_stores, old_payload);
                            inner.is_dirty = true;
                            let _ = fs::remove_file(&tmp_path);
                            return Err(e);
                        }
                    }
                    if let Err(e) = rename_with_retry(&tmp_path, &path) {
                        inner.rollback_mutation(rollback_stores, old_payload);
                        inner.is_dirty = true;
                        return Err(e);
                    }
                    fsync_parent_dir(&path);
                    inner.current_max_tx_id = final_max_tx_id;
                    let new_file_size = fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
                    inner.last_known_disk_state = (final_max_tx_id, new_file_size);
                    inner.is_dirty = false;
                    inner.dirty_generation = inner.dirty_generation.wrapping_add(1);
                    warnings.extend(inner.take_pending_warnings());
                }
            }

            (ret, watchers, warnings, events)
        };

        // Phase 4: 锁外派发通知与告警（无死锁）
        if !watchers.is_empty() {
            for ev in &events {
                for (_, w) in &watchers {
                    w(ev);
                }
            }
        }
        let on_warning = self.inner.read().options.on_warning.clone();
        if let Some(cb) = on_warning {
            for (code, msg) in warnings {
                cb(code, &msg);
            }
        }

        Ok(res)
    }

    fn put_internal(
        &self,
        ns: &str,
        key_bytes: &[u8],
        val_type: u8,
        val_bytes: Vec<u8>,
    ) -> Result<()> {
        let norm_ns = normalize_namespace(ns)?;
        validate_kv_lengths(key_bytes.len(), val_bytes.len())?;

        let entry = RawOpEntry {
            op_code: OP_PUT,
            value_type: val_type,
            namespace: norm_ns.as_bytes().to_vec(),
            key: key_bytes.to_vec(),
            value: val_bytes.clone(),
        };

        let ev = KeyChangeEvent {
            op: KeyChangeOp::Put,
            namespace: norm_ns.to_string(),
            key: String::from_utf8_lossy(key_bytes).into_owned(),
            value_type: val_type,
        };

        self.execute_mutation(|inner, rollback| {
            rollback
                .entry(norm_ns.as_bytes().to_vec())
                .or_insert_with(|| inner.data.get(norm_ns.as_bytes()).cloned());
            inner.mem_put(norm_ns.as_bytes(), key_bytes.to_vec(), val_type, val_bytes);
            Ok((vec![entry], vec![ev], true, ()))
        })
    }

    /// Put raw bytes into the specified namespace.
    pub fn put_bytes<K: AsRef<[u8]>, V: AsRef<[u8]>>(
        &self,
        ns: &str,
        key: K,
        val: V,
    ) -> Result<()> {
        self.put_internal(ns, key.as_ref(), VAL_TYPE_BYTES, val.as_ref().to_vec())
    }

    /// Put a UTF-8 string into the specified namespace.
    pub fn put_string<K: AsRef<[u8]>, S: AsRef<str>>(
        &self,
        ns: &str,
        key: K,
        val: S,
    ) -> Result<()> {
        self.put_internal(
            ns,
            key.as_ref(),
            VAL_TYPE_STRING,
            val.as_ref().as_bytes().to_vec(),
        )
    }

    /// Put a 64-bit signed integer into the specified namespace.
    pub fn put_i64<K: AsRef<[u8]>>(&self, ns: &str, key: K, val: i64) -> Result<()> {
        self.put_internal(ns, key.as_ref(), VAL_TYPE_INT64, encode_i64(val).to_vec())
    }

    /// Get a 64-bit signed integer for a key in the specified namespace.
    ///
    /// # Errors
    /// Returns `DbError::TypeMismatch` if the stored value is not an int64.
    pub fn get_i64<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<i64>> {
        let norm_ns = normalize_namespace(ns)?;
        let key_ref = key.as_ref();
        let inner = self.inner.read();
        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            if let Some((vtype, val)) = ns_map.get(key_ref) {
                if *vtype != VAL_TYPE_INT64 {
                    let key_disp = String::from_utf8_lossy(key_ref);
                    return Err(DbError::TypeMismatch(format!(
                        "Key '{}' has value_type {}, expected int64 ({})",
                        key_disp, vtype, VAL_TYPE_INT64
                    )));
                }
                return Ok(Some(decode_i64(val)?));
            }
        }
        Ok(None)
    }

    /// Put an IEEE 754 64-bit double-precision float into the specified namespace.
    /// NaN values are canonically normalized according to the v3.2 specification.
    pub fn put_f64<K: AsRef<[u8]>>(&self, ns: &str, key: K, val: f64) -> Result<()> {
        self.put_internal(ns, key.as_ref(), VAL_TYPE_FLOAT64, encode_f64(val).to_vec())
    }

    /// Get an IEEE 754 64-bit float for a key in the specified namespace.
    ///
    /// # Errors
    /// Returns `DbError::TypeMismatch` if the stored value is not a float64.
    pub fn get_f64<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<f64>> {
        let norm_ns = normalize_namespace(ns)?;
        let key_ref = key.as_ref();
        let inner = self.inner.read();
        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            if let Some((vtype, val)) = ns_map.get(key_ref) {
                if *vtype != VAL_TYPE_FLOAT64 {
                    let key_disp = String::from_utf8_lossy(key_ref);
                    return Err(DbError::TypeMismatch(format!(
                        "Key '{}' has value_type {}, expected float64 ({})",
                        key_disp, vtype, VAL_TYPE_FLOAT64
                    )));
                }
                return Ok(Some(decode_f64(val)?));
            }
        }
        Ok(None)
    }

    /// Put a boolean into the specified namespace.
    pub fn put_bool<K: AsRef<[u8]>>(&self, ns: &str, key: K, val: bool) -> Result<()> {
        self.put_internal(ns, key.as_ref(), VAL_TYPE_BOOL, encode_bool(val).to_vec())
    }

    /// Get a boolean for a key in the specified namespace.
    ///
    /// # Errors
    /// Returns `DbError::TypeMismatch` if the stored value is not a bool.
    pub fn get_bool<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<bool>> {
        let norm_ns = normalize_namespace(ns)?;
        let key_ref = key.as_ref();
        let inner = self.inner.read();
        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            if let Some((vtype, val)) = ns_map.get(key_ref) {
                if *vtype != VAL_TYPE_BOOL {
                    let key_disp = String::from_utf8_lossy(key_ref);
                    return Err(DbError::TypeMismatch(format!(
                        "Key '{}' has value_type {}, expected bool ({})",
                        key_disp, vtype, VAL_TYPE_BOOL
                    )));
                }
                return Ok(Some(decode_bool(val)?));
            }
        }
        Ok(None)
    }

    /// Atomically increment a 64-bit signed integer by delta.
    /// If the key does not exist, it is initialized to delta.
    /// If existing value is not an int64, returns `DbError::TypeMismatch`.
    pub fn increment_i64<K: AsRef<[u8]>>(&self, ns: &str, key: K, delta: i64) -> Result<i64> {
        let norm_ns = normalize_namespace(ns)?;
        let key_bytes = key.as_ref();
        let bytes_8 = encode_i64(delta);
        validate_kv_lengths(key_bytes.len(), bytes_8.len())?;

        self.execute_mutation(|inner, rollback| {
            let new_v = if let Some((vtype, val_bytes)) = inner
                .data
                .get(norm_ns.as_bytes())
                .and_then(|m| m.get(key_bytes))
            {
                if *vtype != VAL_TYPE_INT64 {
                    let key_disp = String::from_utf8_lossy(key_bytes);
                    return Err(DbError::TypeMismatch(format!(
                        "Key '{}' has value_type {}, expected int64 ({})",
                        key_disp, vtype, VAL_TYPE_INT64
                    )));
                }
                decode_i64(val_bytes)?.wrapping_add(delta)
            } else {
                delta
            };

            rollback
                .entry(norm_ns.as_bytes().to_vec())
                .or_insert_with(|| inner.data.get(norm_ns.as_bytes()).cloned());
            let encoded_new_v = encode_i64(new_v).to_vec();
            inner.mem_put(
                norm_ns.as_bytes(),
                key_bytes.to_vec(),
                VAL_TYPE_INT64,
                encoded_new_v.clone(),
            );

            let entry = RawOpEntry {
                op_code: OP_PUT,
                value_type: VAL_TYPE_INT64,
                namespace: norm_ns.as_bytes().to_vec(),
                key: key_bytes.to_vec(),
                value: encoded_new_v,
            };

            let ev = KeyChangeEvent {
                op: KeyChangeOp::Put,
                namespace: norm_ns.to_string(),
                key: String::from_utf8_lossy(key_bytes).into_owned(),
                value_type: VAL_TYPE_INT64,
            };

            Ok((vec![entry], vec![ev], true, new_v))
        })
    }

    /// Delete a key from the specified namespace.
    ///
    /// Returns `true` if the key existed and was actually deleted,
    /// `false` if the key did not exist (no-op).
    pub fn delete<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<bool> {
        let norm_ns = normalize_namespace(ns)?;
        let key_bytes = key.as_ref();

        self.execute_mutation(|inner, rollback| {
            let key_present = inner
                .data
                .get(norm_ns.as_bytes())
                .is_some_and(|ns_arc| ns_arc.contains_key(key_bytes));

            if !key_present {
                return Ok((Vec::new(), Vec::new(), false, false));
            }

            rollback
                .entry(norm_ns.as_bytes().to_vec())
                .or_insert_with(|| inner.data.get(norm_ns.as_bytes()).cloned());
            inner.mem_delete(norm_ns.as_bytes(), key_bytes);

            let entry = RawOpEntry {
                op_code: OP_DELETE,
                value_type: VAL_TYPE_BYTES,
                namespace: norm_ns.as_bytes().to_vec(),
                key: key_bytes.to_vec(),
                value: Vec::new(),
            };

            let ev = KeyChangeEvent {
                op: KeyChangeOp::Delete,
                namespace: norm_ns.to_string(),
                key: String::from_utf8_lossy(key_bytes).into_owned(),
                value_type: 0,
            };

            Ok((vec![entry], vec![ev], true, true))
        })
    }

    /// Atomically drop an entire namespace and all of its keys.
    ///
    /// Returns `true` if the namespace existed and was dropped.
    /// Notifies watchers with a single `DropNamespace` event (empty key).
    pub fn drop_namespace(&self, ns: &str) -> Result<bool> {
        let norm_ns = normalize_namespace(ns)?;

        self.execute_mutation(|inner, rollback| {
            if !inner.data.contains_key(norm_ns.as_bytes()) {
                return Ok((Vec::new(), Vec::new(), false, false));
            }

            rollback
                .entry(norm_ns.as_bytes().to_vec())
                .or_insert_with(|| inner.data.get(norm_ns.as_bytes()).cloned());
            inner.mem_drop(norm_ns.as_bytes());

            let entry = RawOpEntry {
                op_code: OP_DROP_NAMESPACE,
                value_type: VAL_TYPE_BYTES,
                namespace: norm_ns.as_bytes().to_vec(),
                key: Vec::new(),
                value: Vec::new(),
            };

            let ev = KeyChangeEvent {
                op: KeyChangeOp::DropNamespace,
                namespace: norm_ns.to_string(),
                key: String::new(),
                value_type: 0,
            };

            Ok((vec![entry], vec![ev], true, true))
        })
    }

    /// Clear all keys in a namespace while preserving the namespace itself.
    ///
    /// Returns `true` if the namespace existed and had at least one entry cleared.
    /// Notifies watchers with a single `Clear` event (empty key).
    pub fn clear_namespace(&self, ns: &str) -> Result<bool> {
        let norm_ns = normalize_namespace(ns)?;

        self.execute_mutation(|inner, rollback| {
            let keys_to_delete: Vec<Vec<u8>> = inner
                .data
                .get(norm_ns.as_bytes())
                .map(|ns_arc| ns_arc.keys().cloned().collect())
                .unwrap_or_default();

            if keys_to_delete.is_empty() {
                return Ok((Vec::new(), Vec::new(), false, false));
            }

            rollback
                .entry(norm_ns.as_bytes().to_vec())
                .or_insert_with(|| inner.data.get(norm_ns.as_bytes()).cloned());
            inner.mem_clear(norm_ns.as_bytes());

            let entries: Vec<RawOpEntry> = keys_to_delete
                .into_iter()
                .map(|k| RawOpEntry {
                    op_code: OP_DELETE,
                    value_type: VAL_TYPE_BYTES,
                    namespace: norm_ns.as_bytes().to_vec(),
                    key: k,
                    value: Vec::new(),
                })
                .collect();

            let ev = KeyChangeEvent {
                op: KeyChangeOp::Clear,
                namespace: norm_ns.to_string(),
                key: String::new(),
                value_type: 0,
            };

            Ok((entries, vec![ev], true, true))
        })
    }

    /// Asynchronously flush the in-memory state to disk on a background thread.
    pub fn flush_async(&self) -> std::thread::JoinHandle<Result<()>> {
        let db = self.clone();
        std::thread::spawn(move || db.flush())
    }

    /// Asynchronously backup the database to destination path on a background thread.
    pub fn backup_async<P: AsRef<Path>>(&self, dest: P) -> std::thread::JoinHandle<Result<()>> {
        let db = self.clone();
        let dest_buf = dest.as_ref().to_path_buf();
        std::thread::spawn(move || db.backup(&dest_buf))
    }

    /// Register a key change watcher callback.
    /// Invoked whenever a write/delete/batch transaction commits successfully.
    /// Register a key change watcher callback.
    ///
    /// Returns a [`WatcherHandle`]; dropping it (or calling
    /// [`WatcherHandle::unwatch`]) removes the callback. Keeping the handle alive
    /// keeps the subscription alive.
    ///
    /// Invoked whenever a write/delete/batch transaction commits successfully,
    /// always after the write lock has been fully released (deadlock-free contract).
    pub fn watch<F>(&self, callback: F) -> WatcherHandle
    where
        F: Fn(&KeyChangeEvent) + Send + Sync + 'static,
    {
        let mut inner = self.inner.write();
        let id = inner.next_watcher_id;
        inner.next_watcher_id += 1;
        inner.watchers.push((id, Arc::new(callback)));
        WatcherHandle {
            inner: Arc::downgrade(&self.inner),
            id,
        }
    }

    /// List all namespaces in unsigned UTF-8 byte order.
    ///
    /// The default namespace is returned as `""`. Namespaces that still exist but
    /// hold no entries (after `clear_namespace`) are included, because
    /// `clear_namespace` preserves the namespace itself.
    ///
    /// Namespace names that are not valid UTF-8 (only possible when introduced by an
    /// externally crafted file) are skipped, matching the behaviour of `keys()`.
    pub fn namespaces(&self) -> Vec<String> {
        let inner = self.inner.read();
        inner
            .data
            .keys()
            .filter_map(|ns| String::from_utf8(ns.clone()).ok())
            .collect()
    }

    /// Physical size of the database file in bytes; 0 if nothing has been flushed yet.
    pub fn file_size(&self) -> u64 {
        fs::metadata(&self.inner.read().path)
            .map(|m| m.len())
            .unwrap_or(0)
    }

    /// Aggregate in-memory statistics. Never touches the disk and never mutates state.
    pub fn stats(&self) -> DbStats {
        let inner = self.inner.read();
        let mut total_entries = 0usize;
        for ns_map in inner.data.values() {
            total_entries += ns_map.len();
        }
        DbStats {
            file_size: fs::metadata(&inner.path).map(|m| m.len()).unwrap_or(0),
            namespace_count: inner.data.len(),
            total_entries,
            current_max_tx_id: inner.current_max_tx_id,
        }
    }

    /// Write the current in-memory state to `dest` as a standalone database file.
    ///
    /// The produced file is byte-compatible with a normal database and can be opened
    /// by any CKV implementation. The write goes through `dest + ".tmp"` + fsync +
    /// atomic rename, and **never touches this database's `.bak`**.
    ///
    /// Allowed in read-only mode: backing up does not modify the source database.
    pub fn backup(&self, dest: &Path) -> Result<()> {
        // M3-11：备份到主库自身（或它的 .tmp/.bak）会与写路径争抢同一个文件，
        // 轻则产物互相覆盖，重则把正在使用的库破坏成半个快照（Go 侧已同构校验）。
        // 用 `absolute` 而不是 `canonicalize`：目标文件通常还不存在，canonicalize 会
        // 失败并回退成未规范化的相对路径，导致与 `\?\C:\...` 形式的主库路径比不出来。
        let abs_dest = std::path::absolute(dest).unwrap_or_else(|_| dest.to_path_buf());
        let abs_self = {
            let inner = self.inner.read();
            std::path::absolute(&inner.path).unwrap_or_else(|_| inner.path.clone())
        };
        let self_tmp = format!("{}.tmp", abs_self.display());
        let self_bak = format!("{}.bak", abs_self.display());
        if abs_dest == abs_self
            || abs_dest.as_os_str() == self_tmp.as_str()
            || abs_dest.as_os_str() == self_bak.as_str()
        {
            return Err(DbError::Io(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                format!(
                    "Backup destination '{}' collides with the database file",
                    dest.display()
                ),
            )));
        }
        let dest_tmp = PathBuf::from(format!("{}.tmp", dest.display()));
        let (entries, tx_id) = {
            let inner = self.inner.read();
            let mut entries: Vec<RawOpEntry> = Vec::new();
            for (ns_bytes, ns_map) in inner.data.iter() {
                for (key_bytes, (vtype, val_bytes)) in ns_map.iter() {
                    entries.push(RawOpEntry {
                        op_code: OP_PUT,
                        value_type: *vtype,
                        namespace: ns_bytes.clone(),
                        key: key_bytes.clone(),
                        value: val_bytes.clone(),
                    });
                }
            }
            (entries, inner.current_max_tx_id)
        };

        let mut tmp_file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(true)
            .open(&dest_tmp)?;
        tmp_file.write_all(&FileHeader::create_bytes())?;

        let mut final_max_tx_id = tx_id;
        if !entries.is_empty() {
            let mut current_tx_id = final_max_tx_id + 1;
            let mut frame: Vec<RawOpEntry> = Vec::new();
            let mut payload_len = 0usize;
            for entry in entries {
                let entry_size = entry.encoded_len();
                if frame.len() >= MAX_OPS_PER_FRAME || (payload_len + entry_size > MAX_PAYLOAD_LEN)
                {
                    BatchFrameWriter::write_frame(&mut tmp_file, current_tx_id, &frame)?;
                    current_tx_id += 1;
                    frame.clear();
                    payload_len = 0;
                }
                payload_len += entry_size;
                frame.push(entry);
            }
            if !frame.is_empty() {
                BatchFrameWriter::write_frame(&mut tmp_file, current_tx_id, &frame)?;
                final_max_tx_id = current_tx_id;
            }
        }

        tmp_file.sync_all()?;
        drop(tmp_file);

        // Best-effort directory fsync is not applicable to a user-supplied destination;
        // the rename itself is atomic and that is what guarantees the backup's integrity.
        rename_with_retry(&dest_tmp, dest)?;
        let _ = final_max_tx_id;
        Ok(())
    }

    /// Explicitly flush the in-memory state to disk.
    ///
    /// In Immediate sync mode, this ensures all writes are durable.
    /// In Deferred/Manual sync mode, flushes pending dirty writes to disk.
    pub fn flush(&self) -> Result<()> {
        // FIX-01: serialise with the background flush, which writes the same `.tmp`.
        // Lock order: flush_mutex -> RwLock.
        let flush_mutex = {
            let inner = self.inner.read();
            inner.flush_mutex.clone()
        };
        let _flush_guard = flush_mutex.lock();

        let mut inner = self.inner.write();
        inner.check_writable()?;
        if !inner.is_dirty && inner.path.exists() {
            return Ok(());
        }
        if inner.options.engine_mode == StorageEngineMode::AppendOnlyLog {
            if inner.path.exists() {
                let f = OpenOptions::new().write(true).open(&inner.path)?;
                f.sync_all()?;
            }
            inner.is_dirty = false;
            Ok(())
        } else {
            let disk_max_tx_id = inner.precheck_disk_state()?;
            inner.commit_flush(disk_max_tx_id)
        }
    }

    /// Return all keys matching an optional prefix in unsigned UTF-8 byte order.
    pub fn keys(&self, ns: &str, prefix: Option<&str>) -> Result<Vec<String>> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read();
        let mut result = Vec::new();

        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            let prefix_bytes = prefix.unwrap_or("").as_bytes();
            for (k, _) in ns_map.iter() {
                if k.starts_with(prefix_bytes) {
                    if let Ok(s) = String::from_utf8(k.clone()) {
                        result.push(s);
                    }
                }
            }
        }
        Ok(result)
    }

    /// Scan a range `[start_key, end_key)` in the specified namespace.
    ///
    /// Sorted strictly by unsigned UTF-8 lexicographical byte order.
    pub fn scan_range(
        &self,
        ns: &str,
        start_key: Option<&str>,
        end_key: Option<&str>,
    ) -> Result<Vec<Entry>> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read();
        let mut result = Vec::new();

        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            let start = start_key.map(|s| s.as_bytes());
            let end = end_key.map(|s| s.as_bytes());

            if let (Some(s), Some(e)) = (start, end) {
                if s >= e {
                    return Ok(result);
                }
            }

            for (k, (vtype, val)) in ns_map.iter() {
                if let Some(s) = start {
                    if k.as_slice() < s {
                        continue;
                    }
                }
                if let Some(e) = end {
                    if k.as_slice() >= e {
                        break;
                    }
                }
                // scan_range 按字节序枚举条目，规范未要求在此跳过非法 UTF-8
                // （§3.3.3 只约束 keys / iter_prefix；v1.0 冻结行为见 api_v1_freeze_test）。
                let key_str = String::from_utf8_lossy(k).into_owned();
                result.push(Entry::new(
                    norm_ns.to_string(),
                    key_str,
                    *vtype,
                    val.clone(),
                ));
            }
        }
        Ok(result)
    }

    /// Create a lazy streaming iterator over entries matching prefix in the specified namespace.
    pub fn iter_prefix(&self, ns: &str, prefix: Option<&str>) -> Result<RangeIter> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read();
        let mut entries = Vec::new();

        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            let pfx = prefix.unwrap_or("").as_bytes();
            for (k, (vtype, val)) in ns_map.iter() {
                if k.starts_with(pfx) {
                    // 同上：非法 UTF-8 跳过，保持与 keys()/scan_range 同一口径。
                    let key_str = match std::str::from_utf8(k) {
                        Ok(s) => s.to_string(),
                        Err(_) => continue,
                    };
                    entries.push(Entry::new(
                        norm_ns.to_string(),
                        key_str,
                        *vtype,
                        val.clone(),
                    ));
                }
            }
        }

        Ok(RangeIter::new(entries))
    }

    /// Create a new batch builder.
    pub fn new_batch(&self) -> Batch {
        Batch::new()
    }

    /// Write an accumulated batch atomically.
    pub fn write_batch(&self, batch: Batch) -> Result<()> {
        if batch.ops.is_empty() {
            return Ok(());
        }

        self.execute_mutation(|inner, rollback| {
            let mut raw_entries: Vec<RawOpEntry> = Vec::new();
            for op in &batch.ops {
                match op {
                    BatchOp::Put {
                        namespace,
                        key,
                        val_type,
                        val,
                    } => {
                        raw_entries.push(RawOpEntry {
                            op_code: OP_PUT,
                            value_type: *val_type,
                            namespace: namespace.clone(),
                            key: key.clone(),
                            value: val.clone(),
                        });
                    }
                    BatchOp::Delete { namespace, key } => {
                        raw_entries.push(RawOpEntry {
                            op_code: OP_DELETE,
                            value_type: VAL_TYPE_BYTES,
                            namespace: namespace.clone(),
                            key: key.clone(),
                            value: Vec::new(),
                        });
                    }
                    BatchOp::DropNamespace { namespace } => {
                        raw_entries.push(RawOpEntry {
                            op_code: OP_DROP_NAMESPACE,
                            value_type: VAL_TYPE_BYTES,
                            namespace: namespace.clone(),
                            key: Vec::new(),
                            value: Vec::new(),
                        });
                    }
                    BatchOp::ClearNamespace { namespace } => {
                        raw_entries.retain(|entry| {
                            entry.op_code != OP_PUT || entry.namespace != *namespace
                        });

                        if let Some(ns_arc) = inner.data.get(namespace) {
                            for k in ns_arc.keys() {
                                raw_entries.push(RawOpEntry {
                                    op_code: OP_DELETE,
                                    value_type: VAL_TYPE_BYTES,
                                    namespace: namespace.clone(),
                                    key: k.clone(),
                                    value: Vec::new(),
                                });
                            }
                        }
                    }
                }
            }

            let mut events = Vec::new();
            for op in batch.ops {
                match op {
                    BatchOp::Put {
                        namespace,
                        key,
                        val_type,
                        val,
                    } => {
                        rollback
                            .entry(namespace.clone())
                            .or_insert_with(|| inner.data.get(&namespace).cloned());
                        events.push(KeyChangeEvent {
                            op: KeyChangeOp::Put,
                            namespace: String::from_utf8_lossy(&namespace).into_owned(),
                            key: String::from_utf8_lossy(&key).into_owned(),
                            value_type: val_type,
                        });
                        inner.mem_put(&namespace, key, val_type, val);
                    }
                    BatchOp::Delete { namespace, key } => {
                        let present = inner
                            .data
                            .get(&namespace)
                            .is_some_and(|m| m.contains_key(&key));
                        if present {
                            rollback
                                .entry(namespace.clone())
                                .or_insert_with(|| inner.data.get(&namespace).cloned());
                            inner.mem_delete(&namespace, &key);
                            events.push(KeyChangeEvent {
                                op: KeyChangeOp::Delete,
                                namespace: String::from_utf8_lossy(&namespace).into_owned(),
                                key: String::from_utf8_lossy(&key).into_owned(),
                                value_type: 0,
                            });
                        }
                    }
                    BatchOp::DropNamespace { namespace } => {
                        if inner.data.contains_key(&namespace) {
                            rollback
                                .entry(namespace.clone())
                                .or_insert_with(|| inner.data.get(&namespace).cloned());
                            inner.mem_drop(&namespace);
                            events.push(KeyChangeEvent {
                                op: KeyChangeOp::DropNamespace,
                                namespace: String::from_utf8_lossy(&namespace).into_owned(),
                                key: String::new(),
                                value_type: 0,
                            });
                        }
                    }
                    BatchOp::ClearNamespace { namespace } => {
                        let has_keys = inner.data.get(&namespace).is_some_and(|m| !m.is_empty());
                        if has_keys {
                            rollback
                                .entry(namespace.clone())
                                .or_insert_with(|| inner.data.get(&namespace).cloned());
                            inner.mem_clear(&namespace);
                            events.push(KeyChangeEvent {
                                op: KeyChangeOp::Clear,
                                namespace: String::from_utf8_lossy(&namespace).into_owned(),
                                key: String::new(),
                                value_type: 0,
                            });
                        }
                    }
                }
            }
            Ok((raw_entries, events, true, ()))
        })
    }

    /// Execute operations inside a closure atomically with pure in-memory staging rollback.
    pub fn batch<F>(&self, f: F) -> Result<()>
    where
        F: FnOnce(&mut Batch) -> Result<()>,
    {
        let mut batch = Batch::with_db(self.inner.clone());
        f(&mut batch)?;
        self.write_batch(batch)
    }

    /// Rewrite the entire database into the most compact canonical snapshot file.
    pub fn compact(&self) -> Result<()> {
        // FIX-01: same `.tmp` as the background flush.
        let flush_mutex = {
            let inner = self.inner.read();
            inner.flush_mutex.clone()
        };
        let _flush_guard = flush_mutex.lock();

        let mut inner = self.inner.write();
        // M3-01: `compact()` 是唯一一条不经过 `write_batch` 的整库重写路径，原先只靠
        // `precheck_disk_state()` 拦只读、不拦关闭态。于是 `close()` 之后调用 compact()
        // 会重新创建/覆盖数据库文件——已释放的跨进程锁不可能被重新持有，另一个进程可以
        // 合法打开同名库，形成双写者。关闭检查必须与其它写路径一致。
        inner.check_writable()?;
        let disk_max_tx_id = inner.precheck_disk_state()?;
        inner.commit_flush(disk_max_tx_id)
    }
}

/// A streaming double-ended iterator over entries.
pub struct RangeIter {
    entries: Vec<Entry>,
    pos: usize,
    end_pos: usize,
}

impl RangeIter {
    pub fn new(entries: Vec<Entry>) -> Self {
        let len = entries.len();
        Self {
            entries,
            pos: 0,
            end_pos: len,
        }
    }
}

impl Iterator for RangeIter {
    type Item = Entry;

    fn next(&mut self) -> Option<Self::Item> {
        if self.pos < self.end_pos {
            let item = self.entries[self.pos].clone();
            self.pos += 1;
            Some(item)
        } else {
            None
        }
    }

    fn size_hint(&self) -> (usize, Option<usize>) {
        let remaining = self.end_pos.saturating_sub(self.pos);
        (remaining, Some(remaining))
    }
}

impl DoubleEndedIterator for RangeIter {
    fn next_back(&mut self) -> Option<Self::Item> {
        if self.pos < self.end_pos {
            self.end_pos -= 1;
            Some(self.entries[self.end_pos].clone())
        } else {
            None
        }
    }
}

#[derive(Clone, Debug)]
pub enum BatchOp {
    Put {
        namespace: Vec<u8>,
        key: Vec<u8>,
        val_type: u8,
        val: Vec<u8>,
    },
    Delete {
        namespace: Vec<u8>,
        key: Vec<u8>,
    },
    DropNamespace {
        namespace: Vec<u8>,
    },
    ClearNamespace {
        namespace: Vec<u8>,
    },
}

/// An atomic batch builder.
#[derive(Default, Clone)]
pub struct Batch {
    ops: Vec<BatchOp>,
    /// Handle used for the read-your-own-writes fallback. `None` for a batch
    /// built with `Batch::new()`; such a batch can still see its own staged
    /// writes but has no database state to fall back to.
    db: Option<Arc<RwLock<DbInner>>>,
}

impl std::fmt::Debug for Batch {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Batch")
            .field("ops", &self.ops.len())
            .field("has_db", &self.db.is_some())
            .finish()
    }
}

impl Batch {
    pub fn new() -> Self {
        Self {
            ops: Vec::new(),
            db: None,
        }
    }

    fn with_db(db: Arc<RwLock<DbInner>>) -> Self {
        Self {
            ops: Vec::new(),
            db: Some(db),
        }
    }

    /// 获取绑定到指定命名空间的批处理门面。
    pub fn ns<'a>(&'a mut self, ns: &'a str) -> BatchNamespaceScope<'a> {
        BatchNamespaceScope { batch: self, ns }
    }

    /// 获取默认命名空间的批处理门面。
    pub fn default_ns(&mut self) -> BatchNamespaceScope<'_> {
        self.ns("")
    }

    /// Reads a key as the batch sees it: staged writes win, a staged tombstone
    /// (delete / clear / drop) reports "absent" even when the database still
    /// holds the key, and only a key untouched by this batch falls through to
    /// the committed database state.
    ///
    /// The fallback takes the database read lock, so it observes commits made by
    /// other threads *during* the batch. A caller that needs a frozen base can
    /// take `snapshot()` first; staging a copy through the batch is the cheaper
    /// route when only a few keys are involved.
    ///
    /// Staging is scanned back to front, so the most recent operation wins and
    /// `clear_namespace` / `drop_namespace` hide everything staged before them.
    pub fn get_bytes(&self, ns: &str, key: &[u8]) -> Result<Option<Vec<u8>>> {
        let norm_ns = normalize_namespace(ns)?;
        let ns_bytes = norm_ns.as_bytes();
        for op in self.ops.iter().rev() {
            match op {
                BatchOp::Put {
                    namespace,
                    key: op_key,
                    val,
                    ..
                } if namespace.as_slice() == ns_bytes && op_key.as_slice() == key => {
                    return Ok(Some(val.clone()));
                }
                BatchOp::Delete {
                    namespace,
                    key: op_key,
                } if namespace.as_slice() == ns_bytes && op_key.as_slice() == key => {
                    return Ok(None);
                }
                BatchOp::ClearNamespace { namespace } | BatchOp::DropNamespace { namespace }
                    if namespace.as_slice() == ns_bytes =>
                {
                    return Ok(None);
                }
                _ => {}
            }
        }

        match &self.db {
            Some(db) => {
                let inner = db.read();
                Ok(inner
                    .data
                    .get(ns_bytes)
                    .and_then(|ns_map| ns_map.get(key))
                    .map(|(_, value)| value.clone()))
            }
            None => Ok(None),
        }
    }

    /// String flavour of [`Batch::get_bytes`]; invalid UTF-8 is rejected rather
    /// than lossily converted, matching `Db::get_string`.
    pub fn get_string(&self, ns: &str, key: &[u8]) -> Result<Option<String>> {
        match self.get_bytes(ns, key)? {
            Some(bytes) => match String::from_utf8(bytes) {
                Ok(s) => Ok(Some(s)),
                Err(_) => Err(DbError::InvalidUtf8(format!(
                    "Invalid UTF-8 in value of key '{}'",
                    String::from_utf8_lossy(key)
                ))),
            },
            None => Ok(None),
        }
    }

    pub fn put_bytes<K: AsRef<[u8]>, V: AsRef<[u8]>>(
        &mut self,
        ns: &str,
        key: K,
        val: V,
    ) -> Result<&mut Self> {
        let norm_ns = normalize_namespace(ns)?;
        let key_bytes = key.as_ref();
        let val_bytes = val.as_ref();
        validate_kv_lengths(key_bytes.len(), val_bytes.len())?;
        self.ops.push(BatchOp::Put {
            namespace: norm_ns.as_bytes().to_vec(),
            key: key_bytes.to_vec(),
            val_type: VAL_TYPE_BYTES,
            val: val_bytes.to_vec(),
        });
        Ok(self)
    }

    pub fn put_string<K: AsRef<[u8]>, S: AsRef<str>>(
        &mut self,
        ns: &str,
        key: K,
        val: S,
    ) -> Result<&mut Self> {
        let norm_ns = normalize_namespace(ns)?;
        let key_bytes = key.as_ref();
        let val_str = val.as_ref();
        validate_kv_lengths(key_bytes.len(), val_str.len())?;
        self.ops.push(BatchOp::Put {
            namespace: norm_ns.as_bytes().to_vec(),
            key: key_bytes.to_vec(),
            val_type: VAL_TYPE_STRING,
            val: val_str.as_bytes().to_vec(),
        });
        Ok(self)
    }

    pub fn put_i64<K: AsRef<[u8]>>(&mut self, ns: &str, key: K, val: i64) -> Result<&mut Self> {
        let norm_ns = normalize_namespace(ns)?;
        let key_bytes = key.as_ref();
        let bytes = encode_i64(val);
        validate_kv_lengths(key_bytes.len(), bytes.len())?;
        self.ops.push(BatchOp::Put {
            namespace: norm_ns.as_bytes().to_vec(),
            key: key_bytes.to_vec(),
            val_type: VAL_TYPE_INT64,
            val: bytes.to_vec(),
        });
        Ok(self)
    }

    pub fn put_f64<K: AsRef<[u8]>>(&mut self, ns: &str, key: K, val: f64) -> Result<&mut Self> {
        let norm_ns = normalize_namespace(ns)?;
        let key_bytes = key.as_ref();
        let bytes = encode_f64(val);
        validate_kv_lengths(key_bytes.len(), bytes.len())?;
        self.ops.push(BatchOp::Put {
            namespace: norm_ns.as_bytes().to_vec(),
            key: key_bytes.to_vec(),
            val_type: VAL_TYPE_FLOAT64,
            val: bytes.to_vec(),
        });
        Ok(self)
    }

    pub fn put_bool<K: AsRef<[u8]>>(&mut self, ns: &str, key: K, val: bool) -> Result<&mut Self> {
        let norm_ns = normalize_namespace(ns)?;
        let key_bytes = key.as_ref();
        let bytes = encode_bool(val);
        validate_kv_lengths(key_bytes.len(), bytes.len())?;
        self.ops.push(BatchOp::Put {
            namespace: norm_ns.as_bytes().to_vec(),
            key: key_bytes.to_vec(),
            val_type: VAL_TYPE_BOOL,
            val: bytes.to_vec(),
        });
        Ok(self)
    }

    pub fn delete<K: AsRef<[u8]>>(&mut self, ns: &str, key: K) -> Result<&mut Self> {
        let norm_ns = normalize_namespace(ns)?;
        self.ops.push(BatchOp::Delete {
            namespace: norm_ns.as_bytes().to_vec(),
            key: key.as_ref().to_vec(),
        });
        Ok(self)
    }

    pub fn drop_namespace(&mut self, ns: &str) -> Result<&mut Self> {
        let norm_ns = normalize_namespace(ns)?;
        self.ops.push(BatchOp::DropNamespace {
            namespace: norm_ns.as_bytes().to_vec(),
        });
        Ok(self)
    }

    pub fn clear_namespace(&mut self, ns: &str) -> Result<&mut Self> {
        let norm_ns = normalize_namespace(ns)?;
        self.ops.push(BatchOp::ClearNamespace {
            namespace: norm_ns.as_bytes().to_vec(),
        });
        Ok(self)
    }

    pub fn len(&self) -> usize {
        self.ops.len()
    }

    pub fn is_empty(&self) -> bool {
        self.ops.is_empty()
    }

    pub fn clear(&mut self) {
        self.ops.clear();
    }
}

// Backward compatible alias
pub type BatchWriter = Batch;

/// An immutable, point-in-time snapshot of the database.
///
/// Created via [`Db::snapshot`]. Readers access a frozen view of the data without
/// acquiring locks on the main database, ensuring that long-running readers do not
/// block writers, and concurrent writes do not block or corrupt snapshot readers.
#[derive(Clone)]
pub struct Snapshot {
    tx_id: u64,
    data: DataMap,
}

impl Snapshot {
    /// Return the logical transaction ID (TxID) of this snapshot.
    pub fn tx_id(&self) -> u64 {
        self.tx_id
    }

    /// 获取绑定到指定命名空间的快照门面。
    pub fn ns<'a>(&'a self, ns: &'a str) -> SnapshotNamespaceScope<'a> {
        SnapshotNamespaceScope { snapshot: self, ns }
    }

    /// 获取默认命名空间的快照门面。
    pub fn default_ns(&self) -> SnapshotNamespaceScope<'_> {
        self.ns("")
    }

    /// List all namespaces visible in this snapshot, in unsigned UTF-8 byte order.
    ///
    /// Non-UTF-8 namespace names are skipped, matching [`Db::namespaces`].
    pub fn namespaces(&self) -> Vec<String> {
        self.data
            .keys()
            .filter_map(|ns| String::from_utf8(ns.clone()).ok())
            .collect()
    }

    /// Check if a key exists in the specified namespace.
    pub fn contains_key<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<bool> {
        let norm_ns = normalize_namespace(ns)?;
        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            Ok(ns_map.contains_key(key.as_ref()))
        } else {
            Ok(false)
        }
    }

    /// Return the number of valid keys in the specified namespace.
    pub fn count(&self, ns: &str) -> Result<usize> {
        let norm_ns = normalize_namespace(ns)?;
        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            Ok(ns_map.len())
        } else {
            Ok(0)
        }
    }

    /// Get raw bytes for a key in the specified namespace.
    pub fn get_bytes<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<Vec<u8>>> {
        let norm_ns = normalize_namespace(ns)?;
        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            if let Some((_, val)) = ns_map.get(key.as_ref()) {
                return Ok(Some(val.clone()));
            }
        }
        Ok(None)
    }

    /// Get a UTF-8 string for a key in the specified namespace.
    pub fn get_string<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<String>> {
        let norm_ns = normalize_namespace(ns)?;
        let key_ref = key.as_ref();
        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            if let Some((vtype, val)) = ns_map.get(key_ref) {
                if *vtype != VAL_TYPE_STRING {
                    let key_disp = String::from_utf8_lossy(key_ref);
                    return Err(DbError::TypeMismatch(format!(
                        "Key '{}' has value_type {}, expected string ({})",
                        key_disp, vtype, VAL_TYPE_STRING
                    )));
                }
                let s = std::str::from_utf8(val).map_err(|_| {
                    DbError::InvalidUtf8(format!(
                        "Invalid UTF-8 in key '{}'",
                        String::from_utf8_lossy(key_ref)
                    ))
                })?;
                return Ok(Some(s.to_string()));
            }
        }
        Ok(None)
    }

    /// Get a 64-bit signed integer for a key in the specified namespace.
    ///
    /// # Errors
    /// Returns `DbError::TypeMismatch` if the stored value is not an int64.
    pub fn get_i64<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<i64>> {
        let norm_ns = normalize_namespace(ns)?;
        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            if let Some((vtype, val)) = ns_map.get(key.as_ref()) {
                if *vtype != VAL_TYPE_INT64 {
                    let key_disp = String::from_utf8_lossy(key.as_ref());
                    return Err(DbError::TypeMismatch(format!(
                        "Key '{}' has value_type {}, expected int64 ({})",
                        key_disp, vtype, VAL_TYPE_INT64
                    )));
                }
                return Ok(Some(decode_i64(val)?));
            }
        }
        Ok(None)
    }

    /// Get an IEEE 754 64-bit float for a key in the specified namespace.
    ///
    /// # Errors
    /// Returns `DbError::TypeMismatch` if the stored value is not a float64.
    pub fn get_f64<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<f64>> {
        let norm_ns = normalize_namespace(ns)?;
        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            if let Some((vtype, val)) = ns_map.get(key.as_ref()) {
                if *vtype != VAL_TYPE_FLOAT64 {
                    let key_disp = String::from_utf8_lossy(key.as_ref());
                    return Err(DbError::TypeMismatch(format!(
                        "Key '{}' has value_type {}, expected float64 ({})",
                        key_disp, vtype, VAL_TYPE_FLOAT64
                    )));
                }
                return Ok(Some(decode_f64(val)?));
            }
        }
        Ok(None)
    }

    /// Get a boolean for a key in the specified namespace.
    ///
    /// # Errors
    /// Returns `DbError::TypeMismatch` if the stored value is not a bool.
    pub fn get_bool<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<Option<bool>> {
        let norm_ns = normalize_namespace(ns)?;
        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            if let Some((vtype, val)) = ns_map.get(key.as_ref()) {
                if *vtype != VAL_TYPE_BOOL {
                    let key_disp = String::from_utf8_lossy(key.as_ref());
                    return Err(DbError::TypeMismatch(format!(
                        "Key '{}' has value_type {}, expected bool ({})",
                        key_disp, vtype, VAL_TYPE_BOOL
                    )));
                }
                return Ok(Some(decode_bool(val)?));
            }
        }
        Ok(None)
    }

    /// Return all keys matching an optional prefix in unsigned UTF-8 byte order.
    pub fn keys(&self, ns: &str, prefix: Option<&str>) -> Result<Vec<String>> {
        let norm_ns = normalize_namespace(ns)?;
        let mut result = Vec::new();
        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            let prefix_bytes = prefix.unwrap_or("").as_bytes();
            for (k, _) in ns_map.iter() {
                if k.starts_with(prefix_bytes) {
                    if let Ok(s) = String::from_utf8(k.clone()) {
                        result.push(s);
                    }
                }
            }
        }
        Ok(result)
    }

    /// Scan a range `[start_key, end_key)` in the specified namespace.
    pub fn scan_range(
        &self,
        ns: &str,
        start_key: Option<&str>,
        end_key: Option<&str>,
    ) -> Result<Vec<Entry>> {
        let norm_ns = normalize_namespace(ns)?;
        let mut result = Vec::new();
        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            let start = start_key.map(|s| s.as_bytes());
            let end = end_key.map(|s| s.as_bytes());

            if let (Some(s), Some(e)) = (start, end) {
                if s >= e {
                    return Ok(result);
                }
            }

            for (k, (vtype, val)) in ns_map.iter() {
                if let Some(s) = start {
                    if k.as_slice() < s {
                        continue;
                    }
                }
                if let Some(e) = end {
                    if k.as_slice() >= e {
                        break;
                    }
                }
                // 同 Db::scan_range：按字节序枚举，不在此跳过。
                let key_str = String::from_utf8_lossy(k).into_owned();
                result.push(Entry::new(
                    norm_ns.to_string(),
                    key_str,
                    *vtype,
                    val.clone(),
                ));
            }
        }
        Ok(result)
    }

    /// Return a streaming iterator over entries in range `[start_key, end_key)`.
    pub fn iter_range(
        &self,
        ns: &str,
        start_key: Option<&str>,
        end_key: Option<&str>,
    ) -> Result<RangeIter> {
        let entries = self.scan_range(ns, start_key, end_key)?;
        Ok(RangeIter::new(entries))
    }

    /// Create a lazy streaming iterator over entries matching prefix in the specified namespace.
    pub fn iter_prefix(&self, ns: &str, prefix: Option<&str>) -> Result<RangeIter> {
        let norm_ns = normalize_namespace(ns)?;
        let mut entries = Vec::new();

        if let Some(ns_map) = self.data.get(norm_ns.as_bytes()) {
            let pfx = prefix.map(|p| p.as_bytes());
            for (k, (vtype, val)) in ns_map.iter() {
                if let Some(p) = pfx {
                    if !k.starts_with(p) {
                        continue;
                    }
                }
                if let Ok(key_str) = String::from_utf8(k.clone()) {
                    entries.push(Entry::new(
                        norm_ns.to_string(),
                        key_str,
                        *vtype,
                        val.clone(),
                    ));
                }
            }
        }
        Ok(RangeIter::new(entries))
    }
}

impl DbInner {
    /// Opens the database.
    ///
    /// The caller has already acquired the inter-process lock and holds it for the
    /// whole lifetime of the connection (C9), so every repair performed here --
    /// promoting or deleting `.tmp`, healing a torn log tail, rolling a `.bak`
    /// forward -- writes to the database files while no other process can append to
    /// them. That is what used to be C10: the tail healing ran without the lock and
    /// could truncate frames a concurrent appender had just written.
    fn init_and_recover(&mut self) -> Result<()> {
        let tmp_path = PathBuf::from(format!("{}.tmp", self.path.display()));
        let bak_path = PathBuf::from(format!("{}.bak", self.path.display()));

        // Step 1: Check and clean unfinished temporary file
        if tmp_path.exists() {
            if !self.path.exists() && verify_file_integrity(&tmp_path).is_ok() {
                // System crashed between rotating to .bak and committing .tmp, promote .tmp
                let _ = rename_with_retry(&tmp_path, &self.path);
            } else {
                let _ = fs::remove_file(&tmp_path);
            }
        }

        // Step 2: Try loading primary db file
        if self.path.exists() {
            match self.load_file(&self.path) {
                Ok((entries, max_tx_id, file_size)) => {
                    self.apply_entries(entries)?;
                    self.current_max_tx_id = max_tx_id;
                    self.last_known_disk_state = (max_tx_id, file_size);
                    return Ok(());
                }
                Err(err) => {
                    // Corrupted primary file. Decision D1: `.bak` holds the
                    // generation *before* the last rotation, so replacing the primary
                    // with it silently rolls back every write made since. It is used
                    // only when the caller explicitly opted in; otherwise the error
                    // reports where that copy is kept so an operator can recover.
                    let bak_available = bak_path.exists();

                    // D1-9: a read-only open never modifies data files, so automatic
                    // recovery is unavailable there regardless of the flag.
                    if self.options.auto_recover_from_backup
                        && !self.options.read_only
                        && bak_available
                    {
                        // `.bak` is a recovery source: never tail-truncate it (G1).
                        let bak_load = self.load_file_with(&bak_path, false);
                        if let Ok((entries, max_tx_id, file_size)) = bak_load {
                            // Recover by copying .bak to primary file
                            fs::copy(&bak_path, &self.path)?;
                            if let Some(cb) = &self.options.on_warning {
                                cb(
                                    "onRecoveredFromBackup",
                                    "Recovered database state from .bak",
                                );
                            }
                            self.apply_entries(entries)?;
                            self.current_max_tx_id = max_tx_id;
                            self.last_known_disk_state = (max_tx_id, file_size);
                            return Ok(());
                        }
                        return Err(DbError::Corrupted(format!(
							"{err}; the previous generation kept at '{}' could not be loaded either",
							bak_path.display()
						)));
                    }

                    if bak_available {
                        if let DbError::Corrupted(_) = &err {
                            return Err(DbError::Corrupted(format!(
                                "{err}; a previous generation is kept at '{}': copy it over the \
								 primary file to recover, or open with auto_recover_from_backup = true",
                                bak_path.display()
                            )));
                        }
                    }

                    return Err(err);
                }
            }
        }

        // Branch ③: primary does not exist but a previous generation does.
        //
        // This state cannot be produced by an interrupted rotation -- that always
        // leaves a complete `.tmp` behind, which step 1 promotes -- so it means the
        // primary file disappeared (deleted or replaced externally). Promoting the
        // `.bak` on top of it is a silent downgrade to an older generation, and when
        // the `.bak` itself is unreadable the open used to succeed as an *empty*
        // database: the fourth silent data loss after C1-C3 (D1-7/D1-8). Both cases
        // are refused unless the caller explicitly opted into `.bak` recovery.
        if bak_path.exists() {
            match self.load_file_with(&bak_path, false) {
                Ok((entries, max_tx_id, file_size)) => {
                    if self.options.auto_recover_from_backup && !self.options.read_only {
                        fs::copy(&bak_path, &self.path)?;
                        if let Some(cb) = &self.options.on_warning {
                            cb(
                                "onRecoveredFromBackup",
                                "Recovered database state from .bak",
                            );
                        }
                        self.apply_entries(entries)?;
                        self.current_max_tx_id = max_tx_id;
                        self.last_known_disk_state = (max_tx_id, file_size);
                        return Ok(());
                    }
                    return Err(DbError::Corrupted(format!(
                        "Primary file '{}' is missing while a previous generation is kept at \
						 '{}': copy it over the primary file to recover, or open with \
						 auto_recover_from_backup = true",
                        self.path.display(),
                        bak_path.display()
                    )));
                }
                Err(bak_err) => {
                    return Err(DbError::Corrupted(format!(
                        "Primary file '{}' is missing and the previous generation kept at '{}' \
						 could not be loaded either: {bak_err}",
                        self.path.display(),
                        bak_path.display()
                    )));
                }
            }
        }

        // Completely new database: do not write to disk until first write
        self.current_max_tx_id = 0;
        self.last_known_disk_state = (0, 0);
        Ok(())
    }

    /// Whether the *live* log may be healed by discarding a torn tail.
    fn allow_tail_truncation(&self) -> bool {
        self.options.engine_mode == StorageEngineMode::AppendOnlyLog && !self.options.read_only
    }

    fn load_file(&self, path: &Path) -> Result<(Vec<RawOpEntry>, u64, u64)> {
        self.load_file_with(path, self.allow_tail_truncation())
    }

    /// Loads a file under an explicit tail-truncation policy.
    ///
    /// Recovery sources (`.bak`) are always loaded with `allow_tail_truncation =
    /// false`: truncating a recovery source would destroy the data it is kept for (G1).
    fn load_file_with(
        &self,
        path: &Path,
        allow_tail_truncation: bool,
    ) -> Result<(Vec<RawOpEntry>, u64, u64)> {
        let mut file = if allow_tail_truncation {
            OpenOptions::new().read(true).write(true).open(path)?
        } else {
            File::open(path)?
        };

        let file_size = file.metadata()?.len();

        if file_size < FILE_HEADER_LEN as u64 {
            return Err(DbError::Corrupted(format!(
                "File size {} smaller than header length {}",
                file_size, FILE_HEADER_LEN
            )));
        }

        let mut header_bytes = [0u8; FILE_HEADER_LEN];
        file.read_exact(&mut header_bytes)?;
        FileHeader::verify_bytes(&header_bytes)?;

        if file_size == FILE_HEADER_LEN as u64 {
            // Legal empty database
            return Ok((Vec::new(), 0, file_size));
        }

        let (entries, max_tx_id, effective_size) = FrameLoader::load_with_recovery(
            &mut file,
            file_size,
            allow_tail_truncation,
            &self.options.on_warning,
        )?;
        Ok((entries, max_tx_id, effective_size))
    }

    /// Append operation entries to active database file in AppendOnlyLog mode.
    /// Backoff window, counted in writes, before a failed automatic compaction is
    /// retried.
    ///
    /// `prior_failures` is zero-based: the first failed compaction waits 64
    /// writes, and the window doubles with every further consecutive failure up to
    /// a cap of 4096 writes.
    fn compaction_backoff(prior_failures: u32) -> u64 {
        const BASE: u64 = 64;
        const CAP: u64 = 4096;
        BASE.saturating_mul(1u64 << prior_failures.min(6)).min(CAP)
    }

    /// Applies the automatic-compaction policy to a mutation that has just been
    /// applied: compact once enough *garbage* has accumulated, and retry a
    /// previously failed compaction once its backoff window has elapsed.
    fn maybe_compact_after_write(&mut self, new_file_size: u64) -> Result<()> {
        if self.options.compaction_threshold_bytes == 0 {
            return Ok(());
        }

        if self.needs_compaction {
            self.writes_since_compaction_attempt =
                self.writes_since_compaction_attempt.saturating_add(1);
            if self.writes_since_compaction_attempt
                < Self::compaction_backoff(self.compaction_failures.saturating_sub(1))
            {
                return Ok(());
            }
            return self.attempt_compaction();
        }

        // FIX-21: trigger on garbage, not on the absolute file size.
        //
        // A compaction leaves the file at exactly the live size, so the old
        // `new_file_size >= threshold` test was true on *every* write once live
        // data reached the threshold: at 6 MB live that measured 26 ms/write
        // (38 ops/s) instead of ~5,000. `garbage` is derived from the O(1) live
        // ledger, so this needs no extra state and survives a reopen (an
        // in-memory "bytes appended" counter would not).
        //
        // `effective_threshold` scales with the live size so the write
        // amplification of a compaction stays bounded (~5x) instead of growing
        // with the database.
        let live = self.live_payload_total().min(new_file_size);
        let garbage = new_file_size - live;
        let effective_threshold = self.options.compaction_threshold_bytes.max(live / 4);
        if garbage >= effective_threshold {
            return self.attempt_compaction();
        }

        // Adaptive trigger: a log that is mostly tombstones or overwritten values
        // never crosses the size threshold, so reclaim by dead ratio instead.
        if self.should_compact_by_dead_ratio(new_file_size) {
            return self.attempt_compaction();
        }

        Ok(())
    }

    /// Whether an under-threshold log is disproportionately garbage.
    ///
    /// `live_payload_bytes` only counts key+value bytes, so it is always smaller
    /// than the file (headers and frame overhead account for the difference);
    /// clamping keeps the ratio sane if the ledger were ever off.
    fn should_compact_by_dead_ratio(&self, file_size: u64) -> bool {
        if file_size < ADAPTIVE_MIN_FILE_SIZE {
            return false;
        }
        let live = self.live_payload_total().min(file_size);
        (file_size - live) * 100 >= file_size * ADAPTIVE_DEAD_RATIO_PERCENT
    }

    /// Attempts one automatic compaction and records the outcome.
    ///
    /// A failure never fails the caller's write unless `strict_compaction` is
    /// enabled: the mutation is already durable, so reporting an error would
    /// invite a retry of a write that succeeded, and `increment` (a
    /// read-modify-write) would then apply its delta twice (decision D2).
    fn attempt_compaction(&mut self) -> Result<()> {
        self.writes_since_compaction_attempt = 0;

        let outcome = match self.precheck_disk_state() {
            Ok(disk_max_tx_id) => self.commit_flush(disk_max_tx_id),
            Err(err) => Err(err),
        };

        match outcome {
            Ok(()) => {
                self.needs_compaction = false;
                self.compaction_failures = 0;
                Ok(())
            }
            Err(err) => {
                self.compaction_failures = self.compaction_failures.saturating_add(1);
                self.needs_compaction = true;

                // The warning fires in both modes: `strict_compaction` decides
                // whether the error additionally reaches the caller.
                self.queue_warning(
                    "onCompactionFailed",
                    format!(
                        "Compaction failed (size {} bytes, threshold {} bytes, \
                         consecutive failures {}): {err}",
                        self.last_known_disk_state.1,
                        self.options.compaction_threshold_bytes,
                        self.compaction_failures,
                    ),
                );

                if self.options.strict_compaction {
                    return Err(err);
                }

                Ok(())
            }
        }
    }

    /// Best-effort retry of a pending compaction, called from `close()` so a
    /// transient failure is not left unresolved for the lifetime of a
    /// write-once-then-close workload.
    ///
    /// The outcome is never propagated -- `close()` is a cleanup path, and a
    /// failed attempt is already reported through `onCompactionFailed`. A caller
    /// that needs the error can call `compact()` explicitly.
    fn retry_pending_compaction(&mut self) {
        if !self.needs_compaction
            || self.options.read_only
            || self.options.engine_mode != StorageEngineMode::AppendOnlyLog
        {
            return;
        }
        let _ = self.attempt_compaction();
    }

    /// Appends the mutation's frames to the log and returns the resulting file
    /// size. Compaction is deliberately **not** triggered here: the snapshot a
    /// compaction writes is taken from the current in-memory state, so it may
    /// only run once the mutation has been applied to memory (C1).
    fn append_entries(&mut self, entries: &[RawOpEntry]) -> Result<u64> {
        if entries.is_empty() {
            return Ok(0);
        }

        let disk_max_tx_id = self.precheck_disk_state()?;

        // RC2: open without `create` on the hot path. `create(true)` maps to
        // OPEN_ALWAYS, which performs a create-if-absent check on *every* open;
        // the file exists on all but the first write, so pay that only once.
        let mut file = match OpenOptions::new().read(true).write(true).open(&self.path) {
            Ok(f) => f,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => OpenOptions::new()
                .read(true)
                .write(true)
                .create(true)
                .truncate(false) // append path: an existing log must never be truncated
                .open(&self.path)?,
            Err(e) => return Err(e.into()),
        };

        // RC2: `seek(End)` already reports the size, so the separate
        // `metadata()` was a redundant syscall, and the `path.exists()` that
        // preceded the open was fully redundant: a file that was just created
        // has length 0, which this check already covers.
        let size_at_open = file.seek(SeekFrom::End(0))?;
        if size_at_open == 0 {
            file.write_all(&FileHeader::create_bytes())?;
        }

        let mut current_tx_id = self.current_max_tx_id.max(disk_max_tx_id) + 1;
        let mut current_frame_entries: Vec<RawOpEntry> = Vec::new();
        let mut current_payload_len = 0usize;

        // M2-03: a failed write must be rolled back to the length observed at
        // open. Otherwise a torn tail frame stays in the log forever and every
        // later `precheck_disk_state()` reports "disk changed externally"
        // against our own half-written file -- the connection cannot write
        // again until it is reopened and the tail is truncated.
        let appended: Result<()> = (|| {
            for entry in entries {
                let entry_size = entry.encoded_len();
                if current_frame_entries.len() >= MAX_OPS_PER_FRAME
                    || (current_payload_len + entry_size > MAX_PAYLOAD_LEN)
                {
                    BatchFrameWriter::write_frame(
                        &mut file,
                        current_tx_id,
                        &current_frame_entries,
                    )?;
                    current_tx_id += 1;
                    current_frame_entries.clear();
                    current_payload_len = 0;
                }
                current_payload_len += entry_size;
                current_frame_entries.push(entry.clone());
            }

            if !current_frame_entries.is_empty() {
                BatchFrameWriter::write_frame(&mut file, current_tx_id, &current_frame_entries)?;
            } else {
                current_tx_id -= 1;
            }
            Ok(())
        })();

        if let Err(e) = appended {
            let _ = file.set_len(size_at_open);
            let _ = file.sync_all();
            return Err(e);
        }

        if self.options.sync_mode == SyncMode::Immediate {
            file.sync_all()?;
            self.is_dirty = false;
        } else {
            file.flush()?;
            self.is_dirty = true;
        }

        // RC2: the current position after the appends *is* the new file size;
        // the previous `metadata()` was another redundant path lookup.
        let new_file_size = file.stream_position()?;
        self.current_max_tx_id = current_tx_id;
        self.last_known_disk_state = (current_tx_id, new_file_size);

        Ok(new_file_size)
    }

    /// Replays the op sequence recovered from a file into in-memory state.
    ///
    /// Unknown op codes (spec v3.2 defines only OP_PUT / OP_DELETE /
    /// OP_DROP_NAMESPACE on disk, and the file-header version check already
    /// rejects higher-version files) are treated as corruption and rejected,
    /// matching the Go and .NET engines.
    fn apply_entries(&mut self, entries: Vec<RawOpEntry>) -> Result<()> {
        for e in entries {
            match e.op_code {
                OP_PUT => {
                    self.mem_put(&e.namespace, e.key, e.value_type, e.value);
                }
                OP_DELETE => {
                    self.mem_delete(&e.namespace, &e.key);
                }
                OP_DROP_NAMESPACE => {
                    self.mem_drop(&e.namespace);
                }
                other => {
                    return Err(DbError::Corrupted(format!(
                        "Unknown op code 0x{:02X} in namespace {} (spec v3.2 defines only OP_PUT/OP_DELETE/OP_DROP_NAMESPACE)",
                        other,
                        String::from_utf8_lossy(&e.namespace)
                    )));
                }
            }
        }
        Ok(())
    }

    /// Phase 1 of the write path: every validation that can reject a write runs
    /// here, **before** any in-memory mutation, while the caller still holds the
    /// process-wide write lock. If this returns `Err`, nothing has been mutated,
    /// so the in-memory state can never diverge from the error the caller sees.
    ///
    /// Returns the max TxID currently on disk.
    ///
    /// The inter-process lock no longer appears here: since C9 it is taken once by
    /// `open` and held for the whole lifetime of the connection, so the write path
    /// cannot collide with itself any more (that was C2), and a failed pre-check
    /// cannot leave mutated-but-unpersisted state behind.
    fn precheck_disk_state(&self) -> Result<u64> {
        if self.options.read_only {
            return Err(DbError::Locked(
                "Database is opened in read-only mode".to_string(),
            ));
        }

        // Pre-check disk state.
        //
        // RC2: a single `metadata` replaces the previous `exists() + metadata()`
        // pair. Both calls answer the same question (does it exist, how big is
        // it), and on a filtered filesystem each path lookup costs tens of
        // microseconds; two lookups per write is pure waste.
        let (disk_max_tx_id, disk_file_size) = match fs::metadata(&self.path) {
            Ok(meta) => {
                let size = meta.len();
                if self.last_known_disk_state != (0, 0) && size == self.last_known_disk_state.1 {
                    (self.last_known_disk_state.0, size)
                } else if size >= FILE_HEADER_LEN as u64 {
                    let mut f = File::open(&self.path)?;
                    let max_tx = FrameLoader::scan_max_tx_id(&mut f, size)?;
                    (max_tx, size)
                } else {
                    (0, size)
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => (0, 0),
            Err(e) => return Err(e.into()),
        };

        // An empty database file (exactly 32B header, N=0 frames) cannot express a
        // TxID on disk: scan_max_tx_id returns 0 for it. Comparing the TxID in that
        // state would produce a false ConcurrentModificationError after any
        // clear/drop that empties the database and is followed by another write on
        // the same instance. For empty files only the file size is comparable.
        let state_matches = if disk_file_size == FILE_HEADER_LEN as u64 {
            disk_file_size == self.last_known_disk_state.1
        } else {
            (disk_max_tx_id, disk_file_size) == self.last_known_disk_state
        };

        if self.last_known_disk_state != (0, 0) && !state_matches {
            return Err(DbError::ConcurrentModification(format!(
                "Disk state changed externally: expected {:?}, found ({}, {})",
                self.last_known_disk_state, disk_max_tx_id, disk_file_size
            )));
        }

        Ok(disk_max_tx_id)
    }

    /// Serializes a snapshot into a `.tmp` file and fsyncs it.
    /// Can be executed outside of write locks to prevent blocking readers (C16).
    fn serialize_snapshot_to_tmp(tmp_path: &Path, data: &DataMap, start_tx_id: u64) -> Result<u64> {
        let mut tmp_file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(true)
            .open(tmp_path)?;

        tmp_file.write_all(&FileHeader::create_bytes())?;

        let mut sorted_entries: Vec<RawOpEntry> = Vec::new();
        for (ns_bytes, ns_map) in data.iter() {
            for (key_bytes, (vtype, val_bytes)) in ns_map.iter() {
                sorted_entries.push(RawOpEntry {
                    op_code: OP_PUT,
                    value_type: *vtype,
                    namespace: ns_bytes.clone(),
                    key: key_bytes.clone(),
                    value: val_bytes.clone(),
                });
            }
        }

        let mut final_max_tx_id = start_tx_id;
        if !sorted_entries.is_empty() {
            let mut current_tx_id = final_max_tx_id + 1;
            let mut current_frame_entries: Vec<RawOpEntry> = Vec::new();
            let mut current_payload_len = 0usize;

            for entry in sorted_entries {
                let entry_size = entry.encoded_len();
                if current_frame_entries.len() >= MAX_OPS_PER_FRAME
                    || (current_payload_len + entry_size > MAX_PAYLOAD_LEN)
                {
                    BatchFrameWriter::write_frame(
                        &mut tmp_file,
                        current_tx_id,
                        &current_frame_entries,
                    )?;
                    current_tx_id += 1;
                    current_frame_entries.clear();
                    current_payload_len = 0;
                }
                current_payload_len += entry_size;
                current_frame_entries.push(entry);
            }

            if !current_frame_entries.is_empty() {
                BatchFrameWriter::write_frame(
                    &mut tmp_file,
                    current_tx_id,
                    &current_frame_entries,
                )?;
                final_max_tx_id = current_tx_id;
            }
        }

        tmp_file.sync_all()?;
        Ok(final_max_tx_id)
    }

    /// Phase 2 of the write path: serialize the in-memory state into `.tmp`,
    /// fsync it and atomically rotate `.tmp` -> `.db`, preserving the previous
    /// generation as `.bak`. The inter-process lock is already held by the
    /// connection (C9) and stays held.
    fn commit_flush(&mut self, disk_max_tx_id: u64) -> Result<()> {
        let tmp_path = PathBuf::from(format!("{}.tmp", self.path.display()));
        let bak_path = PathBuf::from(format!("{}.bak", self.path.display()));

        let start_tx = self.current_max_tx_id.max(disk_max_tx_id);
        let final_max_tx_id = match Self::serialize_snapshot_to_tmp(&tmp_path, &self.data, start_tx)
        {
            Ok(tx) => tx,
            Err(e) => {
                self.is_dirty = true;
                let _ = fs::remove_file(&tmp_path);
                return Err(e);
            }
        };

        // 4. Atomic rotation
        // Step A: rotate existing db to .bak
        if self.path.exists() {
            if let Err(e) = rename_with_retry(&self.path, &bak_path) {
                self.is_dirty = true;
                let _ = fs::remove_file(&tmp_path);
                return Err(e);
            }
        }

        // Step B: rename .tmp to db
        if let Err(e) = rename_with_retry(&tmp_path, &self.path) {
            self.is_dirty = true;
            return Err(e);
        }

        // C18: fsync parent directory on POSIX (Windows skips)
        fsync_parent_dir(&self.path);

        // 5. Update in-memory state
        self.current_max_tx_id = final_max_tx_id;
        let new_file_size = fs::metadata(&self.path).map(|m| m.len()).unwrap_or(0);
        self.last_known_disk_state = (final_max_tx_id, new_file_size);
        self.is_dirty = false;

        Ok(())
    }
}

fn validate_kv_lengths(key_len: usize, val_len: usize) -> Result<()> {
    if key_len > MAX_KEY_LEN {
        return Err(DbError::KeyTooLarge(format!(
            "Key length {} exceeds limit {}",
            key_len, MAX_KEY_LEN
        )));
    }
    if val_len > MAX_VAL_LEN {
        return Err(DbError::KeyTooLarge(format!(
            "Value length {} exceeds limit {}",
            val_len, MAX_VAL_LEN
        )));
    }
    Ok(())
}

fn verify_file_integrity(path: &Path) -> Result<()> {
    let mut file = File::open(path)?;
    let size = file.metadata()?.len();
    if size < FILE_HEADER_LEN as u64 {
        return Err(DbError::Corrupted("File too small".to_string()));
    }
    let mut header = [0u8; FILE_HEADER_LEN];
    file.read_exact(&mut header)?;
    FileHeader::verify_bytes(&header)?;
    if size > FILE_HEADER_LEN as u64 {
        FrameLoader::load_all(&mut file, size)?;
    }
    Ok(())
}

/// Renames `src` over `dst`, replacing `dst` atomically where the platform allows
/// it.
///
/// The destination is **never** removed first (C5): `rename` already replaces it
/// on POSIX and on Windows (Rust uses `MOVEFILE_REPLACE_EXISTING`), so a preceding
/// `remove_file` only opens a window in which `.db` and `.bak` are *both* absent --
/// a crash inside that window leaves no generation at all. Retrying with backoff
/// covers the transient sharing violations (antivirus, indexer, a reader that has
/// not closed its handle yet).
fn rename_with_retry(src: &Path, dst: &Path) -> Result<()> {
    let backoffs = [5, 15, 30, 60, 120];
    let mut last_err: Option<std::io::Error> = None;

    for &ms in &backoffs {
        match fs::rename(src, dst) {
            Ok(_) => return Ok(()),
            Err(e) => {
                let raw = e.raw_os_error().unwrap_or(0);
                if raw == 5 || raw == 32 || raw == 33 {
                    last_err = Some(e);
                    thread::sleep(Duration::from_millis(ms));
                    continue;
                }
                return Err(DbError::Io(e));
            }
        }
    }

    // Last resort: `rename` cannot replace `dst` (e.g. the destination is held
    // exclusively by a scanner). Copying straight onto `dst` would truncate it
    // first and could leave a *partial* file behind, so the copy is staged next to
    // the destination and only then renamed over it. If even that fails, the
    // staged copy is kept so that both generations still exist on disk.
    let staged = PathBuf::from(format!("{}.stage", dst.display()));
    match fs::copy(src, &staged) {
        Ok(copied) => {
            let src_len = fs::metadata(src)?.len();
            if copied != src_len {
                return Err(DbError::Io(std::io::Error::new(
                    std::io::ErrorKind::UnexpectedEof,
                    format!(
                        "staged copy of '{}' is {} bytes instead of {}",
                        src.display(),
                        copied,
                        src_len
                    ),
                )));
            }
            match fs::rename(&staged, dst) {
                Ok(_) => {
                    let _ = fs::remove_file(src);
                    Ok(())
                }
                Err(e) => Err(DbError::Io(e)),
            }
        }
        Err(e) => Err(DbError::Io(match last_err {
            Some(prev) => std::io::Error::other(format!(
                "rename failed ({prev}); staging a copy also failed ({e})"
            )),
            None => e,
        })),
    }
}

/// On POSIX, fsync the parent directory after file rename to persist directory metadata.
/// On Windows, this is a no-op as metadata persists on close/rename.
#[cfg(unix)]
fn fsync_parent_dir(path: &Path) {
    if let Some(parent) = path.parent() {
        if let Ok(dir) = File::open(parent) {
            let _ = dir.sync_all();
        }
    }
}

#[cfg(not(unix))]
fn fsync_parent_dir(_path: &Path) {}

/// 绑定到特定命名空间的便利门面（Namespace Scope）。
#[derive(Clone, Copy)]
pub struct NamespaceScope<'a> {
    db: &'a Db,
    ns: &'a str,
}

impl<'a> NamespaceScope<'a> {
    pub fn namespace(&self) -> &'a str {
        self.ns
    }

    pub fn get_bytes<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<Vec<u8>>> {
        self.db.get_bytes(self.ns, key)
    }

    pub fn get_string<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<String>> {
        self.db.get_string(self.ns, key)
    }

    pub fn get_i64<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<i64>> {
        self.db.get_i64(self.ns, key)
    }

    pub fn get_f64<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<f64>> {
        self.db.get_f64(self.ns, key)
    }

    pub fn get_bool<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<bool>> {
        self.db.get_bool(self.ns, key)
    }

    pub fn contains_key<K: AsRef<[u8]>>(&self, key: K) -> Result<bool> {
        self.db.contains_key(self.ns, key)
    }

    pub fn put_bytes<K: AsRef<[u8]>, V: AsRef<[u8]>>(&self, key: K, val: V) -> Result<()> {
        self.db.put_bytes(self.ns, key, val)
    }

    pub fn put_string<K: AsRef<[u8]>, S: AsRef<str>>(&self, key: K, val: S) -> Result<()> {
        self.db.put_string(self.ns, key, val)
    }

    pub fn put_i64<K: AsRef<[u8]>>(&self, key: K, val: i64) -> Result<()> {
        self.db.put_i64(self.ns, key, val)
    }

    pub fn put_f64<K: AsRef<[u8]>>(&self, key: K, val: f64) -> Result<()> {
        self.db.put_f64(self.ns, key, val)
    }

    pub fn put_bool<K: AsRef<[u8]>>(&self, key: K, val: bool) -> Result<()> {
        self.db.put_bool(self.ns, key, val)
    }

    pub fn increment_i64<K: AsRef<[u8]>>(&self, key: K, delta: i64) -> Result<i64> {
        self.db.increment_i64(self.ns, key, delta)
    }

    pub fn delete<K: AsRef<[u8]>>(&self, key: K) -> Result<bool> {
        self.db.delete(self.ns, key)
    }

    pub fn count(&self) -> Result<usize> {
        self.db.count(self.ns)
    }

    pub fn keys(&self, prefix: Option<&str>) -> Result<Vec<String>> {
        self.db.keys(self.ns, prefix)
    }

    pub fn keys_all(&self) -> Result<Vec<String>> {
        self.keys(None)
    }

    pub fn iter_prefix(&self, prefix: Option<&str>) -> Result<RangeIter> {
        self.db.iter_prefix(self.ns, prefix)
    }

    pub fn scan_range(&self, start: Option<&str>, end: Option<&str>) -> Result<Vec<Entry>> {
        self.db.scan_range(self.ns, start, end)
    }

    pub fn clear(&self) -> Result<bool> {
        self.db.clear_namespace(self.ns)
    }

    pub fn drop(&self) -> Result<bool> {
        self.db.drop_namespace(self.ns)
    }
}

/// 绑定到快照上特定命名空间的便利门面。
#[derive(Clone, Copy)]
pub struct SnapshotNamespaceScope<'a> {
    snapshot: &'a Snapshot,
    ns: &'a str,
}

impl<'a> SnapshotNamespaceScope<'a> {
    pub fn namespace(&self) -> &'a str {
        self.ns
    }

    pub fn get_bytes<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<Vec<u8>>> {
        self.snapshot.get_bytes(self.ns, key)
    }

    pub fn get_string<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<String>> {
        self.snapshot.get_string(self.ns, key)
    }

    pub fn get_i64<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<i64>> {
        self.snapshot.get_i64(self.ns, key)
    }

    pub fn get_f64<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<f64>> {
        self.snapshot.get_f64(self.ns, key)
    }

    pub fn get_bool<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<bool>> {
        self.snapshot.get_bool(self.ns, key)
    }

    pub fn contains_key<K: AsRef<[u8]>>(&self, key: K) -> Result<bool> {
        self.snapshot.contains_key(self.ns, key)
    }

    pub fn count(&self) -> Result<usize> {
        self.snapshot.count(self.ns)
    }

    pub fn keys(&self, prefix: Option<&str>) -> Result<Vec<String>> {
        self.snapshot.keys(self.ns, prefix)
    }

    pub fn keys_all(&self) -> Result<Vec<String>> {
        self.keys(None)
    }

    pub fn iter_prefix(&self, prefix: Option<&str>) -> Result<RangeIter> {
        self.snapshot.iter_prefix(self.ns, prefix)
    }

    pub fn scan_range(&self, start: Option<&str>, end: Option<&str>) -> Result<Vec<Entry>> {
        self.snapshot.scan_range(self.ns, start, end)
    }
}

/// 绑定到批处理上特定命名空间的便利门面。
pub struct BatchNamespaceScope<'a> {
    batch: &'a mut Batch,
    ns: &'a str,
}

impl<'a> BatchNamespaceScope<'a> {
    pub fn namespace(&self) -> &'a str {
        self.ns
    }

    pub fn put_bytes<K: AsRef<[u8]>, V: AsRef<[u8]>>(
        &mut self,
        key: K,
        val: V,
    ) -> Result<&mut Self> {
        self.batch.put_bytes(self.ns, key, val)?;
        Ok(self)
    }

    pub fn put_string<K: AsRef<[u8]>, S: AsRef<str>>(
        &mut self,
        key: K,
        val: S,
    ) -> Result<&mut Self> {
        self.batch.put_string(self.ns, key, val)?;
        Ok(self)
    }

    pub fn put_i64<K: AsRef<[u8]>>(&mut self, key: K, val: i64) -> Result<&mut Self> {
        self.batch.put_i64(self.ns, key, val)?;
        Ok(self)
    }

    pub fn put_f64<K: AsRef<[u8]>>(&mut self, key: K, val: f64) -> Result<&mut Self> {
        self.batch.put_f64(self.ns, key, val)?;
        Ok(self)
    }

    pub fn put_bool<K: AsRef<[u8]>>(&mut self, key: K, val: bool) -> Result<&mut Self> {
        self.batch.put_bool(self.ns, key, val)?;
        Ok(self)
    }

    pub fn delete<K: AsRef<[u8]>>(&mut self, key: K) -> Result<&mut Self> {
        self.batch.delete(self.ns, key)?;
        Ok(self)
    }

    pub fn clear(&mut self) -> Result<&mut Self> {
        self.batch.clear_namespace(self.ns)?;
        Ok(self)
    }

    pub fn drop(&mut self) -> Result<&mut Self> {
        self.batch.drop_namespace(self.ns)?;
        Ok(self)
    }

    pub fn get_bytes<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<Vec<u8>>> {
        self.batch.get_bytes(self.ns, key.as_ref())
    }

    pub fn get_string<K: AsRef<[u8]>>(&self, key: K) -> Result<Option<String>> {
        self.batch.get_string(self.ns, key.as_ref())
    }
}

#[cfg(test)]
mod rotation_tests {
    use super::rename_with_retry;
    use std::fs;
    use tempfile::tempdir;

    /// C5: the destination must survive a rotation that does not complete.
    ///
    /// The source is deliberately absent, so `rename` fails with a permanent error
    /// and the fallback cannot help either. The destination holding the previous
    /// generation must still be there afterwards: a rotation that removes it first
    /// destroys the only remaining generation.
    #[test]
    fn rename_never_removes_the_destination() {
        let dir = tempdir().unwrap();
        let src = dir.path().join("absent.tmp");
        let dst = dir.path().join("gen.bak");
        let previous: Vec<u8> = (0u8..64).collect();
        fs::write(&dst, &previous).unwrap();

        let result = rename_with_retry(&src, &dst);

        assert!(result.is_err(), "renaming a missing source must fail");
        assert_eq!(
            fs::read(&dst).ok().as_deref(),
            Some(previous.as_slice()),
            "the destination (previous generation) must not be removed by a \
             failed rotation (C5)"
        );
        assert!(
            !dir.path().join("gen.bak.stage").exists(),
            "a failed rotation must not leave a staged copy behind here"
        );
    }

    /// The happy path still replaces the destination, and does so for a *large*
    /// destination (i.e. not by truncating it and copying in place).
    #[test]
    fn rename_replaces_an_existing_destination() {
        let dir = tempdir().unwrap();
        let src = dir.path().join("gen.tmp");
        let dst = dir.path().join("gen.bak");
        fs::write(&dst, vec![0xAAu8; 4096]).unwrap();
        let new_generation: Vec<u8> = (0u8..32).collect();
        fs::write(&src, &new_generation).unwrap();

        rename_with_retry(&src, &dst).unwrap();

        assert_eq!(fs::read(&dst).unwrap(), new_generation);
        assert!(!src.exists(), "the source must be consumed by the rename");
    }
}

#[cfg(test)]
mod aol_flush_guard_tests {
    use super::*;

    fn aol_deferred(dir: &std::path::Path, name: &str) -> Db {
        let opts = DbOptions::default()
            .engine_mode(StorageEngineMode::AppendOnlyLog)
            .sync_mode(SyncMode::Deferred);
        Db::open(dir.join(name), Some(opts)).unwrap()
    }

    /// N-X2: a frame appended while the background step is fsyncing bumps the
    /// generation; the step must then leave `is_dirty` set so `close()`/the next
    /// tick fsyncs it instead of treating it as already durable.
    #[test]
    fn dirty_flag_survives_a_generation_bump() {
        let dir = tempfile::tempdir().unwrap();
        let db = aol_deferred(dir.path(), "guard.db");
        db.put_string("", "k", "v").unwrap();

        let sampled = db.inner.read().dirty_generation;
        db.inner.write().dirty_generation = sampled + 1;

        assert!(
            !db.inner.read().aol_flush_should_clear_dirty(sampled),
            "a stale generation must never clear the dirty flag"
        );
        assert!(db.inner.read().is_dirty);
        db.close().unwrap();
    }

    #[test]
    fn dirty_flag_is_cleared_for_the_sampled_generation() {
        let dir = tempfile::tempdir().unwrap();
        let db = aol_deferred(dir.path(), "same_gen.db");
        db.put_string("", "k", "v").unwrap();

        let sampled = db.inner.read().dirty_generation;
        assert!(db.inner.read().aol_flush_should_clear_dirty(sampled));
        db.close().unwrap();
    }

    /// A closed instance must never clear the flag: `close()` has already run
    /// its final flush, so clearing it afterwards hides writes from every later
    /// flush opportunity.
    #[test]
    fn closed_instance_never_clears_dirty() {
        let dir = tempfile::tempdir().unwrap();
        let db = aol_deferred(dir.path(), "closed.db");
        db.put_string("", "k", "v").unwrap();
        db.close().unwrap();

        let mut inner = db.inner.write();
        inner.is_dirty = true;
        let sampled = inner.dirty_generation;
        assert!(!inner.aol_flush_should_clear_dirty(sampled));
    }
}
