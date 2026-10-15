use std::sync::Arc;

pub type WarningCallback = Arc<dyn Fn(&str, &str) + Send + Sync>;

#[derive(Clone)]
pub struct DbOptions {
    pub read_only: bool,
    pub lock_timeout_ms: u64,
    pub force_unlock: bool,
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
