enum SyncMode {
  immediate,
  deferred,
  manual,
}

enum StorageEngineMode {
  snapshotAtomic,
  appendOnlyLog,
}

typedef WarningCallback = void Function(String type, String message);

class DbOptions {
  bool readOnly;
  bool autoRecoverFromBackup;
  SyncMode syncMode;
  int syncIntervalMs;
  StorageEngineMode engineMode;
  int compactionThresholdBytes;
  int lockTimeoutMs;
  bool syncOnWrite;
  WarningCallback? onWarning;
  bool strictCompaction;

  DbOptions({
    this.readOnly = false,
    // Decision D1: `.bak` holds the generation *before* the last rotation, so
    // replacing a corrupt primary with it silently rolls back every write made
    // since. It is an offline copy for manual recovery, not an automatic
    // rollback mechanism.
    this.autoRecoverFromBackup = false,
    // Defaults follow C8 (4 MB compaction threshold) and C19 (10 s lock
    // timeout), matching the other four implementations. A zero threshold would
    // silently disable automatic compaction in `appendOnlyLog` mode, which is a
    // behavioural difference rather than a tuning difference.
    this.syncMode = SyncMode.immediate,
    this.syncIntervalMs = 100,
    this.engineMode = StorageEngineMode.snapshotAtomic,
    this.compactionThresholdBytes = 4 * 1024 * 1024,
    this.lockTimeoutMs = 10000,
    this.syncOnWrite = true,
    this.onWarning,
    // Decision D2: compaction failure is reported through `onWarning` and retried
    // later, because the mutation that triggered it is already durable. Set this
    // to true to propagate the error to the caller instead.
    this.strictCompaction = false,
  });

  DbOptions clone() {
    return DbOptions(
      readOnly: readOnly,
      autoRecoverFromBackup: autoRecoverFromBackup,
      syncMode: syncMode,
      syncIntervalMs: syncIntervalMs,
      engineMode: engineMode,
      compactionThresholdBytes: compactionThresholdBytes,
      lockTimeoutMs: lockTimeoutMs,
      syncOnWrite: syncOnWrite,
      onWarning: onWarning,
      strictCompaction: strictCompaction,
    );
  }
}
