import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:path/path.dart' as p;

import 'binary_helper.dart';
import 'constants.dart';
import 'crc32.dart';
import 'errors.dart';
import 'format.dart';
import 'lock.dart';
import 'options.dart';
import 'storage.dart';
import 'types.dart';

class _WatcherSlot implements WatcherHandle {
  final int id;
  final KeyChangeCallback callback;
  final CkvDb _owner;
  bool _disposed = false;

  _WatcherSlot(this.id, this.callback, this._owner);

  @override
  void unwatch() => dispose();

  @override
  void dispose() {
    if (_disposed) return;
    _disposed = true;
    _owner._removeWatcher(id);
  }
}

class CkvDb {
  static final Map<String, CkvDb> _registry = {};

  final String _path;
  final String _canonicalPath;
  final DbOptions _options;

  final Map<String, NamespaceStore> _namespaces = {};
  int _currentMaxTxId = 0;
  int _lastKnownDiskMaxTxId = 0;
  int _lastKnownDiskFileSize = 0;
  bool _isDirty = false;

  /// 跨进程连接锁，自 C9 起由 open 获取、持有整个连接生命周期（内核锁）。
  LockFile? _connectionLock;
  bool _isDisposed = false;
  int _snapshotCount = 0;

  // Automatic-compaction failure state (decision D2): `_needsCompaction` is set
  // when an attempt failed and a retry is pending; the mutation that triggered it
  // is already durable.
  bool _needsCompaction = false;
  int _compactionFailures = 0;
  int _writesSinceCompactionAttempt = 0;

  Timer? _syncTimer;
  int _nextWatcherId = 0;
  final List<_WatcherSlot> _watchers = [];

  CkvDb._(this._path, this._canonicalPath, this._options) {
    if (!_options.readOnly && _options.syncMode == SyncMode.deferred) {
      final interval = Duration(
        milliseconds: _options.syncIntervalMs > 0 ? _options.syncIntervalMs : 100,
      );
      _syncTimer = Timer.periodic(interval, (_) => _flushIfDirty());
    }
  }

  static void _checkOptionsConflict(DbOptions existingOpts, DbOptions newOpts) {
    final conflicts = <String>[];
    if (existingOpts.readOnly != newOpts.readOnly) {
      conflicts.add('readOnly (existing=${existingOpts.readOnly}, requested=${newOpts.readOnly})');
    }
    if (existingOpts.engineMode != newOpts.engineMode) {
      conflicts.add('engineMode (existing=${existingOpts.engineMode}, requested=${newOpts.engineMode})');
    }
    if (existingOpts.syncMode != newOpts.syncMode) {
      conflicts.add('syncMode (existing=${existingOpts.syncMode}, requested=${newOpts.syncMode})');
    }
    if (conflicts.isNotEmpty) {
      final msg = 'Options conflict on reentrant open: ${conflicts.join(", ")}. Using existing singleton options.';
      if (newOpts.onWarning != null) {
        newOpts.onWarning!('onOptionsConflict', msg);
      } else if (existingOpts.onWarning != null) {
        existingOpts.onWarning!('onOptionsConflict', msg);
      }
    }
  }

  static CkvDb open(String path, [DbOptions? options]) {
    final opts = options?.clone() ?? DbOptions();
    final canonical = p.canonicalize(path);

    final existing = _registry[canonical];
    if (existing != null && !existing._isDisposed) {
      _checkOptionsConflict(existing._options, opts);
      return existing;
    }

    final db = CkvDb._(path, canonical, opts);
    // C9: 跨进程内核锁在任何磁盘动作之前获取，并持有整个连接生命周期。因此
    // _initAndRecover 的所有修复动作（.tmp 晋升/删除、残尾自愈、.bak 回退）都在锁内执行，
    // 不会再截掉另一个进程刚追加的帧（C10）。
    db._connectionLock = LockFile.acquire(path, opts);
    try {
      db._initAndRecover();
    } catch (_) {
      db._connectionLock!.release();
      db._connectionLock = null;
      rethrow;
    }
    _registry[canonical] = db;
    return db;
  }

  /// Opens the database.
  ///
  /// The caller ([open]) has already acquired the connection lock and holds it for
  /// the whole lifetime of the connection, so every repair performed here --
  /// promoting or deleting `.tmp`, healing a torn log tail, rolling a `.bak`
  /// forward -- writes to the database files while no other process can append to
  /// them. That is what used to be C10: the tail healing ran without the lock and
  /// could truncate frames a concurrent appender had just written.
  void _initAndRecover() {

    final dbPath = _path;
    final tmpPath = '$_path.tmp';
    final bakPath = '$_path.bak';

    bool dbExists = File(dbPath).existsSync();

    if (File(tmpPath).existsSync()) {
      if (!dbExists) {
        try {
          final tmpBytes = File(tmpPath).readAsBytesSync();
          final parsed = FrameLoader.loadAllWithRecovery(tmpBytes);
          File(tmpPath).renameSync(dbPath);
          _applyParsed(parsed.namespaces, parsed.maxTxId, File(dbPath).lengthSync());
          dbExists = true;
        } catch (_) {
          try {
            File(tmpPath).deleteSync();
          } catch (_) {}
        }
      } else {
        try {
          File(tmpPath).deleteSync();
        } catch (_) {}
      }
    }

    if (dbExists) {
      final bytes = File(dbPath).readAsBytesSync();
      final allowTailTrunc =
          _options.engineMode == StorageEngineMode.appendOnlyLog && !_options.readOnly;
      try {
        final parsed = FrameLoader.loadAllWithRecovery(
          bytes,
          allowTailTruncation: allowTailTrunc,
          onWarning: _options.onWarning,
        );
        if (allowTailTrunc && parsed.effectiveLength < bytes.length) {
          final raf = File(dbPath).openSync(mode: FileMode.writeOnlyAppend);
          try {
            raf.truncateSync(parsed.effectiveLength);
            raf.flushSync();
          } finally {
            raf.closeSync();
          }
        }
        _applyParsed(parsed.namespaces, parsed.maxTxId, parsed.effectiveLength);
      } on CorruptedError catch (primaryError) {
        // Decision D1: `.bak` holds the generation *before* the last rotation,
        // so replacing the primary with it silently rolls back every write made
        // since. It is only used when the caller explicitly opted in; otherwise
        // the error reports where that copy is kept for offline recovery.
        final bakAvailable = File(bakPath).existsSync();

        if (_options.autoRecoverFromBackup && !_options.readOnly && bakAvailable) {
          try {
            // `.bak` is a recovery source: load it as-is, never tail-truncate (G1).
            final bakBytes = File(bakPath).readAsBytesSync();
            final bakParsed = FrameLoader.loadAllWithRecovery(bakBytes);
            File(bakPath).copySync(dbPath);
            _applyParsed(bakParsed.namespaces, bakParsed.maxTxId, bakBytes.length);
            _options.onWarning?.call(
              'onRecoveredFromBackup',
              'Recovered database "$_path" from backup "$bakPath"',
            );
            return;
          } catch (_) {
            throw CorruptedError(
              '${primaryError.message}; the previous generation kept at '
              '"$bakPath" could not be loaded either',
            );
          }
        }

        if (bakAvailable) {
          throw CorruptedError(
            '${primaryError.message}; a previous generation is kept at "$bakPath": '
            'copy it over the primary file to recover, or open with '
            'autoRecoverFromBackup = true',
          );
        }

        rethrow;
      }
    } else if (File(bakPath).existsSync()) {
      // Branch ③: primary does not exist but a previous generation does.
      //
      // This state cannot be produced by an interrupted rotation -- that always
      // leaves a complete `.tmp` behind, which the step above promotes -- so it
      // means the primary file disappeared (deleted or replaced externally).
      // Promoting the `.bak` on top of it is a silent downgrade to an older
      // generation, and when the `.bak` itself is unreadable the open used to
      // succeed as an *empty* database: the fourth silent data loss after C1-C3
      // (D1-7/D1-8). Both cases are refused unless the caller explicitly opted into
      // `.bak` recovery.
      final bakBytes = File(bakPath).readAsBytesSync();
      ParsedResult bakParsed;
      try {
        // `.bak` is a recovery source: load it as-is, never tail-truncate (G1).
        bakParsed = FrameLoader.loadAllWithRecovery(bakBytes);
      } on CorruptedError catch (bakError) {
        throw CorruptedError(
          'Primary file "$dbPath" is missing and the previous generation kept at '
          '"$bakPath" could not be loaded either: ${bakError.message}',
        );
      }

      if (!_options.autoRecoverFromBackup || _options.readOnly) {
        throw CorruptedError(
          'Primary file "$dbPath" is missing while a previous generation is kept at '
          '"$bakPath": copy it over the primary file to recover, or open with '
          'autoRecoverFromBackup = true',
        );
      }

      File(bakPath).copySync(dbPath);
      _applyParsed(bakParsed.namespaces, bakParsed.maxTxId, bakBytes.length);
      _options.onWarning?.call(
        'onRecoveredFromBackup',
        'Recovered database "$_path" from backup "$bakPath"',
      );
    }
  }

  void _applyParsed(Map<String, NamespaceStore> namespaces, int maxTxId, int fileSize) {
    _namespaces.clear();
    _namespaces.addAll(namespaces);
    _currentMaxTxId = maxTxId;
    _lastKnownDiskMaxTxId = maxTxId;
    _lastKnownDiskFileSize = fileSize;
  }

  void _flushIfDirty() {
    if (_isDisposed || _options.readOnly || !_isDirty) return;
    try {
      flush();
    } catch (_) {}
  }

  /// Phase 1 of the write path: every validation that can reject a write runs here,
  /// **before** any in-memory mutation. Returns the max TxID currently on disk.
  ///
  /// Since C9 the inter-process lock is taken once by [open] and held for the whole
  /// lifetime of the connection, so it no longer appears here (that was C2).
  int _precheckDiskState() {
    if (_options.readOnly) {
      throw LockedError('Database is opened in read-only mode');
    }

    final dbFile = File(_path);
    int diskMaxTx = 0;
    int diskSize = 0;
    if (dbFile.existsSync()) {
      diskSize = dbFile.lengthSync();
      if ((_lastKnownDiskMaxTxId != 0 || _lastKnownDiskFileSize != 0) &&
          diskSize == _lastKnownDiskFileSize) {
        diskMaxTx = _lastKnownDiskMaxTxId;
      } else if (diskSize >= Constants.fileHeaderLen) {
        final raf = dbFile.openSync(mode: FileMode.read);
        try {
          diskMaxTx = FrameLoader.scanMaxTxId(raf, diskSize);
        } finally {
          raf.closeSync();
        }
      }

      final stateMatches = diskSize == _lastKnownDiskFileSize &&
          (diskSize == Constants.fileHeaderLen || diskMaxTx == _lastKnownDiskMaxTxId);
      if ((_lastKnownDiskMaxTxId != 0 || _lastKnownDiskFileSize != 0) && !stateMatches) {
        throw ConcurrentModificationError(
          'Disk state changed externally: expected ($_lastKnownDiskMaxTxId, $_lastKnownDiskFileSize), found ($diskMaxTx, $diskSize)',
        );
      }
    }

    return diskMaxTx;
  }

  Uint8List _serializeSnapshotBytes(int diskMaxTxId, List<int> outMaxTxId) {
    final bb = BytesBuilder(copy: false);
    bb.add(FileHeader.createBytes());

    int totalEntries = 0;
    for (final store in _namespaces.values) {
      totalEntries += store.items.length;
    }

    if (totalEntries == 0) {
      outMaxTxId.add(_currentMaxTxId);
      return bb.takeBytes();
    }

    int nextTxId = (_currentMaxTxId > diskMaxTxId ? _currentMaxTxId : diskMaxTxId) + 1;
    final sortedNs = _namespaces.keys.toList()
      ..sort((a, b) => BinaryHelper.compareBytes(utf8.encode(a), utf8.encode(b)));

    final frameEntries = <RawOpEntry>[];
    int currentPayloadLen = 0;

    void flushFrame() {
      if (frameEntries.isEmpty) return;
      final payloadBb = BytesBuilder(copy: false);
      for (final e in frameEntries) {
        e.encodeTo(payloadBb);
      }
      final payloadBytes = payloadBb.takeBytes();
      final pCrc = Crc32.compute(payloadBytes);

      final frameHeader = Uint8List(Constants.frameHeaderLen);
      BinaryHelper.writeUint32LE(frameHeader, 0, Constants.frameMagic);

      final tail20 = Uint8List(20);
      BinaryHelper.writeUint64LE(tail20, 0, nextTxId++);
      BinaryHelper.writeUint32LE(tail20, 8, frameEntries.length);
      BinaryHelper.writeUint32LE(tail20, 12, payloadBytes.length);
      BinaryHelper.writeUint32LE(tail20, 16, pCrc);

      final hdrCrc = Crc32.compute(tail20);
      BinaryHelper.writeUint32LE(frameHeader, 4, hdrCrc);
      frameHeader.setRange(8, 28, tail20);

      bb.add(frameHeader);
      bb.add(payloadBytes);

      frameEntries.clear();
      currentPayloadLen = 0;
    }

    for (final ns in sortedNs) {
      final nsBytes = Uint8List.fromList(utf8.encode(ns));
      final store = _namespaces[ns]!;
      for (final item in store.items) {
        final entry = RawOpEntry(
          opCode: Constants.opPut,
          valueType: item.valueType,
          namespace: nsBytes,
          key: item.key,
          value: item.value,
        );
        final entryLen = entry.encodedLen();
        if (frameEntries.length >= Constants.maxOpsPerFrame ||
            (currentPayloadLen + entryLen > Constants.maxPayloadLen)) {
          flushFrame();
        }
        currentPayloadLen += entryLen;
        frameEntries.add(entry);
      }
    }

    if (frameEntries.isNotEmpty) {
      flushFrame();
    }

    outMaxTxId.add(nextTxId - 1);
    return bb.takeBytes();
  }

  void _commitFlush(int diskMaxTxId) {
    final tmpPath = '$_path.tmp';
    final bakPath = '$_path.bak';

    final outTx = <int>[];
    final finalData = _serializeSnapshotBytes(diskMaxTxId, outTx);
    final newMaxTxId = outTx.first;

    final tmpFile = File(tmpPath);
    final raf = tmpFile.openSync(mode: FileMode.write);
    try {
      raf.writeFromSync(finalData);
      if (_options.syncOnWrite) {
        raf.flushSync();
      }
    } finally {
      raf.closeSync();
    }

    if (File(_path).existsSync()) {
      replaceFileAtomicallyWithRetry(_path, bakPath);
    }
    replaceFileAtomicallyWithRetry(tmpPath, _path);

    // Directory fsync is deliberately a no-op here: `dart:io` exposes no
    // directory-descriptor API (`File`/`RandomAccessFile` can only be opened on
    // regular files), and CKV forbids FFI. Spec §1.2 item 7 classifies directory
    // fsync as best-effort for exactly this reason, so on Linux a power loss
    // immediately after the rename may still roll back the directory entry.
    // Rust/Go/.NET/Kotlin cover this window; Dart documents it instead of
    // pretending to close it.

    _currentMaxTxId = newMaxTxId;
    _lastKnownDiskFileSize = File(_path).lengthSync();
    _lastKnownDiskMaxTxId = _currentMaxTxId;
    _isDirty = false;
  }

  /// Writes to skip before retrying a failed automatic compaction: the first
  /// failure waits 64 writes, doubling per consecutive failure up to 4096.
  static int _compactionBackoff(int priorFailures) {
    const base = 64;
    const cap = 4096;
    final shift = priorFailures > 6 ? 6 : priorFailures;
    final window = base << shift;
    return window > cap ? cap : window;
  }

  /// Applies the automatic-compaction policy to a mutation that has just been
  /// applied: compact when the log crossed the threshold, and retry a previously
  /// failed compaction once its backoff window has elapsed.
  void _maybeCompactAfterWrite(int newFileSize) {
    if (_options.compactionThresholdBytes <= 0) return;

    if (_needsCompaction) {
      _writesSinceCompactionAttempt++;
      final priorFailures = _compactionFailures > 0 ? _compactionFailures - 1 : 0;
      if (_writesSinceCompactionAttempt < _compactionBackoff(priorFailures)) {
        return;
      }
      _attemptCompaction();
      return;
    }

    if (newFileSize >= _options.compactionThresholdBytes) {
      _attemptCompaction();
      return;
    }

    // Adaptive trigger: a log dominated by tombstones or overwritten values never
    // reaches the size threshold, so reclaim it by dead ratio instead.
    if (_shouldCompactByDeadRatio(newFileSize)) {
      _attemptCompaction();
    }
  }

  /// Live payload bytes across all namespaces. O(#namespaces): each store keeps
  /// its own ledger, so no entry is walked.
  int _livePayloadBytes() {
    var total = 0;
    for (final store in _namespaces.values) {
      total += store.liveBytes;
    }
    return total;
  }

  /// Whether an under-threshold log is mostly garbage. `liveBytes` only counts
  /// payload, so it is always below the file size (headers and frame overhead
  /// account for the difference); clamping keeps the ratio sane if the ledger
  /// were ever off.
  bool _shouldCompactByDeadRatio(int fileSize) {
    if (fileSize < _adaptiveMinFileSize) return false;
    var live = _livePayloadBytes();
    if (live > fileSize) live = fileSize;
    return (fileSize - live) * 100 >= fileSize * _adaptiveDeadRatioPercent;
  }

  /// Smallest physical log size at which the dead-ratio trigger is considered.
  static const int _adaptiveMinFileSize = 2 * 1024 * 1024;

  /// Dead-payload share (percent) at which an under-threshold log is compacted.
  static const int _adaptiveDeadRatioPercent = 50;

  /// Performs one automatic compaction and records the outcome.
  ///
  /// A failure never fails the caller's write unless `strictCompaction` is set:
  /// the mutation is already durable, so reporting an error would invite a retry
  /// of a write that succeeded, and `increment` (a read-modify-write) would then
  /// apply its delta twice (decision D2).
  void _attemptCompaction() {
    _writesSinceCompactionAttempt = 0;

    try {
      final diskMax = _precheckDiskState();
      _commitFlush(diskMax);
    } catch (err) {
      _compactionFailures++;
      _needsCompaction = true;

      // The warning fires in both modes; `strictCompaction` only decides whether
      // the error additionally reaches the caller.
      _options.onWarning?.call(
        'onCompactionFailed',
        'Compaction failed (size $_lastKnownDiskFileSize bytes, '
        'threshold ${_options.compactionThresholdBytes} bytes, '
        'consecutive failures $_compactionFailures): $err',
      );

      if (_options.strictCompaction) rethrow;
      return;
    }

    _needsCompaction = false;
    _compactionFailures = 0;
  }

  /// Best-effort retry of a pending compaction, called from `close()` so a
  /// transient failure does not outlive the handle. The outcome is never
  /// propagated: a failed attempt is already reported through `onWarning`.
  void _retryPendingCompaction() {
    if (!_needsCompaction ||
        _options.readOnly ||
        _options.engineMode != StorageEngineMode.appendOnlyLog) {
      return;
    }
    try {
      _attemptCompaction();
    } catch (_) {
      // Already reported through onWarning; close() must not start failing.
    }
  }

  /// Appends the mutation's frames to the log and returns the resulting file
  /// size.
  ///
  /// Compaction is deliberately *not* triggered here: the snapshot a compaction
  /// writes is taken from the current in-memory state, so it may only run once
  /// the mutation has been applied to memory (C1).
  int _appendEntries(List<RawOpEntry> entries) {
    if (entries.isEmpty) return 0;

    final diskMaxTxId = _precheckDiskState();
    final file = File(_path);
    final fileExists = file.existsSync();
    final raf = file.openSync(mode: FileMode.writeOnlyAppend);
    try {
      if (!fileExists || raf.lengthSync() == 0) {
        raf.writeFromSync(FileHeader.createBytes());
      }

      int currentTxId = (_currentMaxTxId > diskMaxTxId ? _currentMaxTxId : diskMaxTxId) + 1;
      final frameEntries = <RawOpEntry>[];
      int currentPayloadLen = 0;

      void flushFrame() {
        if (frameEntries.isEmpty) return;
        BatchFrameWriter.writeFrame(raf, currentTxId++, frameEntries);
        frameEntries.clear();
        currentPayloadLen = 0;
      }

      for (final entry in entries) {
        final entryLen = entry.encodedLen();
        if (frameEntries.length >= Constants.maxOpsPerFrame ||
            (currentPayloadLen + entryLen > Constants.maxPayloadLen)) {
          flushFrame();
        }
        currentPayloadLen += entryLen;
        frameEntries.add(entry);
      }

      if (frameEntries.isNotEmpty) {
        flushFrame();
      }

      final finalMaxTxId = currentTxId - 1;

      if (_options.syncMode == SyncMode.immediate) {
        raf.flushSync();
        _isDirty = false;
      } else {
        _isDirty = true;
      }

      final newFileSize = raf.lengthSync();
      _currentMaxTxId = finalMaxTxId;
      _lastKnownDiskMaxTxId = finalMaxTxId;
      _lastKnownDiskFileSize = newFileSize;

      return newFileSize;
    } finally {
      try {
        raf.closeSync();
      } catch (_) {}
    }
  }

  void _applyMutation(List<RawOpEntry> entries, void Function() applyMem) {
    if (_options.readOnly) {
      throw LockedError('Database is opened in read-only mode');
    }

    if (_options.engineMode == StorageEngineMode.appendOnlyLog) {
      // Order matters (C1/C2):
      //   1. append the frames (durability first),
      //   2. apply the mutation to the in-memory state,
      //   3. only now consider compaction, whose snapshot is serialised from the
      //      in-memory state and must therefore already contain this mutation.
      // `_appendEntries` releases the `.db` lock before returning, so the
      // compaction below acquires it in its own lock cycle instead of colliding
      // with the lock the caller held (C2).
      final newFileSize = _appendEntries(entries);
      applyMem();
      _maybeCompactAfterWrite(newFileSize);
    } else if (_options.syncMode == SyncMode.immediate) {
      final diskMaxTxId = _precheckDiskState();
      applyMem();
      _commitFlush(diskMaxTxId);
    } else {
      applyMem();
      _currentMaxTxId++;
      _isDirty = true;
    }
  }

  NamespaceStore _getOrCreateStoreForWrite(String normNs) {
    var store = _namespaces[normNs];
    if (store == null) {
      store = NamespaceStore();
      _namespaces[normNs] = store;
      return store;
    }
    if (_snapshotCount > 0) {
      store = store.clone();
      _namespaces[normNs] = store;
    }
    return store;
  }

  void _dispatchEvents(List<KeyChangeEvent> events) {
    if (events.isEmpty || _watchers.isEmpty) return;
    final snapshot = List<_WatcherSlot>.from(_watchers);
    for (final ev in events) {
      for (final slot in snapshot) {
        slot.callback(ev);
      }
    }
  }

  void _removeWatcher(int id) {
    _watchers.removeWhere((w) => w.id == id);
  }

  // ================= API Implementation =================

  void compact() {
    if (_options.readOnly) {
      throw LockedError('Database is opened in read-only mode');
    }
    final diskMax = _precheckDiskState();
    _commitFlush(diskMax);
  }

  void flush() {
    if (_options.readOnly) {
      throw LockedError('Database is opened in read-only mode');
    }
    if (!_isDirty && File(_path).existsSync()) return;

    if (_options.engineMode == StorageEngineMode.appendOnlyLog) {
      if (File(_path).existsSync()) {
        final raf = File(_path).openSync(mode: FileMode.writeOnlyAppend);
        try {
          raf.flushSync();
        } finally {
          raf.closeSync();
        }
      }
      _isDirty = false;
      return;
    }

    final diskMax = _precheckDiskState();
    _commitFlush(diskMax);
  }

  void backup(String dest) {
    final outTx = <int>[];
    final data = _serializeSnapshotBytes(0, outTx);
    final tmpPath = '$dest.tmp';
    final tmpFile = File(tmpPath);
    final raf = tmpFile.openSync(mode: FileMode.write);
    try {
      raf.writeFromSync(data);
      if (_options.syncOnWrite) {
        raf.flushSync();
      }
    } finally {
      raf.closeSync();
    }
    replaceFileAtomicallyWithRetry(tmpPath, dest);
  }

  int fileSize() {
    final f = File(_path);
    return f.existsSync() ? f.lengthSync() : 0;
  }

  DbStats stats() {
    int total = 0;
    for (final store in _namespaces.values) {
      total += store.items.length;
    }
    return DbStats(
      fileSize: fileSize(),
      namespaceCount: _namespaces.length,
      totalEntries: total,
      currentMaxTxId: _currentMaxTxId,
    );
  }

  List<String> namespaces() {
    final list = _namespaces.keys.toList();
    list.sort((a, b) => BinaryHelper.compareBytes(utf8.encode(a), utf8.encode(b)));
    return list;
  }

  WatcherHandle watch(KeyChangeCallback callback) {
    final id = ++_nextWatcherId;
    final slot = _WatcherSlot(id, callback, this);
    _watchers.add(slot);
    return slot;
  }

  Snapshot createSnapshot() {
    _snapshotCount++;
    final snapNs = Map<String, NamespaceStore>.from(_namespaces);
    return Snapshot(_currentMaxTxId, snapNs, () {
      if (_snapshotCount > 0) _snapshotCount--;
    });
  }

  Uint8List? getBytes(String? ns, String key) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs];
    if (store == null) return null;
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final idx = store.findIndex(keyBytes);
    if (idx < 0) return null;
    return Uint8List.fromList(store.items[idx].value);
  }

  String? getString(String? ns, String key) {
    final val = getBytes(ns, key);
    if (val == null) return null;
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs]!;
    final item = store.items[store.findIndex(Uint8List.fromList(utf8.encode(key)))];
    if (item.valueType != Constants.valTypeString) {
      throw TypeMismatchError(
        "Key '$key' has value_type ${item.valueType}, expected string (0x01)",
      );
    }
    if (!BinaryHelper.isValidUtf8(val)) {
      throw InvalidUtf8Error("Invalid UTF-8 sequence in key '$key'");
    }
    return utf8.decode(val);
  }

  int? getInt64(String? ns, String key) {
    final val = getBytes(ns, key);
    if (val == null) return null;
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final item = _namespaces[normNs]!.items[_namespaces[normNs]!.findIndex(Uint8List.fromList(utf8.encode(key)))];
    if (item.valueType != Constants.valTypeInt64) {
      throw TypeMismatchError(
        "Key '$key' has value_type ${item.valueType}, expected int64 (0x02)",
      );
    }
    return BinaryHelper.decodeInt64(val);
  }

  double? getFloat64(String? ns, String key) {
    final val = getBytes(ns, key);
    if (val == null) return null;
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final item = _namespaces[normNs]!.items[_namespaces[normNs]!.findIndex(Uint8List.fromList(utf8.encode(key)))];
    if (item.valueType != Constants.valTypeFloat64) {
      throw TypeMismatchError(
        "Key '$key' has value_type ${item.valueType}, expected float64 (0x03)",
      );
    }
    return BinaryHelper.decodeFloat64(val);
  }

  bool? getBool(String? ns, String key) {
    final val = getBytes(ns, key);
    if (val == null) return null;
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final item = _namespaces[normNs]!.items[_namespaces[normNs]!.findIndex(Uint8List.fromList(utf8.encode(key)))];
    if (item.valueType != Constants.valTypeBool) {
      throw TypeMismatchError(
        "Key '$key' has value_type ${item.valueType}, expected bool (0x04)",
      );
    }
    return BinaryHelper.decodeBool(val);
  }

  bool containsKey(String? ns, String key) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs];
    if (store == null) return false;
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    return store.findIndex(keyBytes) >= 0;
  }

  int count(String? ns) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    return _namespaces[normNs]?.items.length ?? 0;
  }

  List<String> keys(String? ns, [String? prefix]) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs];
    if (store == null) return [];
    final prefixBytes = prefix != null ? Uint8List.fromList(utf8.encode(prefix)) : null;
    final res = <String>[];
    for (final item in store.items) {
      if (prefixBytes == null || BinaryHelper.startsWith(item.key, prefixBytes)) {
        if (BinaryHelper.isValidUtf8(item.key)) {
          res.add(utf8.decode(item.key));
        }
      }
    }
    return res;
  }

  List<Entry> iterPrefix(String? ns, [String? prefix]) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs];
    if (store == null) return [];
    final prefixBytes = prefix != null ? Uint8List.fromList(utf8.encode(prefix)) : null;
    final res = <Entry>[];
    for (final item in store.items) {
      if (prefixBytes == null || BinaryHelper.startsWith(item.key, prefixBytes)) {
        res.add(
          Entry(
            normNs,
            utf8.decode(item.key, allowMalformed: true),
            item.valueType,
            Uint8List.fromList(item.value),
          ),
        );
      }
    }
    return res;
  }

  List<Entry> scanRange(String? ns, [String? startKey, String? endKey]) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs];
    if (store == null) return [];

    final startBytes = startKey != null ? Uint8List.fromList(utf8.encode(startKey)) : null;
    final endBytes = endKey != null ? Uint8List.fromList(utf8.encode(endKey)) : null;

    if (startBytes != null && endBytes != null && BinaryHelper.compareBytes(startBytes, endBytes) >= 0) {
      return [];
    }

    int startIdx = 0;
    if (startBytes != null) {
      final found = store.findIndex(startBytes);
      startIdx = found >= 0 ? found : ~found;
    }

    final res = <Entry>[];
    for (int i = startIdx; i < store.items.length; i++) {
      final item = store.items[i];
      if (endBytes != null && BinaryHelper.compareBytes(item.key, endBytes) >= 0) {
        break;
      }
      res.add(
        Entry(
          normNs,
          utf8.decode(item.key, allowMalformed: true),
          item.valueType,
          Uint8List.fromList(item.value),
        ),
      );
    }
    return res;
  }

  void _putInternal(String normNs, String key, Uint8List keyBytes, int valType, Uint8List valBytes) {
    final nsBytes = Uint8List.fromList(utf8.encode(normNs));
    final entries = [
      RawOpEntry(
        opCode: Constants.opPut,
        valueType: valType,
        namespace: nsBytes,
        key: keyBytes,
        value: valBytes,
      )
    ];

    _applyMutation(entries, () {
      final store = _getOrCreateStoreForWrite(normNs);
      store.put(keyBytes, valType, valBytes);
    });

    _dispatchEvents([
      KeyChangeEvent(KeyChangeOp.put, normNs, key, valType),
    ]);
  }

  void putBytes(String? ns, String key, Uint8List val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    BatchWriter.validateLengths(keyBytes, val);
    _putInternal(normNs, key, keyBytes, Constants.valTypeBytes, val);
  }

  void putString(String? ns, String key, String val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final valBytes = Uint8List.fromList(utf8.encode(val));
    BatchWriter.validateLengths(keyBytes, valBytes);
    _putInternal(normNs, key, keyBytes, Constants.valTypeString, valBytes);
  }

  void putInt64(String? ns, String key, int val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final valBytes = BinaryHelper.encodeInt64(val);
    BatchWriter.validateLengths(keyBytes, valBytes);
    _putInternal(normNs, key, keyBytes, Constants.valTypeInt64, valBytes);
  }

  void putFloat64(String? ns, String key, double val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final valBytes = BinaryHelper.encodeFloat64(val);
    BatchWriter.validateLengths(keyBytes, valBytes);
    _putInternal(normNs, key, keyBytes, Constants.valTypeFloat64, valBytes);
  }

  void putBool(String? ns, String key, bool val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final valBytes = BinaryHelper.encodeBool(val);
    BatchWriter.validateLengths(keyBytes, valBytes);
    _putInternal(normNs, key, keyBytes, Constants.valTypeBool, valBytes);
  }

  int incrementInt64(String? ns, String key, int delta) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    BatchWriter.validateLengths(keyBytes, Uint8List(0));

    int newVal;
    final existingStore = _namespaces[normNs];
    if (existingStore == null) {
      newVal = delta;
    } else {
      final idx = existingStore.findIndex(keyBytes);
      if (idx < 0) {
        newVal = delta;
      } else {
        final item = existingStore.items[idx];
        if (item.valueType != Constants.valTypeInt64) {
          throw TypeMismatchError(
            "Key '$key' has value_type ${item.valueType}, expected int64 (0x02)",
          );
        }
        final current = BinaryHelper.decodeInt64(item.value);
        newVal = (current + delta).toSigned(64);
      }
    }

    final valBytes = BinaryHelper.encodeInt64(newVal);
    final nsBytes = Uint8List.fromList(utf8.encode(normNs));
    final entries = [
      RawOpEntry(
        opCode: Constants.opPut,
        valueType: Constants.valTypeInt64,
        namespace: nsBytes,
        key: keyBytes,
        value: valBytes,
      )
    ];

    _applyMutation(entries, () {
      final store = _getOrCreateStoreForWrite(normNs);
      store.put(keyBytes, Constants.valTypeInt64, valBytes);
    });

    _dispatchEvents([
      KeyChangeEvent(KeyChangeOp.put, normNs, key, Constants.valTypeInt64),
    ]);
    return newVal;
  }

  bool delete(String? ns, String key) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs];
    if (store == null) return false;
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final idx = store.findIndex(keyBytes);
    if (idx < 0) return false;

    final nsBytes = Uint8List.fromList(utf8.encode(normNs));
    final entries = [
      RawOpEntry(
        opCode: Constants.opDelete,
        valueType: Constants.valTypeBytes,
        namespace: nsBytes,
        key: keyBytes,
        value: Uint8List(0),
      )
    ];

    bool deleted = false;
    _applyMutation(entries, () {
      var writeStore = store;
      if (_snapshotCount > 0) {
        writeStore = store.clone();
        _namespaces[normNs] = writeStore;
      }
      deleted = writeStore.delete(keyBytes);
    });

    if (deleted) {
      _dispatchEvents([
        KeyChangeEvent(KeyChangeOp.delete, normNs, key),
      ]);
    }
    return deleted;
  }

  bool dropNamespace(String? ns) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    if (!_namespaces.containsKey(normNs)) return false;

    final nsBytes = Uint8List.fromList(utf8.encode(normNs));
    final entries = [
      RawOpEntry(
        opCode: Constants.opDropNamespace,
        valueType: Constants.valTypeBytes,
        namespace: nsBytes,
        key: Uint8List(0),
        value: Uint8List(0),
      )
    ];

    _applyMutation(entries, () {
      _namespaces.remove(normNs);
    });

    _dispatchEvents([
      KeyChangeEvent(KeyChangeOp.dropNamespace, normNs, ''),
    ]);
    return true;
  }

  bool clearNamespace(String? ns) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs];
    if (store == null || store.items.isEmpty) return false;

    final nsBytes = Uint8List.fromList(utf8.encode(normNs));
    final entries = store.items.map((item) {
      return RawOpEntry(
        opCode: Constants.opDelete,
        valueType: Constants.valTypeBytes,
        namespace: nsBytes,
        key: item.key,
        value: Uint8List(0),
      );
    }).toList();

    _applyMutation(entries, () {
      _namespaces[normNs] = NamespaceStore();
    });

    _dispatchEvents([
      KeyChangeEvent(KeyChangeOp.clear, normNs, ''),
    ]);
    return true;
  }

  void batch(void Function(BatchWriter) action) {
    final writer = BatchWriter(
      readBytes: (normNs, key) => getBytes(normNs, key),
      readString: (normNs, key) => getString(normNs, key),
    );
    action(writer);
    if (writer.ops.isEmpty) return;

    if (_options.readOnly) {
      throw LockedError('Database is opened in read-only mode');
    }

    final rawEntries = <RawOpEntry>[];
    for (final op in writer.ops) {
      final nsBytes = Uint8List.fromList(utf8.encode(op.namespace));
      switch (op.opType) {
        case Constants.opPut:
          rawEntries.add(
            RawOpEntry(
              opCode: Constants.opPut,
              valueType: op.valType,
              namespace: nsBytes,
              key: op.key,
              value: op.val,
            ),
          );
          break;
        case Constants.opDelete:
          rawEntries.add(
            RawOpEntry(
              opCode: Constants.opDelete,
              valueType: Constants.valTypeBytes,
              namespace: nsBytes,
              key: op.key,
              value: Uint8List(0),
            ),
          );
          break;
        case Constants.opDropNamespace:
          rawEntries.add(
            RawOpEntry(
              opCode: Constants.opDropNamespace,
              valueType: Constants.valTypeBytes,
              namespace: nsBytes,
              key: Uint8List(0),
              value: Uint8List(0),
            ),
          );
          break;
        case Constants.batchOpClearStaging:
          // C3: a Put issued earlier in this same batch is not in `_namespaces`
          // yet, so expanding the clear from memory alone would leave its OpPut in
          // the log with no matching delete -- replaying the log would bring that
          // key back to life. Drop the shadowed puts (the clear removes them again
          // by definition) before collecting the deletes below.
          rawEntries.removeWhere(
            (entry) =>
                entry.opCode == Constants.opPut &&
                _sameBytes(entry.namespace, nsBytes),
          );

          final clrStore = _namespaces[op.namespace];
          if (clrStore != null) {
            for (final item in clrStore.items) {
              rawEntries.add(
                RawOpEntry(
                  opCode: Constants.opDelete,
                  valueType: Constants.valTypeBytes,
                  namespace: nsBytes,
                  key: item.key,
                  value: Uint8List(0),
                ),
              );
            }
          }
          break;
      }
    }

    final events = <KeyChangeEvent>[];
    _applyMutation(rawEntries, () {
      for (final op in writer.ops) {
        switch (op.opType) {
          case Constants.opPut:
            final store = _getOrCreateStoreForWrite(op.namespace);
            store.put(op.key, op.valType, op.val);
            events.add(
              KeyChangeEvent(
                KeyChangeOp.put,
                op.namespace,
                utf8.decode(op.key),
                op.valType,
              ),
            );
            break;
          case Constants.opDelete:
            final store = _namespaces[op.namespace];
            if (store != null) {
              final idx = store.findIndex(op.key);
              if (idx >= 0) {
                var writeStore = store;
                if (_snapshotCount > 0) {
                  writeStore = store.clone();
                  _namespaces[op.namespace] = writeStore;
                }
                writeStore.delete(op.key);
                events.add(
                  KeyChangeEvent(
                    KeyChangeOp.delete,
                    op.namespace,
                    utf8.decode(op.key),
                  ),
                );
              }
            }
            break;
          case Constants.opDropNamespace:
            if (_namespaces.remove(op.namespace) != null) {
              events.add(
                KeyChangeEvent(KeyChangeOp.dropNamespace, op.namespace, ''),
              );
            }
            break;
          case Constants.batchOpClearStaging:
            final store = _namespaces[op.namespace];
            if (store != null && store.items.isNotEmpty) {
              _namespaces[op.namespace] = NamespaceStore();
              events.add(KeyChangeEvent(KeyChangeOp.clear, op.namespace, ''));
            }
            break;
        }
      }
    });

    _dispatchEvents(events);
  }

  void close() {
    if (_isDisposed) return;
    _isDisposed = true;

    _syncTimer?.cancel();
    _syncTimer = null;

    if (_isDirty && !_options.readOnly) {
      try {
        if (_options.engineMode == StorageEngineMode.appendOnlyLog) {
          if (File(_path).existsSync()) {
            final raf = File(_path).openSync(mode: FileMode.writeOnlyAppend);
            try {
              raf.flushSync();
            } finally {
              raf.closeSync();
            }
          }
          _isDirty = false;
        } else {
          final diskMax = _precheckDiskState();
          _commitFlush(diskMax);
        }
      } catch (_) {}
    }

    // D2: give a compaction that failed earlier one more chance, so a transient
    // failure does not outlive the handle.
    _retryPendingCompaction();

    // C9: release the connection lock (the lock file itself is kept as a marker)
    _connectionLock?.release();
    _connectionLock = null;

    _registry.remove(_canonicalPath);
  }

  void dispose() => close();
}

/// Byte-wise equality for two byte lists (used to match a batch op's namespace
/// against the entries already staged for the log).
bool _sameBytes(Uint8List a, Uint8List b) {
  if (identical(a, b)) return true;
  if (a.length != b.length) return false;
  for (var i = 0; i < a.length; i++) {
    if (a[i] != b[i]) return false;
  }
  return true;
}

/// Renames `src` over `dst`, replacing `dst` atomically where the platform allows
/// it.
///
/// The destination is **never** removed first (C5): `renameSync` replaces it on
/// both POSIX and Windows, so a preceding `deleteSync` only opens a window in
/// which `.db` and `.bak` are *both* absent -- a crash inside that window leaves no
/// generation at all.
///
/// Internal plumbing: deliberately not exported from `ckv.dart`, but public within
/// this library so that `test/rotation_atomicity_test.dart` can exercise the
/// primitive directly (the rotation of a live database cannot be made to fail
/// through the public API).
void replaceFileAtomically(String src, String dst) {
  File(src).renameSync(dst);
}

/// [replaceFileAtomically] with a short retry ladder for transient sharing
/// violations (antivirus, indexer, a handle that has not been closed yet), and a
/// staged-copy fallback for a destination that flatly refuses to be replaced.
void replaceFileAtomicallyWithRetry(String src, String dst) {
  const backoffs = [5, 15, 30, 60, 120];
  for (int attempt = 0; attempt < backoffs.length; attempt++) {
    try {
      replaceFileAtomically(src, dst);
      return;
    } catch (_) {
      sleep(Duration(milliseconds: backoffs[attempt]));
    }
  }

  // Last resort: `rename` cannot replace `dst` (e.g. it is held exclusively by a
  // scanner). Copying straight onto `dst` would truncate it first and could leave a
  // *partial* file behind, so the copy is staged next to the destination and only
  // then renamed over it. If even that fails, the staged copy is kept so that both
  // generations still exist on disk, and the error is reported instead of being
  // swallowed.
  final staged = '$dst.stage';
  final copied = File(src).copySync(staged);
  final srcLen = File(src).lengthSync();
  if (copied.lengthSync() != srcLen) {
    throw StateError(
      'staged copy of "$src" is ${copied.lengthSync()} bytes instead of $srcLen',
    );
  }
  File(staged).renameSync(dst);
  File(src).deleteSync();
}
