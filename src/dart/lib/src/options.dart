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

  DbOptions({
    this.readOnly = false,
    // Decision D1: `.bak` holds the generation *before* the last rotation, so
    // replacing a corrupt primary with it silently rolls back every write made
    // since. It is an offline copy for manual recovery, not an automatic
    // rollback mechanism.
    this.autoRecoverFromBackup = false,
    this.syncMode = SyncMode.immediate,
    this.syncIntervalMs = 50,
    this.engineMode = StorageEngineMode.snapshotAtomic,
    this.compactionThresholdBytes = 0,
    this.lockTimeoutMs = 5000,
    this.syncOnWrite = true,
    this.onWarning,
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
    );
  }
}
