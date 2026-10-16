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
/// use ckv::DbOptions;
///
/// let options = DbOptions::default()
///     .read_only(false)
///     .lock_timeout_ms(5000);
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
}

impl Default for DbOptions {
    fn default() -> Self {
        Self {
            read_only: false,
            lock_timeout_ms: 10_000,
            force_unlock: false,
            on_warning: None,
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

    /// Set warning callback.
    pub fn on_warning<F>(mut self, cb: F) -> Self
    where
        F: Fn(&str, &str) + Send + Sync + 'static,
    {
        self.on_warning = Some(Arc::new(cb));
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
            .finish()
    }
}
