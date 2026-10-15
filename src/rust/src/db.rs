use crate::errors::{DbError, Result};
use crate::format::{BatchFrameWriter, FileHeader, FrameLoader, RawOpEntry, FILE_HEADER_LEN};
use crate::lock::FileLock;
use crate::options::DbOptions;
use crate::types::*;
use std::collections::BTreeMap;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, RwLock};
use std::thread;
use std::time::Duration;

pub struct DbInner {
    path: PathBuf,
    options: DbOptions,
    // Outer key: namespace bytes; Inner key: key bytes -> (value_type, value_bytes)
    data: BTreeMap<Vec<u8>, BTreeMap<Vec<u8>, (u8, Vec<u8>)>>,
    current_max_tx_id: u64,
    last_known_disk_state: (u64, u64), // (max_tx_id, file_size)
}

#[derive(Clone)]
pub struct Db {
    inner: Arc<RwLock<DbInner>>,
}

impl Db {
    pub fn open<P: AsRef<Path>>(path: P, options: Option<DbOptions>) -> Result<Self> {
        let path = path.as_ref().to_path_buf();
        let options = options.unwrap_or_default();

        let mut inner = DbInner {
            path: path.clone(),
            options: options.clone(),
            data: BTreeMap::new(),
            current_max_tx_id: 0,
            last_known_disk_state: (0, 0),
        };

        inner.init_and_recover()?;

        Ok(Db {
            inner: Arc::new(RwLock::new(inner)),
        })
    }

    pub fn close(&self) -> Result<()> {
        // Resources are automatically released on drop
        Ok(())
    }

    pub fn get_bytes(&self, ns: &str, key: &str) -> Result<Option<Vec<u8>>> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read().map_err(|e| DbError::Corrupted(e.to_string()))?;
        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            if let Some((_, val)) = ns_map.get(key.as_bytes()) {
                return Ok(Some(val.clone()));
            }
        }
        Ok(None)
    }

    pub fn get_string(&self, ns: &str, key: &str) -> Result<Option<String>> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read().map_err(|e| DbError::Corrupted(e.to_string()))?;
        if let Some(ns_map) = inner.data.get(norm_ns.as_bytes()) {
            if let Some((val_type, val)) = ns_map.get(key.as_bytes()) {
                if *val_type == VAL_TYPE_STRING {
                    return Ok(Some(String::from_utf8(val.clone()).map_err(|e| {
                        DbError::InvalidUtf8(format!("Invalid UTF-8 in key '{}': {}", key, e))
                    })?));
                } else {
                    // Raw bytes, must strictly validate UTF-8
                    return Ok(Some(String::from_utf8(val.clone()).map_err(|e| {
                        DbError::InvalidUtf8(format!("Invalid UTF-8 in key '{}': {}", key, e))
                    })?));
                }
            }
        }
        Ok(None)
    }

    pub fn put_bytes(&self, ns: &str, key: &str, val: &[u8]) -> Result<()> {
        let norm_ns = normalize_namespace(ns)?;
        validate_kv_lengths(key, val)?;
        let mut inner = self.inner.write().map_err(|e| DbError::Corrupted(e.to_string()))?;
        inner.data
            .entry(norm_ns.as_bytes().to_vec())
            .or_default()
            .insert(key.as_bytes().to_vec(), (VAL_TYPE_BYTES, val.to_vec()));
        inner.flush_snapshot()
    }

    pub fn put_string(&self, ns: &str, key: &str, val: &str) -> Result<()> {
        let norm_ns = normalize_namespace(ns)?;
        validate_kv_lengths(key, val.as_bytes())?;
        let mut inner = self.inner.write().map_err(|e| DbError::Corrupted(e.to_string()))?;
        inner.data
            .entry(norm_ns.as_bytes().to_vec())
            .or_default()
            .insert(key.as_bytes().to_vec(), (VAL_TYPE_STRING, val.as_bytes().to_vec()));
        inner.flush_snapshot()
    }

    pub fn delete(&self, ns: &str, key: &str) -> Result<()> {
        let norm_ns = normalize_namespace(ns)?;
        let mut inner = self.inner.write().map_err(|e| DbError::Corrupted(e.to_string()))?;
        if let Some(ns_map) = inner.data.get_mut(norm_ns.as_bytes()) {
            ns_map.remove(key.as_bytes());
        }
        inner.flush_snapshot()
    }

    pub fn drop_namespace(&self, ns: &str) -> Result<()> {
        let norm_ns = normalize_namespace(ns)?;
        let mut inner = self.inner.write().map_err(|e| DbError::Corrupted(e.to_string()))?;
        inner.data.remove(norm_ns.as_bytes());
        inner.flush_snapshot()
    }

    pub fn keys(&self, ns: &str, prefix: Option<&str>) -> Result<Vec<String>> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read().map_err(|e| DbError::Corrupted(e.to_string()))?;
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
        // Result is naturally sorted by unsigned byte lexicographical order
        Ok(result)
    }

    pub fn scan_range(
        &self,
        ns: &str,
        start_key: Option<&str>,
        end_key: Option<&str>,
    ) -> Result<Vec<Entry>> {
        let norm_ns = normalize_namespace(ns)?;
        let inner = self.inner.read().map_err(|e| DbError::Corrupted(e.to_string()))?;
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
                let key_str = String::from_utf8(k.clone())
                    .map_err(|e| DbError::InvalidUtf8(e.to_string()))?;
                result.push(Entry {
                    namespace: norm_ns.to_string(),
                    key: key_str,
                    value_type: *vtype,
                    raw_bytes: val.clone(),
                });
            }
        }
        Ok(result)
    }

    pub fn batch<F>(&self, f: F) -> Result<()>
    where
        F: FnOnce(&mut BatchWriter) -> Result<()>,
    {
        let mut writer = BatchWriter::new();
        f(&mut writer)?;

        if writer.ops.is_empty() {
            return Ok(());
        }

        let mut inner = self.inner.write().map_err(|e| DbError::Corrupted(e.to_string()))?;
        for op in writer.ops {
            match op {
                BatchOp::Put {
                    namespace,
                    key,
                    val_type,
                    val,
                } => {
                    inner
                        .data
                        .entry(namespace)
                        .or_default()
                        .insert(key, (val_type, val));
                }
                BatchOp::Delete { namespace, key } => {
                    if let Some(ns_map) = inner.data.get_mut(&namespace) {
                        ns_map.remove(&key);
                    }
                }
                BatchOp::DropNamespace { namespace } => {
                    inner.data.remove(&namespace);
                }
            }
        }
        inner.flush_snapshot()
    }

    pub fn compact(&self) -> Result<()> {
        let mut inner = self.inner.write().map_err(|e| DbError::Corrupted(e.to_string()))?;
        inner.flush_snapshot()
    }
}

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
}

pub struct BatchWriter {
    ops: Vec<BatchOp>,
}

impl BatchWriter {
    pub fn new() -> Self {
        Self { ops: Vec::new() }
    }

    pub fn put_bytes(&mut self, ns: &str, key: &str, val: &[u8]) -> Result<()> {
        let norm_ns = normalize_namespace(ns)?;
        validate_kv_lengths(key, val)?;
        self.ops.push(BatchOp::Put {
            namespace: norm_ns.as_bytes().to_vec(),
            key: key.as_bytes().to_vec(),
            val_type: VAL_TYPE_BYTES,
            val: val.to_vec(),
        });
        Ok(())
    }

    pub fn put_string(&mut self, ns: &str, key: &str, val: &str) -> Result<()> {
        let norm_ns = normalize_namespace(ns)?;
        validate_kv_lengths(key, val.as_bytes())?;
        self.ops.push(BatchOp::Put {
            namespace: norm_ns.as_bytes().to_vec(),
            key: key.as_bytes().to_vec(),
            val_type: VAL_TYPE_STRING,
            val: val.as_bytes().to_vec(),
        });
        Ok(())
    }

    pub fn delete(&mut self, ns: &str, key: &str) -> Result<()> {
        let norm_ns = normalize_namespace(ns)?;
        self.ops.push(BatchOp::Delete {
            namespace: norm_ns.as_bytes().to_vec(),
            key: key.as_bytes().to_vec(),
        });
        Ok(())
    }

    pub fn drop_namespace(&mut self, ns: &str) -> Result<()> {
        let norm_ns = normalize_namespace(ns)?;
        self.ops.push(BatchOp::DropNamespace {
            namespace: norm_ns.as_bytes().to_vec(),
        });
        Ok(())
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
                    self.apply_entries(entries);
                    self.current_max_tx_id = max_tx_id;
                    self.last_known_disk_state = (max_tx_id, file_size);
                    return Ok(());
                }
                Err(err) => {
                    // Corrupted primary file, try fallback to .bak
                    if bak_path.exists() {
                        if let Ok((entries, max_tx_id, file_size)) = self.load_file(&bak_path) {
                            // Recover by copying .bak to primary file
                            fs::copy(&bak_path, &self.path)?;
                            if let Some(cb) = &self.options.on_warning {
                                cb("onRecoveredFromBackup", "Recovered database state from .bak");
                            }
                            self.apply_entries(entries);
                            self.current_max_tx_id = max_tx_id;
                            self.last_known_disk_state = (max_tx_id, file_size);
                            return Ok(());
                        }
                    }
                    return Err(err);
                }
            }
        }

        // Primary does not exist, check .bak
        if bak_path.exists() {
            if let Ok((entries, max_tx_id, file_size)) = self.load_file(&bak_path) {
                fs::copy(&bak_path, &self.path)?;
                if let Some(cb) = &self.options.on_warning {
                    cb("onRecoveredFromBackup", "Recovered database state from .bak");
                }
                self.apply_entries(entries);
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

    fn load_file(&self, path: &Path) -> Result<(Vec<RawOpEntry>, u64, u64)> {
        let mut file = File::open(path)?;
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

        let (entries, max_tx_id) = FrameLoader::load_all(&mut file, file_size)?;
        Ok((entries, max_tx_id, file_size))
    }

    fn apply_entries(&mut self, entries: Vec<RawOpEntry>) {
        for e in entries {
            match e.op_code {
                OP_PUT => {
                    self.data
                        .entry(e.namespace)
                        .or_default()
                        .insert(e.key, (e.value_type, e.value));
                }
                OP_DELETE => {
                    if let Some(ns_map) = self.data.get_mut(&e.namespace) {
                        ns_map.remove(&e.key);
                    }
                }
                OP_DROP_NAMESPACE => {
                    self.data.remove(&e.namespace);
                }
                _ => {}
            }
        }
    }

    fn flush_snapshot(&mut self) -> Result<()> {
        if self.options.read_only {
            return Err(DbError::Locked("Database is opened in read-only mode".to_string()));
        }

        // 1. Acquire write lock
        let mut lock = FileLock::acquire(&self.path, &self.options)?;

        // 2. Pre-check disk state
        let (disk_max_tx_id, disk_file_size) = if self.path.exists() {
            let mut f = File::open(&self.path)?;
            let size = f.metadata()?.len();
            let max_tx = if size >= FILE_HEADER_LEN as u64 {
                FrameLoader::scan_max_tx_id(&mut f, size)?
            } else {
                0
            };
            (max_tx, size)
        } else {
            (0, 0)
        };

        if self.last_known_disk_state != (0, 0)
            && (disk_max_tx_id, disk_file_size) != self.last_known_disk_state
        {
            return Err(DbError::ConcurrentModification(format!(
                "Disk state changed externally: expected {:?}, found ({}, {})",
                self.last_known_disk_state, disk_max_tx_id, disk_file_size
            )));
        }

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

        lock.release();
        Ok(())
    }
}

fn validate_kv_lengths(key: &str, val: &[u8]) -> Result<()> {
    if key.len() > MAX_KEY_LEN {
        return Err(DbError::KeyTooLarge(format!(
            "Key length {} exceeds limit {}",
            key.len(),
            MAX_KEY_LEN
        )));
    }
    if val.len() > MAX_VAL_LEN {
        return Err(DbError::KeyTooLarge(format!(
            "Value length {} exceeds limit {}",
            val.len(),
            MAX_VAL_LEN
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
    // Final attempt
    fs::rename(src, dst).map_err(DbError::Io)
}
