use crate::types::{StorageEngineMode, SyncMode};
use std::sync::Arc;

/// Warning callback signature: receives warning type and human-readable message.
pub type WarningCallback = Arc<dyn Fn(&str, &str) + Send + Sync>;

/// Configuration options for opening a database.
///
/// Supports fluent builder pattern for ease of use.
///
/// # Examples
///
/// ```rust
/// use ckv::{DbOptions, SyncMode, StorageEngineMode};
///
/// let options = DbOptions::default()
///     .read_only(false)
///     .lock_timeout_ms(5000)
///     .sync_mode(SyncMode::Immediate)
///     .engine_mode(StorageEngineMode::SnapshotAtomic);
/// ```
#[derive(Clone)]
pub struct DbOptions {
    /// Open the database in read-only mode.
    /// In read-only mode, write locks are never acquired and write operations return an error.
    pub read_only: bool,

    /// Timeout in milliseconds before a lock is considered stale and eligible for stealing.
    /// Defaults to 10,000 ms (10 seconds).
    ///
    /// Type is `u32` to match the v3.2 spec (and Go / .NET). Note the dual meaning:
    /// it is both this process's wait budget *and* the staleness threshold for the
    /// lock file's age, so a write that takes longer than this can be stolen by
    /// another process.
    pub lock_timeout_ms: u32,

    /// Force clean any existing lock upon opening.
    /// Useful for single-instance desktop applications recovering from a previous crash.
    pub force_unlock: bool,

    /// Callback invoked when a non-fatal warning occurs (e.g. self-healing from .bak).
    pub on_warning: Option<WarningCallback>,

    /// Allow the engine to recover a corrupt primary file from `.bak` while opening.
    ///
    /// Defaults to `false`: a file that is identifiable as corrupt is reported as
    /// `Corrupted` instead of being silently replaced by a possibly older
    /// generation. `.bak` is an **offline copy for manual recovery**, not an
    /// automatic rollback mechanism (decision D1).
    pub auto_recover_from_backup: bool,

    /// Data synchronization persistence mode.
    /// Defaults to `SyncMode::Immediate`.
    pub sync_mode: SyncMode,

    /// Interval in milliseconds for background flush in `Deferred` sync mode.
    /// Defaults to 100 ms.
    pub sync_interval_ms: u32,

    /// Storage engine persistence mode.
    /// Defaults to `StorageEngineMode::SnapshotAtomic`.
    pub engine_mode: StorageEngineMode,

    /// File size threshold in bytes to trigger automatic compaction in `AppendOnlyLog` mode.
    /// Defaults to 4 MB.
    pub compaction_threshold_bytes: u64,

    /// Ratio of dead bytes (overwrites/deletions) to trigger automatic compaction.
    /// Defaults to 0.30 (30%).
    pub compaction_dead_ratio: f64,
}

impl Default for DbOptions {
    fn default() -> Self {
        Self {
            read_only: false,
            lock_timeout_ms: 10_000,
            force_unlock: false,
            on_warning: None,
            auto_recover_from_backup: false,
            sync_mode: SyncMode::Immediate,
            sync_interval_ms: 100,
            engine_mode: StorageEngineMode::SnapshotAtomic,
            compaction_threshold_bytes: 4 * 1024 * 1024,
            compaction_dead_ratio: 0.30,
        }
    }
}

impl DbOptions {
    /// Create default options.
    pub fn new() -> Self {
        Self::default()
    }

    /// Set read-only mode.
    pub fn read_only(mut self, read_only: bool) -> Self {
        self.read_only = read_only;
        self
    }

    /// Set lock timeout in milliseconds.
    pub fn lock_timeout_ms(mut self, timeout_ms: u32) -> Self {
        self.lock_timeout_ms = timeout_ms;
        self
    }

    /// Set force unlock flag.
    pub fn force_unlock(mut self, force: bool) -> Self {
        self.force_unlock = force;
        self
    }

    /// Allow automatic recovery of a corrupt primary file from `.bak`.
    ///
    /// Off by default (decision D1). Enabling it restores the legacy behaviour of
    /// silently replacing the primary file with the previous generation, which
    /// discards every write made after that generation was rotated.
    pub fn auto_recover_from_backup(mut self, enabled: bool) -> Self {
        self.auto_recover_from_backup = enabled;
        self
    }

    /// Set warning callback.
    pub fn on_warning<F>(mut self, cb: F) -> Self
    where
        F: Fn(&str, &str) + Send + Sync + 'static,
    {
        self.on_warning = Some(Arc::new(cb));
        self
    }

    /// Set data synchronization persistence mode.
    pub fn sync_mode(mut self, sync_mode: SyncMode) -> Self {
        self.sync_mode = sync_mode;
        self
    }

    /// Set background flush interval in milliseconds for `Deferred` sync mode.
    pub fn sync_interval_ms(mut self, interval_ms: u32) -> Self {
        self.sync_interval_ms = interval_ms;
        self
    }

    /// Set storage engine mode.
    pub fn engine_mode(mut self, engine_mode: StorageEngineMode) -> Self {
        self.engine_mode = engine_mode;
        self
    }

    /// Set compaction file size threshold in bytes.
    pub fn compaction_threshold_bytes(mut self, bytes: u64) -> Self {
        self.compaction_threshold_bytes = bytes;
        self
    }

    /// Set compaction dead data ratio.
    pub fn compaction_dead_ratio(mut self, ratio: f64) -> Self {
        self.compaction_dead_ratio = ratio;
        self
    }
}

impl std::fmt::Debug for DbOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DbOptions")
            .field("read_only", &self.read_only)
            .field("lock_timeout_ms", &self.lock_timeout_ms)
            .field("force_unlock", &self.force_unlock)
            .field("has_warning_callback", &self.on_warning.is_some())
            .field("auto_recover_from_backup", &self.auto_recover_from_backup)
            .field("sync_mode", &self.sync_mode)
            .field("sync_interval_ms", &self.sync_interval_ms)
            .field("engine_mode", &self.engine_mode)
            .field("compaction_threshold_bytes", &self.compaction_threshold_bytes)
            .field("compaction_dead_ratio", &self.compaction_dead_ratio)
            .finish()
    }
}

