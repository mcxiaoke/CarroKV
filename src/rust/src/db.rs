use crate::errors::{DbError, Result};
use crate::format::{BatchFrameWriter, FileHeader, FrameLoader, RawOpEntry, FILE_HEADER_LEN};
use crate::lock::FileLock;
use crate::options::DbOptions;
use crate::types::*;
use parking_lot::{Mutex, RwLock};
use std::collections::{BTreeMap, HashMap};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
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

pub struct DbInner {
    canonical_path: PathBuf,
    path: PathBuf,
    options: DbOptions,
    // Outer key: namespace bytes; Inner key: key bytes -> (value_type, value_bytes) wrapped in Arc for zero-copy CoW snapshots
    data: BTreeMap<Vec<u8>, Arc<BTreeMap<Vec<u8>, (u8, Vec<u8>)>>>,
    current_max_tx_id: u64,
    last_known_disk_state: (u64, u64), // (max_tx_id, file_size)
    registered: bool,
    watchers: Vec<WatcherSlot>,
    next_watcher_id: u64,
    is_dirty: bool,
    bg_shutdown: Option<Arc<AtomicBool>>,
    bg_thread: Option<thread::JoinHandle<()>>,
}

impl DbInner {
    pub fn check_writable(&self) -> Result<()> {
        if self.options.read_only {
            return Err(DbError::Locked(
                "Database is opened in read-only mode".to_string(),
            ));
        }
        Ok(())
    }
}

impl Drop for DbInner {
    fn drop(&mut self) {
        if let Some(shutdown) = self.bg_shutdown.take() {
            shutdown.store(true, Ordering::Release);
        }
        if let Some(handle) = self.bg_thread.take() {
            let _ = handle.join();
        }
        if self.is_dirty && !self.options.read_only {
            if self.options.engine_mode == StorageEngineMode::AppendOnlyLog {
                if self.path.exists() {
                    if let Ok(f) = OpenOptions::new().write(true).open(&self.path) {
                        let _ = f.sync_all();
                    }
                }
                self.is_dirty = false;
            } else if let Ok((lock, disk_max_tx_id)) = self.begin_flush() {
                let _ = self.commit_flush(lock, disk_max_tx_id);
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
/// use ckv::Db;
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

    fn open_internal(path: PathBuf, canon_path: PathBuf, options: DbOptions) -> Result<Self> {
        // Step 1: Check existing instance under short lock
        {
            let mut guard = SHARED_REGISTRY.lock();
            let map = guard.get_or_insert_with(HashMap::new);
            if let Some(weak) = map.get(&canon_path) {
                if let Some(arc) = weak.upgrade() {
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
            current_max_tx_id: 0,
            last_known_disk_state: (0, 0),
            registered: false,
            watchers: Vec::new(),
            next_watcher_id: 1,
            is_dirty: false,
            bg_shutdown: None,
            bg_thread: None,
        };

        inner.init_and_recover()?;

        // Step 3: Register under short lock
        let mut guard = SHARED_REGISTRY.lock();
        let map = guard.get_or_insert_with(HashMap::new);

        // Double check if another thread opened it concurrently
        if let Some(weak) = map.get(&canon_path) {
            if let Some(arc) = weak.upgrade() {
                return Ok(Db { inner: arc });
            }
        }

        inner.registered = true;
        let arc = Arc::new(RwLock::new(inner));
        map.insert(canon_path, Arc::downgrade(&arc));

        if options.sync_mode == SyncMode::Deferred && !options.read_only {
            let weak = Arc::downgrade(&arc);
            let shutdown = Arc::new(AtomicBool::new(false));
            let shutdown_clone = shutdown.clone();
            let interval_ms = options.sync_interval_ms as u64;
            let handle = thread::Builder::new()
                .name("ckv-sync-worker".to_string())
                .spawn(move || {
                    while !shutdown_clone.load(Ordering::Acquire) {
                        thread::sleep(Duration::from_millis(interval_ms));
                        if shutdown_clone.load(Ordering::Acquire) {
                            break;
                        }
                        if let Some(inner_arc) = weak.upgrade() {
                            let mut inner = inner_arc.write();
                            if inner.is_dirty && !inner.options.read_only {
                                if inner.options.engine_mode == StorageEngineMode::AppendOnlyLog {
                                    if inner.path.exists() {
                                         if let Ok(f) = OpenOptions::new().write(true).open(&inner.path) {
                                             let _ = f.sync_all();
                                         }
                                    }
                                    inner.is_dirty = false;
                                } else if let Ok((lock, disk_max_tx_id)) = inner.begin_flush() {
                                    let _ = inner.commit_flush(lock, disk_max_tx_id);
                                }
                            }
                        } else {
                            break;
                        }
                    }
                })
                .ok();
            let mut guard = arc.write();
            guard.bg_shutdown = Some(shutdown);
            guard.bg_thread = handle;
        }

        Ok(Db { inner: arc })
    }

    /// Close the database instance.
    /// Flushes any pending dirty state if in Deferred/Manual sync mode and shuts down background workers.
    pub fn close(&self) -> Result<()> {
        let mut inner = self.inner.write();
        if let Some(shutdown) = inner.bg_shutdown.take() {
            shutdown.store(true, Ordering::Release);
        }
        let handle = inner.bg_thread.take();
        if let Some(h) = handle {
            drop(inner);
            let _ = h.join();
            let mut inner = self.inner.write();
            if inner.is_dirty && !inner.options.read_only {
                if inner.options.engine_mode == StorageEngineMode::AppendOnlyLog {
                    if inner.path.exists() {
                        let f = OpenOptions::new().write(true).open(&inner.path)?;
                        f.sync_all()?;
                    }
                    inner.is_dirty = false;
                } else {
                    let (lock, disk_max_tx_id) = inner.begin_flush()?;
                    inner.commit_flush(lock, disk_max_tx_id)?;
                }
            }
        } else if inner.is_dirty && !inner.options.read_only {
            if inner.options.engine_mode == StorageEngineMode::AppendOnlyLog {
                if inner.path.exists() {
                    let f = OpenOptions::new().write(true).open(&inner.path)?;
                    f.sync_all()?;
                }
                inner.is_dirty = false;
            } else {
                let (lock, disk_max_tx_id) = inner.begin_flush()?;
                inner.commit_flush(lock, disk_max_tx_id)?;
            }
        }
        Ok(())
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
        Ok(inner.data.get(norm_ns.as_bytes()).map(|m| m.len()).unwrap_or(0))
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

        let watchers = {
            let mut inner = self.inner.write();
            inner.apply_mutation(&[entry], |inner_ref| {
                let ns_arc = inner_ref.data.entry(norm_ns.as_bytes().to_vec()).or_default();
                let ns_map = Arc::make_mut(ns_arc);
                ns_map.insert(key_bytes.to_vec(), (val_type, val_bytes));
            })?;
            inner.watchers.clone()
        };

        if !watchers.is_empty() {
            let ev = KeyChangeEvent {
                op: KeyChangeOp::Put,
                namespace: norm_ns.to_string(),
                key: String::from_utf8_lossy(key_bytes).into_owned(),
                value_type: val_type,
            };
            for (_, w) in &watchers {
                w(&ev);
            }
        }
        Ok(())
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
        self.put_internal(ns, key.as_ref(), VAL_TYPE_STRING, val.as_ref().as_bytes().to_vec())
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
    /// Atomically increment a 64-bit signed integer by delta.
    /// If the key does not exist, it is initialized to delta.
    /// If existing value is not an int64, returns `DbError::TypeMismatch`.
    pub fn increment_i64<K: AsRef<[u8]>>(&self, ns: &str, key: K, delta: i64) -> Result<i64> {
        let norm_ns = normalize_namespace(ns)?;
        let key_bytes = key.as_ref();
        let bytes_8 = encode_i64(delta);
        validate_kv_lengths(key_bytes.len(), bytes_8.len())?;

        let (new_val, watchers) = {
            let mut inner = self.inner.write();
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

            let entry = RawOpEntry {
                op_code: OP_PUT,
                value_type: VAL_TYPE_INT64,
                namespace: norm_ns.as_bytes().to_vec(),
                key: key_bytes.to_vec(),
                value: encode_i64(new_v).to_vec(),
            };

            inner.apply_mutation(&[entry], |inner_ref| {
                let ns_arc = inner_ref.data.entry(norm_ns.as_bytes().to_vec()).or_default();
                let ns_map = Arc::make_mut(ns_arc);
                ns_map.insert(key_bytes.to_vec(), (VAL_TYPE_INT64, encode_i64(new_v).to_vec()));
            })?;

            (new_v, inner.watchers.clone())
        };

        if !watchers.is_empty() {
            let ev = KeyChangeEvent {
                op: KeyChangeOp::Put,
                namespace: norm_ns.to_string(),
                key: String::from_utf8_lossy(key_bytes).into_owned(),
                value_type: VAL_TYPE_INT64,
            };
            for (_, w) in &watchers {
                w(&ev);
            }
        }
        Ok(new_val)
    }

    /// Delete a key from the specified namespace.
    ///
    /// Returns `true` if the key existed and was actually deleted,
    /// `false` if the key did not exist (no-op).
    pub fn delete<K: AsRef<[u8]>>(&self, ns: &str, key: K) -> Result<bool> {
        let norm_ns = normalize_namespace(ns)?;
        let key_bytes = key.as_ref();
        let (watchers, existed) = {
            let mut inner = self.inner.write();
            inner.check_writable()?;
            let mut existed = false;
            let key_present = inner
                .data
                .get(norm_ns.as_bytes())
                .is_some_and(|ns_arc| ns_arc.contains_key(key_bytes));
            if key_present {
                let entry = RawOpEntry {
                    op_code: OP_DELETE,
                    value_type: VAL_TYPE_BYTES,
                    namespace: norm_ns.as_bytes().to_vec(),
                    key: key_bytes.to_vec(),
                    value: Vec::new(),
                };
                inner.apply_mutation(&[entry], |inner_ref| {
                    if let Some(ns_arc) = inner_ref.data.get_mut(norm_ns.as_bytes()) {
                        let ns_map = Arc::make_mut(ns_arc);
                        ns_map.remove(key_bytes);
                    }
                })?;
                existed = true;
            }
            (inner.watchers.clone(), existed)
        };

        if existed && !watchers.is_empty() {
            let ev = KeyChangeEvent {
                op: KeyChangeOp::Delete,
                namespace: norm_ns.to_string(),
                key: String::from_utf8_lossy(key_bytes).into_owned(),
                value_type: 0,
            };
            for (_, w) in &watchers {
                w(&ev);
            }
        }
        Ok(existed)
    }

    /// Atomically drop an entire namespace and all of its keys.
    ///
    /// Returns `true` if the namespace existed and was dropped.
    /// Notifies watchers with a single `DropNamespace` event (empty key).
    pub fn drop_namespace(&self, ns: &str) -> Result<bool> {
        let norm_ns = normalize_namespace(ns)?;
        let (watchers, existed) = {
            let mut inner = self.inner.write();
            inner.check_writable()?;
            let mut existed = false;
            if inner.data.contains_key(norm_ns.as_bytes()) {
                let entry = RawOpEntry {
                    op_code: OP_DROP_NAMESPACE,
                    value_type: VAL_TYPE_BYTES,
                    namespace: norm_ns.as_bytes().to_vec(),
                    key: Vec::new(),
                    value: Vec::new(),
                };
                inner.apply_mutation(&[entry], |inner_ref| {
                    inner_ref.data.remove(norm_ns.as_bytes());
                })?;
                existed = true;
            }
            (inner.watchers.clone(), existed)
        };

        if existed && !watchers.is_empty() {
            let ev = KeyChangeEvent {
                op: KeyChangeOp::DropNamespace,
                namespace: norm_ns.to_string(),
                key: String::new(),
                value_type: 0,
            };
            for (_, w) in &watchers {
                w(&ev);
            }
        }
        Ok(existed)
    }

    /// Clear all keys in a namespace while preserving the namespace itself.
    ///
    /// Returns `true` if the namespace existed and had at least one entry cleared.
    /// Notifies watchers with a single `Clear` event (empty key).
    pub fn clear_namespace(&self, ns: &str) -> Result<bool> {
        let norm_ns = normalize_namespace(ns)?;
        let (watchers, cleared) = {
            let mut inner = self.inner.write();
            inner.check_writable()?;
            let mut cleared = false;
            let keys_to_delete: Vec<Vec<u8>> = inner
                .data
                .get(norm_ns.as_bytes())
                .map(|ns_arc| ns_arc.keys().cloned().collect())
                .unwrap_or_default();

            if !keys_to_delete.is_empty() {
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

                inner.apply_mutation(&entries, |inner_ref| {
                    if let Some(ns_arc) = inner_ref.data.get_mut(norm_ns.as_bytes()) {
                        let ns_map = Arc::make_mut(ns_arc);
                        ns_map.clear();
                    }
                })?;
                cleared = true;
            }
            (inner.watchers.clone(), cleared)
        };

        if cleared && !watchers.is_empty() {
            let ev = KeyChangeEvent {
                op: KeyChangeOp::Clear,
                namespace: norm_ns.to_string(),
                key: String::new(),
                value_type: 0,
            };
            for (_, w) in &watchers {
                w(&ev);
            }
        }
        Ok(cleared)
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
                if frame.len() >= MAX_OPS_PER_FRAME
                    || (payload_len + entry_size > MAX_PAYLOAD_LEN)
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
            let (lock, disk_max_tx_id) = inner.begin_flush()?;
            inner.commit_flush(lock, disk_max_tx_id)
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
                let key_str = String::from_utf8_lossy(k).into_owned();
                result.push(Entry::new(norm_ns.to_string(), key_str, *vtype, val.clone()));
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
                    let key_str = String::from_utf8_lossy(k).into_owned();
                    entries.push(Entry::new(norm_ns.to_string(), key_str, *vtype, val.clone()));
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

        let (watchers, events) = {
            let mut inner = self.inner.write();
            inner.check_writable()?;

            // Pre-encode entries
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
            inner.apply_mutation(&raw_entries, |inner_ref| {
                for op in batch.ops {
                    match op {
                        BatchOp::Put {
                            namespace,
                            key,
                            val_type,
                            val,
                        } => {
                            events.push(KeyChangeEvent {
                                op: KeyChangeOp::Put,
                                namespace: String::from_utf8_lossy(&namespace).into_owned(),
                                key: String::from_utf8_lossy(&key).into_owned(),
                                value_type: val_type,
                            });
                            let ns_arc = inner_ref.data.entry(namespace).or_default();
                            let ns_map = Arc::make_mut(ns_arc);
                            ns_map.insert(key, (val_type, val));
                        }
                        BatchOp::Delete { namespace, key } => {
                            let removed = if let Some(ns_arc) = inner_ref.data.get_mut(&namespace) {
                                let ns_map = Arc::make_mut(ns_arc);
                                ns_map.remove(&key).is_some()
                            } else {
                                false
                            };
                            if removed {
                                events.push(KeyChangeEvent {
                                    op: KeyChangeOp::Delete,
                                    namespace: String::from_utf8_lossy(&namespace).into_owned(),
                                    key: String::from_utf8_lossy(&key).into_owned(),
                                    value_type: 0,
                                });
                            }
                        }
                        BatchOp::DropNamespace { namespace } => {
                            if inner_ref.data.remove(&namespace).is_some() {
                                events.push(KeyChangeEvent {
                                    op: KeyChangeOp::DropNamespace,
                                    namespace: String::from_utf8_lossy(&namespace).into_owned(),
                                    key: String::new(),
                                    value_type: 0,
                                });
                            }
                        }
                        BatchOp::ClearNamespace { namespace } => {
                            let non_empty = inner_ref
                                .data
                                .get(&namespace)
                                .is_some_and(|ns_arc| !ns_arc.is_empty());
                            if non_empty {
                                if let Some(ns_arc) = inner_ref.data.get_mut(&namespace) {
                                    let ns_map = Arc::make_mut(ns_arc);
                                    ns_map.clear();
                                }
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
            })?;

            (inner.watchers.clone(), events)
        };

        if !watchers.is_empty() {
            for ev in &events {
                for (_, w) in &watchers {
                    w(ev);
                }
            }
        }
        Ok(())
    }

    /// Execute operations inside a closure atomically with pure in-memory staging rollback.
    pub fn batch<F>(&self, f: F) -> Result<()>
    where
        F: FnOnce(&mut Batch) -> Result<()>,
    {
        let mut batch = Batch::new();
        f(&mut batch)?;
        self.write_batch(batch)
    }

    /// Rewrite the entire database into the most compact canonical snapshot file.
    pub fn compact(&self) -> Result<()> {
        let mut inner = self.inner.write();
        let (lock, disk_max_tx_id) = inner.begin_flush()?;
        inner.commit_flush(lock, disk_max_tx_id)
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
#[derive(Default, Clone, Debug)]
pub struct Batch {
    ops: Vec<BatchOp>,
}

impl Batch {
    pub fn new() -> Self {
        Self { ops: Vec::new() }
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
    data: BTreeMap<Vec<u8>, Arc<BTreeMap<Vec<u8>, (u8, Vec<u8>)>>>,
}

impl Snapshot {
    /// Return the logical transaction ID (TxID) of this snapshot.
    pub fn tx_id(&self) -> u64 {
        self.tx_id
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
                let key_str = String::from_utf8_lossy(k).into_owned();
                result.push(Entry::new(norm_ns.to_string(), key_str, *vtype, val.clone()));
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
}

impl DbInner {
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
								cb("onRecoveredFromBackup", "Recovered database state from .bak");
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

		// Primary does not exist, check .bak
		if bak_path.exists() {
			// `.bak` is a recovery source: load it as-is, never tail-truncate (G1).
			if let Ok((entries, max_tx_id, file_size)) = self.load_file_with(&bak_path, false) {
				fs::copy(&bak_path, &self.path)?;
				if let Some(cb) = &self.options.on_warning {
					cb("onRecoveredFromBackup", "Recovered database state from .bak");
				}
				self.apply_entries(entries)?;
				self.current_max_tx_id = max_tx_id;
				self.last_known_disk_state = (max_tx_id, file_size);
				return Ok(());
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
    /// Appends the mutation's frames to the log and returns the resulting file
    /// size. Compaction is deliberately **not** triggered here: the snapshot a
    /// compaction writes is taken from the current in-memory state, so it may
    /// only run once the mutation has been applied to memory (C1).
    fn append_entries(&mut self, entries: &[RawOpEntry]) -> Result<u64> {
        if entries.is_empty() {
            return Ok(0);
        }

        let (mut lock, disk_max_tx_id) = self.begin_flush()?;

        let file_exists = self.path.exists();
        let mut file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .open(&self.path)?;

        if !file_exists || file.metadata()?.len() == 0 {
            file.write_all(&FileHeader::create_bytes())?;
        }
        file.seek(SeekFrom::End(0))?;

        let mut current_tx_id = self.current_max_tx_id.max(disk_max_tx_id) + 1;
        let mut current_frame_entries: Vec<RawOpEntry> = Vec::new();
        let mut current_payload_len = 0usize;

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
            BatchFrameWriter::write_frame(
                &mut file,
                current_tx_id,
                &current_frame_entries,
            )?;
        } else {
            current_tx_id -= 1;
        }

        if self.options.sync_mode == SyncMode::Immediate {
            file.sync_all()?;
            self.is_dirty = false;
        } else {
            file.flush()?;
            self.is_dirty = true;
        }

        let new_file_size = file.metadata()?.len();
        self.current_max_tx_id = current_tx_id;
        self.last_known_disk_state = (current_tx_id, new_file_size);
        lock.release();

        Ok(new_file_size)
    }

    /// Execute an atomic mutation across in-memory state and the storage engine.
    fn apply_mutation<F>(
        &mut self,
        entries: &[RawOpEntry],
        apply_mem: F,
    ) -> Result<()>
    where
        F: FnOnce(&mut Self),
    {
        self.check_writable()?;
        if self.options.engine_mode == StorageEngineMode::AppendOnlyLog {
            // Order matters (C1/C2):
            //   1. append the frames (durability comes first),
            //   2. apply the mutation to the in-memory state,
            //   3. only now consider compaction, whose snapshot is serialised
            //      from the in-memory state and must therefore already contain
            //      this mutation.
            // `append_entries` releases the `.db` lock before returning, so the
            // compaction below acquires it in its own lock cycle instead of
            // colliding with the lock held by the caller (C2).
            let new_file_size = self.append_entries(entries)?;
            apply_mem(self);

            if self.options.compaction_threshold_bytes > 0
                && new_file_size >= self.options.compaction_threshold_bytes
            {
                let (comp_lock, disk_max_tx_id) = self.begin_flush()?;
                self.commit_flush(comp_lock, disk_max_tx_id)?;
            }
        } else if self.options.sync_mode == SyncMode::Immediate {
            let (lock, disk_max_tx_id) = self.begin_flush()?;
            apply_mem(self);
            self.commit_flush(lock, disk_max_tx_id)?;
        } else {
            apply_mem(self);
            self.current_max_tx_id += 1;
            self.is_dirty = true;
        }
        Ok(())
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
                    let ns_arc = self.data.entry(e.namespace).or_default();
                    let ns_map = Arc::make_mut(ns_arc);
                    ns_map.insert(e.key, (e.value_type, e.value));
                }
                OP_DELETE => {
                    if let Some(ns_arc) = self.data.get_mut(&e.namespace) {
                        let ns_map = Arc::make_mut(ns_arc);
                        ns_map.remove(&e.key);
                    }
                }
                OP_DROP_NAMESPACE => {
                    self.data.remove(&e.namespace);
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
    /// Returns the inter-process file lock (which must be handed to
    /// [`Self::commit_flush`]) together with the max TxID currently on disk.
    fn begin_flush(&mut self) -> Result<(FileLock, u64)> {
        if self.options.read_only {
            return Err(DbError::Locked(
                "Database is opened in read-only mode".to_string(),
            ));
        }

        // 1. Acquire the inter-process write lock first. Failing here after the
        //    mutation would leave mutated-but-unpersisted state behind.
        let lock = FileLock::acquire(&self.path, &self.options)?;

        // 2. Pre-check disk state
        let (disk_max_tx_id, disk_file_size) = if self.path.exists() {
            let size = fs::metadata(&self.path)?.len();
            if self.last_known_disk_state != (0, 0) && size == self.last_known_disk_state.1 {
                (self.last_known_disk_state.0, size)
            } else if size >= FILE_HEADER_LEN as u64 {
                let mut f = File::open(&self.path)?;
                let max_tx = FrameLoader::scan_max_tx_id(&mut f, size)?;
                (max_tx, size)
            } else {
                (0, size)
            }
        } else {
            (0, 0)
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

        Ok((lock, disk_max_tx_id))
    }

    /// Phase 2 of the write path: serialize the in-memory state into `.tmp`,
    /// fsync it and atomically rotate `.tmp` -> `.db`, preserving the previous
    /// generation as `.bak`. Consumes the lock from [`Self::begin_flush`]; the
    /// lock is released when the value is dropped (including on error paths).
    fn commit_flush(&mut self, mut lock: FileLock, disk_max_tx_id: u64) -> Result<()> {
        // 3. Serialize snapshot into .tmp
        let tmp_path = PathBuf::from(format!("{}.tmp", self.path.display()));
        let bak_path = PathBuf::from(format!("{}.bak", self.path.display()));

        let mut tmp_file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(true)
            .open(&tmp_path)?;

        // Write 32B file header
        tmp_file.write_all(&FileHeader::create_bytes())?;

        // Collect all alive entries in canonical order:
        // NamespaceBytes asc, then KeyBytes asc
        let mut sorted_entries: Vec<RawOpEntry> = Vec::new();
        for (ns_bytes, ns_map) in self.data.iter() {
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

        let mut final_max_tx_id = self.current_max_tx_id.max(disk_max_tx_id);

        if !sorted_entries.is_empty() {
            let mut current_tx_id = final_max_tx_id + 1;
            let mut current_frame_entries: Vec<RawOpEntry> = Vec::new();
            let mut current_payload_len = 0usize;

            for entry in sorted_entries {
                let entry_size = entry.encoded_len();
                if current_frame_entries.len() >= MAX_OPS_PER_FRAME
                    || (current_payload_len + entry_size > MAX_PAYLOAD_LEN)
                {
                    // Flush current frame
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
        drop(tmp_file); // Close handle before rename

        // 4. Atomic rotation
        // Step A: rotate existing db to .bak
        if self.path.exists() {
            rename_with_retry(&self.path, &bak_path)?;
        }

        // Step B: rename .tmp to db
        rename_with_retry(&tmp_path, &self.path)?;

        // 5. Update in-memory state
        self.current_max_tx_id = final_max_tx_id;
        let new_file_size = fs::metadata(&self.path)?.len();
        self.last_known_disk_state = (final_max_tx_id, new_file_size);
        self.is_dirty = false;

        lock.release();
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

fn rename_with_retry(src: &Path, dst: &Path) -> Result<()> {
    let backoffs = [5, 15, 30, 60, 120];
    for &ms in &backoffs {
        if dst.exists() {
            let _ = fs::remove_file(dst);
        }
        match fs::rename(src, dst) {
            Ok(_) => return Ok(()),
            Err(e) => {
                let raw = e.raw_os_error().unwrap_or(0);
                if raw == 5 || raw == 32 || raw == 33 {
                    thread::sleep(Duration::from_millis(ms));
                    continue;
                }
                return Err(DbError::Io(e));
            }
        }
    }
    // Fallback: copy and remove src
    if dst.exists() {
        let _ = fs::remove_file(dst);
    }
    match fs::rename(src, dst) {
        Ok(_) => Ok(()),
        Err(_) => {
            fs::copy(src, dst).map_err(DbError::Io)?;
            let _ = fs::remove_file(src);
            Ok(())
        }
    }
}
