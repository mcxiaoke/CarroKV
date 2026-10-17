enum SyncMode {
  immediate,
  deferred,
  manual,
}

/// 底层存储引擎模式。
///
/// ⚠️ [snapshotAtomic]（默认）的写成本是 O(库大小)：每次写入都全量序列化整库并
/// fsync。500 条目 2.29ms/写、5,000 条目 4.43ms/写，外推到 5 万条目约 40ms/写；
/// 而 [appendOnlyLog] 稳态约 0.18ms/写且与规模无关。
/// 数据量超过约 2MB 或存在高频写入时，请选 [appendOnlyLog]。
/// 详见 `DbOptions.engineMode`。
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
    // ⚠️ [StorageEngineMode.snapshotAtomic] 的写成本是 O(库大小)。
    //
    // 它每次写入都会**全量序列化整库并 fsync**，代价随库大小线性增长。这是默认配置，
    // 但没有任何运行时护栏或告警阻止你把它用在远大于建议规模的库上——到那时吞吐会比
    // [StorageEngineMode.appendOnlyLog] 差两个数量级。
    //
    // 实测（本机 Windows 10，稳态写延迟中位数）：
    //
    // | 存活条目 | snapshotAtomic + immediate | appendOnlyLog |
    // | ---: | ---: | ---: |
    // | 500 | 2.29 ms (~437 ops/s) | 0.18 ms (~5,500 ops/s)，与规模无关 |
    // | 2,000 | 2.93 ms (~341 ops/s) | 同上 |
    // | 5,000 | 4.43 ms (~226 ops/s) | 同上 |
    // | ~50,000（外推，约 5.7 MB） | ~40 ms (~25 ops/s) | 同上 |
    //
    // 选型建议：数据量超过约 2 MB，或存在高频写入时，请改用
    // [StorageEngineMode.appendOnlyLog]。
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
