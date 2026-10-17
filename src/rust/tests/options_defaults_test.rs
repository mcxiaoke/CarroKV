use ckv::{DbOptions, StorageEngineMode, SyncMode};

// Guards the cross-language default matrix documented in
// `KV-DB-DESIGN-SPEC.md` §5.1.1.1: every implementation must ship the same
// conservative, durability-first defaults. A change here is an ADR-level
// decision and must be applied to all five languages in one batch.
#[test]
fn test_default_options_matrix() {
    let o = DbOptions::default();

    assert!(!o.read_only);
    assert!(!o.auto_recover_from_backup, "D1: .bak lags one generation");
    assert_eq!(o.sync_mode, SyncMode::Immediate);
    assert_eq!(o.sync_interval_ms, 100);
    assert_eq!(o.engine_mode, StorageEngineMode::SnapshotAtomic);
    assert_eq!(o.compaction_threshold_bytes, 4 * 1024 * 1024);
    assert_eq!(o.lock_timeout_ms, 10_000);
    assert!(!o.strict_compaction, "D2: warning + backoff by default");
}
