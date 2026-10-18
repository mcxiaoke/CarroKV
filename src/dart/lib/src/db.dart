import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;
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
  final _SharedDbInstance _owner;
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

class _FinalizerToken {
  final _SharedDbInstance shared;
  final WarningCallback? onWarning;
  bool isDisposed = false;
  _FinalizerToken(this.shared, this.onWarning);
}

/// [CkvDb] 是操作数据库的连接句柄（ADR-20261017）。
/// 每次 [open] 均返回独立的句柄包装，内部持有对进程内同路径唯一的 [_SharedDbInstance] 底座引用。
/// 单个句柄调用 [close] 仅关闭当前连接并递减引用计数，底座与其他活跃句柄保持完好。
class CkvDb {
  final _SharedDbInstance _shared;
  final _FinalizerToken _token;
  bool _isDisposed = false;

  static final Finalizer<_FinalizerToken> _finalizer = Finalizer((token) {
    if (!token.isDisposed) {
      token.isDisposed = true;
      token.onWarning?.call(
          'onFinalizerLeak', 'Ckv DB handle was collected without close()');
      token.shared._release();
    }
  });

  CkvDb._(this._shared)
      : _token = _FinalizerToken(_shared, _shared._options.onWarning) {
    _finalizer.attach(this, _token, detach: this);
  }

  Object get sharedInstance => _shared;
  bool get isClosed => _isDisposed;

  void _checkDisposed() {
    if (_isDisposed) {
      throw LockedError(
          'Database is closed: operations are refused after close()');
    }
  }

  void _checkWritable() {
    if (_isDisposed) {
      throw LockedError('Database is closed: writes are refused after close()');
    }
    if (_shared._options.readOnly) {
      throw LockedError('Database is opened in read-only mode');
    }
  }

  /// §7.4 静态备份接口：以只读模式打开 source 数据库，将其一致性快照原子备份到 dest，完成后自动关闭连接。
  static void backupFile(String source, String dest, [DbOptions? options]) {
    final opts = options?.clone() ?? DbOptions();
    opts.readOnly = true;
    final db = open(source, opts);
    try {
      db.backup(dest);
    } finally {
      db.close();
    }
  }

  static CkvDb open(String path, [DbOptions? options]) {
    final opts = options?.clone() ?? DbOptions();
    final canonical = p.canonicalize(path);

    final existing = _SharedDbInstance._registry[canonical];
    if (existing != null && !existing._isDisposed) {
      _SharedDbInstance._checkOptionsConflict(existing._options, opts);
      existing.refCount++;
      return CkvDb._(existing);
    }

    final shared = _SharedDbInstance._(path, canonical, opts);
    shared._connectionLock = LockFile.acquire(path, opts);
    try {
      shared._initAndRecover();
      shared._startSyncTimer();
    } catch (_) {
      shared._syncTimer?.cancel();
      shared._syncTimer = null;
      shared._connectionLock?.release();
      shared._connectionLock = null;
      rethrow;
    }
    _SharedDbInstance._registry[canonical] = shared;
    return CkvDb._(shared);
  }

  void close() {
    if (_isDisposed) return;
    _isDisposed = true;
    _token.isDisposed = true;
    _finalizer.detach(this);
    _shared._release();
  }

  void dispose() => close();

  /// 默认命名空间索引器语法糖（读取）
  String? operator [](String key) => getString(null, key);

  /// 默认命名空间索引器语法糖（写入或删除）
  void operator []=(String key, String? value) {
    if (value == null) {
      delete(null, key);
    } else {
      putString(null, key, value);
    }
  }

  /// 获取绑定到特定命名空间的免传 ns 便利门面（Namespace Scope）
  NamespaceScope ns([String? namespace]) =>
      NamespaceScope(this, namespace ?? '');

  /// 获取默认命名空间的免传 ns 便利门面
  NamespaceScope get defaultNs => ns('');

  /// 对齐 API 共有规范 snapshot 别名
  Snapshot snapshot() => createSnapshot();

  // ---------- 读操作（关闭后全部抛错防护，杜绝静默读脏数据） ----------

  Uint8List? getBytes(String? ns, String key) {
    _checkDisposed();
    return _shared.getBytes(ns, key);
  }

  String? getString(String? ns, String key) {
    _checkDisposed();
    return _shared.getString(ns, key);
  }

  int? getInt64(String? ns, String key) {
    _checkDisposed();
    return _shared.getInt64(ns, key);
  }

  double? getFloat64(String? ns, String key) {
    _checkDisposed();
    return _shared.getFloat64(ns, key);
  }

  bool? getBool(String? ns, String key) {
    _checkDisposed();
    return _shared.getBool(ns, key);
  }

  bool containsKey(String? ns, String key) {
    _checkDisposed();
    return _shared.containsKey(ns, key);
  }

  int count(String? ns) {
    _checkDisposed();
    return _shared.count(ns);
  }

  List<String> keys(String? ns, [String? prefix]) {
    _checkDisposed();
    return _shared.keys(ns, prefix);
  }

  List<Entry> iterPrefix(String? ns, [String? prefix]) {
    _checkDisposed();
    return _shared.iterPrefix(ns, prefix);
  }

  List<Entry> scanRange(String? ns, [String? startKey, String? endKey]) {
    _checkDisposed();
    return _shared.scanRange(ns, startKey, endKey);
  }

  List<String> namespaces() {
    _checkDisposed();
    return _shared.namespaces();
  }

  int fileSize() {
    _checkDisposed();
    return _shared.fileSize();
  }

  DbStats stats() {
    _checkDisposed();
    return _shared.stats();
  }

  Snapshot createSnapshot() {
    _checkDisposed();
    return _shared.createSnapshot();
  }

  WatcherHandle watch(KeyChangeCallback callback) {
    _checkDisposed();
    return _shared.watch(callback);
  }

  void backup(String dest) {
    _checkDisposed();
    _shared.backup(dest);
  }

  // ---------- 写操作 ----------

  void putBytes(String? ns, String key, Uint8List value) {
    _checkWritable();
    _shared.putBytes(ns, key, Uint8List.fromList(value));
  }

  void putString(String? ns, String key, String value) {
    _checkWritable();
    _shared.putString(ns, key, value);
  }

  void putInt64(String? ns, String key, int value) {
    _checkWritable();
    _shared.putInt64(ns, key, value);
  }

  void putFloat64(String? ns, String key, double value) {
    _checkWritable();
    _shared.putFloat64(ns, key, value);
  }

  void putBool(String? ns, String key, bool value) {
    _checkWritable();
    _shared.putBool(ns, key, value);
  }

  int incrementInt64(String? ns, String key, [int delta = 1]) {
    _checkWritable();
    return _shared.incrementInt64(ns, key, delta);
  }

  bool delete(String? ns, String key) {
    _checkWritable();
    return _shared.delete(ns, key);
  }

  bool dropNamespace(String? ns) {
    _checkWritable();
    return _shared.dropNamespace(ns);
  }

  bool clearNamespace(String? ns) {
    _checkWritable();
    return _shared.clearNamespace(ns);
  }

  void batch(void Function(BatchWriter) action) {
    _checkWritable();
    _shared.batch(action);
  }

  void flush() {
    _checkWritable();
    _shared.flush();
  }

  Future<void> flushAsync() async {
    _checkWritable();
    await _shared.flushAsync();
  }

  Future<void> backupAsync(String dest) async {
    _checkDisposed();
    await _shared.backupAsync(dest);
  }

  void compact() {
    _checkWritable();
    _shared.compact();
  }
}

class _SharedDbInstance {
  static final Map<String, _SharedDbInstance> _registry = {};
  int refCount = 1;

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

  /// Monotonic counter bumped by every in-memory mutation and by every
  /// synchronous commit. The async commit path samples it before serializing
  /// and re-checks it before renaming: a mismatch means the bytes it produced
  /// belong to an older generation and must be discarded instead of renamed
  /// over a newer `.db` (N-X6 / N-X9 family).
  int _mutationSerial = 0;

  /// Serializes the async commit paths (`flushAsync` / `backupAsync`) so two of
  /// them can never interleave around their `await` points and clobber each
  /// other's `.tmp` file. Null means "no async commit in flight".
  Future<void>? _asyncCommitChain;

  // Automatic-compaction failure state (decision D2): `_needsCompaction` is set
  // when an attempt failed and a retry is pending; the mutation that triggered it
  // is already durable.
  bool _needsCompaction = false;
  int _compactionFailures = 0;
  int _writesSinceCompactionAttempt = 0;

  Timer? _syncTimer;
  int _nextWatcherId = 0;
  final List<_WatcherSlot> _watchers = [];
  final List<void Function()> _pendingWarnings = [];

  _SharedDbInstance._(this._path, this._canonicalPath, this._options);

  void _queueWarning(String code, String message) {
    if (_options.onWarning != null) {
      _pendingWarnings.add(() {
        _options.onWarning!(code, message);
      });
    }
  }

  void _dispatchPendingWarnings() {
    if (_pendingWarnings.isEmpty) return;
    final list = List<void Function()>.from(_pendingWarnings);
    _pendingWarnings.clear();
    for (final fn in list) {
      fn();
    }
  }

  void _recordRollback(String ns, Map<String, NamespaceStore?> rollbackStores) {
    if (!rollbackStores.containsKey(ns)) {
      rollbackStores[ns] = _namespaces[ns];
    }
  }

  void _rollback(Map<String, NamespaceStore?> rollbackStores) {
    for (final entry in rollbackStores.entries) {
      if (entry.value != null) {
        _namespaces[entry.key] = entry.value!;
      } else {
        _namespaces.remove(entry.key);
      }
    }
  }

  /// 启动 Deferred 模式的后台刷盘定时器。
  ///
  /// FIX-P1-23：**只能在构造完全成功之后调用**。`Timer.periodic` 会让 Dart VM 的事件循环
  /// 保持存活；旧实现在构造函数里就起定时器，于是 `deferred` 模式下**任何** open() 失败
  /// （目录不存在、库损坏、LockedError）都会让进程永不退出——CLI 工具直接挂死。
  void _startSyncTimer() {
    if (_syncTimer != null) return;
    if (_options.readOnly || _options.syncMode != SyncMode.deferred) return;
    final interval = Duration(
      milliseconds: _options.syncIntervalMs > 0 ? _options.syncIntervalMs : 100,
    );
    _syncTimer = Timer.periodic(interval, (_) => _flushIfDirty());
  }

  /// Reentrant-open 选项冲突检查（§3.1 冻结）。
  ///
  /// **`readOnly` 冲突必须抛错，不得静默复用。** 命中单例时若只发一条 `onWarning`
  /// 就返回原实例，调用方以 `readOnly = true` 打开却拿到一个读写实例——那是静默提权；
  /// 而 `onWarning` 默认是 `null`，默认配置下连告警都看不见。反方向（要写拿到只读实例）
  /// 同样会让后续写入以 `LockedError` 的形式在远处失败、误导排查。两个方向都拒绝。
  ///
  /// `engineMode` / `syncMode` 冲突维持告警：它们只造成性能与落盘语义差异，不构成权限问题。
  static void _checkOptionsConflict(DbOptions existingOpts, DbOptions newOpts) {
    if (existingOpts.readOnly != newOpts.readOnly) {
      throw ConflictingOptionsError(
        'readOnly conflict on reentrant open: existing=${existingOpts.readOnly}, '
        'requested=${newOpts.readOnly}. The in-process singleton is returned unchanged by '
        'contract, so it cannot honour the requested permission. Close the other handle '
        'first, or open a different canonical path.',
      );
    }

    final conflicts = <String>[];
    if (existingOpts.engineMode != newOpts.engineMode) {
      conflicts.add(
          'engineMode (existing=${existingOpts.engineMode}, requested=${newOpts.engineMode})');
    }
    if (existingOpts.syncMode != newOpts.syncMode) {
      conflicts.add(
          'syncMode (existing=${existingOpts.syncMode}, requested=${newOpts.syncMode})');
    }
    if (conflicts.isNotEmpty) {
      final msg =
          'Options conflict on reentrant open: ${conflicts.join(", ")}. Using existing singleton options.';
      if (newOpts.onWarning != null) {
        newOpts.onWarning!('onOptionsConflict', msg);
      } else if (existingOpts.onWarning != null) {
        existingOpts.onWarning!('onOptionsConflict', msg);
      }
    }
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

    // M2-16：只读模式不得写盘。`.tmp` 的晋升与删除都会修改文件系统，因此整段跳过
    // （`.bak` 分支已有同样的守卫）。
    if (!_options.readOnly && File(tmpPath).existsSync()) {
      if (!dbExists) {
        // 主库不存在，此时 `.tmp` 是唯一完好的数据。必须区分两类失败：
        //  1. `.tmp` 读不出来 / 内容确实不可解析 → 删除它，继续走分支③（与旧行为一致）；
        //  2. `.tmp` 有效但晋升（renameSync）失败 —— 可能是杀软/索引器/预览窗格的瞬时占用
        //     —— 此时**必须保留 `.tmp` 并让 open() 失败**。旧实现的 catch-all 会把已经
        //     校验通过的 `.tmp` 一并删掉，即销毁唯一的好数据，还把一次瞬时 IO 失败伪装成
        //     "内容确实损坏"（C1-C3 之后的第 4 处静默数据丢失）。
        Uint8List? tmpBytes;
        try {
          tmpBytes = File(tmpPath).readAsBytesSync();
        } catch (_) {
          tmpBytes = null;
        }
        ParsedResult? parsed;
        if (tmpBytes != null) {
          try {
            parsed = FrameLoader.loadAllWithRecovery(tmpBytes);
          } catch (_) {
            parsed = null;
          }
        }

        if (parsed == null) {
          // 内容确实不可解析（或读不出来）：删除 .tmp 是安全的。
          try {
            File(tmpPath).deleteSync();
          } catch (_) {}
        } else {
          try {
            File(tmpPath).renameSync(dbPath);
          } catch (err) {
            throw CorruptedError(
              "Primary file '$dbPath' is missing and a valid '.tmp' could not be "
              'promoted to it (rename failed: $err); the only intact copy is still at '
              "'$tmpPath': close whatever holds the file handle and reopen",
            );
          }
          _applyParsed(
              parsed.namespaces, parsed.maxTxId, File(dbPath).lengthSync());
          dbExists = true;
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
          _options.engineMode == StorageEngineMode.appendOnlyLog &&
              !_options.readOnly;
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

        if (_options.autoRecoverFromBackup &&
            !_options.readOnly &&
            bakAvailable) {
          try {
            // `.bak` is a recovery source: load it as-is, never tail-truncate (G1).
            final bakBytes = File(bakPath).readAsBytesSync();
            final bakParsed = FrameLoader.loadAllWithRecovery(bakBytes);
            File(bakPath).copySync(dbPath);
            _applyParsed(
                bakParsed.namespaces, bakParsed.maxTxId, bakBytes.length);
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

  void _applyParsed(
      Map<String, NamespaceStore> namespaces, int maxTxId, int fileSize) {
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
    // RC2: one statSync() answers both "does it exist" and "how big is it",
    // replacing the existsSync() + lengthSync() pair (two path lookups per write,
    // each of them tens of microseconds on a filtered filesystem).
    final stat = dbFile.statSync();
    if (stat.type != FileSystemEntityType.notFound) {
      diskSize = stat.size;
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
          (diskSize == Constants.fileHeaderLen ||
              diskMaxTx == _lastKnownDiskMaxTxId);
      if ((_lastKnownDiskMaxTxId != 0 || _lastKnownDiskFileSize != 0) &&
          !stateMatches) {
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

    int nextTxId =
        (_currentMaxTxId > diskMaxTxId ? _currentMaxTxId : diskMaxTxId) + 1;
    final sortedNs = _namespaces.keys.toList()
      ..sort(
          (a, b) => BinaryHelper.compareBytes(utf8.encode(a), utf8.encode(b)));

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
    try {
      // 同步提交同样推进代际：在途的异步提交必须放弃它那份更旧的快照。
      _mutationSerial++;
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

      _currentMaxTxId = newMaxTxId;
      _lastKnownDiskFileSize = File(_path).lengthSync();
      _lastKnownDiskMaxTxId = _currentMaxTxId;
      _isDirty = false;
    } catch (_) {
      _isDirty = true;
      rethrow;
    }
  }

  Future<void> _commitFlushAsync(int diskMaxTxId) async {
    final serial = _mutationSerial;
    final txAtStart = _currentMaxTxId;
    // 必须在序列化之前同步取一次"主库是否存在"：若放在代际检查之后，
    // `await File(...).exists()` 本身就是一个挂起点，写者能插进来，导致
    // 轮转后的元数据（内存 TxID）与文件实际内容分叉。
    final bool primaryExists = File(_path).existsSync();
    try {
      final tmpPath = '$_path.tmp';
      final bakPath = '$_path.bak';

      final outTx = <int>[];
      final finalData = _serializeSnapshotBytes(diskMaxTxId, outTx);
      final newMaxTxId = outTx.first;

      final tmpFile = File(tmpPath);
      final raf = await tmpFile.open(mode: FileMode.write);
      try {
        await raf.writeFrom(finalData);
        if (_options.syncOnWrite) {
          await raf.flush();
        }
      } finally {
        await raf.close();
      }

      // N-X6：序列化与写 `.tmp` 都是 await 点，期间完全可能有同步写提交了一代新数据
      // （并已 rename 到 `.db`）。此时本轮的字节属于旧一代，rename 上去就是静默回滚，
      // 因此必须先比对代际：不一致就丢弃 `.tmp`、保留脏标记，交由下一轮重做。
      if (_mutationSerial != serial || _currentMaxTxId != txAtStart) {
        try {
          await tmpFile.delete();
        } catch (_) {}
        _isDirty = true;
        return;
      }

      // 代际检查之后到方法结束没有 await 点：轮转与元数据更新必须连着做。
      if (primaryExists) {
        replaceFileAtomicallyWithRetry(_path, bakPath);
      }
      replaceFileAtomicallyWithRetry(tmpPath, _path);
      // 元数据必须完全来自本轮写入的内容（字节数 + 目标 TxID），不能再去 stat 文件：
      // 轮转之后任何一次可能抛错的系统调用都会留下"磁盘已是新代、账本还是旧代"的
      // 分叉，下一次 precheck 就会把自己的写误报成外部并发修改。
      if (newMaxTxId > _currentMaxTxId) _currentMaxTxId = newMaxTxId;
      _lastKnownDiskFileSize = finalData.length;
      _lastKnownDiskMaxTxId = newMaxTxId;
      _isDirty = false;
    } catch (_) {
      _isDirty = true;
      rethrow;
    }
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
      final priorFailures =
          _compactionFailures > 0 ? _compactionFailures - 1 : 0;
      if (_writesSinceCompactionAttempt < _compactionBackoff(priorFailures)) {
        return;
      }
      _attemptCompaction();
      return;
    }

    // FIX-21: trigger on garbage, not on the absolute file size.
    //
    // A compaction leaves the file at exactly the live size, so the old
    // `newFileSize >= threshold` test was true on *every* write once live data
    // reached the threshold (measured at 6MB live: 26ms/write instead of ~0.2ms).
    // `garbage` is derived from the per-namespace ledger, so it needs no extra
    // state and stays correct after a reopen.
    final live = math.min(_livePayloadBytes(), newFileSize);
    final garbage = newFileSize - live;
    final effectiveThreshold =
        math.max(_options.compactionThresholdBytes, live ~/ 4);
    if (garbage >= effectiveThreshold) {
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
      _queueWarning(
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
    // RC2: openSync in append mode already creates the file, so the preceding
    // existsSync() was redundant; lengthSync() on the open handle is enough to
    // tell "needs a file header" from "already has one".
    final raf = file.openSync(mode: FileMode.writeOnlyAppend);
    try {
      if (raf.lengthSync() == 0) {
        raf.writeFromSync(FileHeader.createBytes());
      }

      int currentTxId =
          (_currentMaxTxId > diskMaxTxId ? _currentMaxTxId : diskMaxTxId) + 1;
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

  /// FIX-22: a handle kept past [close] must not write.
  ///
  /// `close()` releases the inter-process lock, so a write that still succeeded
  /// afterwards would land in a database another process may already hold - the
  /// exact two-writer situation the lock exists to prevent.
  void _checkWritable() {
    if (_isDisposed) {
      throw LockedError('Database is closed: writes are refused after close()');
    }
    if (_options.readOnly) {
      throw LockedError('Database is opened in read-only mode');
    }
  }

  void _applyMutation(
    List<RawOpEntry> entries,
    void Function(
            Set<String> clonedInTx, Map<String, NamespaceStore?> rollbackStores)
        applyMem,
  ) {
    _checkWritable();
    final rollbackStores = <String, NamespaceStore?>{};
    final clonedInTx = <String>{};

    try {
      if (_options.engineMode == StorageEngineMode.appendOnlyLog) {
        // Order matters (C1/C2):
        //   1. append the frames (durability first),
        //   2. apply the mutation to the in-memory state,
        //   3. only now consider compaction.
        final newFileSize = _appendEntries(entries);
        try {
          applyMem(clonedInTx, rollbackStores);
        } catch (e) {
          _rollback(rollbackStores);
          _isDirty = true;
          if (e is DbError) rethrow;
          throw IoError('AOL mutation failed: $e');
        }
        // N-X8：这次变异此刻已经 append 到日志（持久化成功），压实失败绝不能回滚
        // 内存——否则会话内读到旧值、崩溃重开读到新值，随后一次 flush 还会用旧内存
        // 覆盖日志，把已持久化的写永久抹掉（D2 的论证同样适用于此）。
        _maybeCompactAfterWrite(newFileSize);
      } else if (_options.syncMode == SyncMode.immediate) {
        final diskMaxTxId = _precheckDiskState();
        try {
          applyMem(clonedInTx, rollbackStores);
          _commitFlush(diskMaxTxId);
        } catch (e) {
          _rollback(rollbackStores);
          _isDirty = true;
          if (e is DbError) rethrow;
          throw IoError('Immediate flush failed: $e');
        }
      } else {
        applyMem(clonedInTx, rollbackStores);
        _currentMaxTxId++;
        _isDirty = true;
      }
      // 任何成功的内存变更都会推进代际，让在途的异步提交丢弃它的陈旧快照。
      _mutationSerial++;
    } finally {
      _dispatchPendingWarnings();
    }
  }

  NamespaceStore _getOrCreateStoreForWrite(String normNs,
      [Set<String>? clonedInTx]) {
    var store = _namespaces[normNs];
    if (store == null) {
      store = NamespaceStore();
      _namespaces[normNs] = store;
      clonedInTx?.add(normNs);
      return store;
    }
    // 这里的克隆**不是**为快照服务的可选优化：`_recordRollback` 记录的是克隆前的
    // store 对象，`_rollback` 靠它把内存恢复原状。所以只要本条写路径可能回滚
    // （三条分支都会），就必须先 CoW，否则"就地修改"会把回滚点一起改掉，
    // 回滚形同虚设（`batch_four_pipeline_test` 的 P0-7 用例正是这个断言）。
    // 想把稳态写成本降到与规模无关，必须把回滚改成 undo-log（按 key 记录旧值），
    // 而不是恢复批次三的 `_snapshotCount > 0` 守卫。
    if (clonedInTx != null) {
      if (!clonedInTx.contains(normNs)) {
        store = store.clone();
        _namespaces[normNs] = store;
        clonedInTx.add(normNs);
      }
    } else {
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
    _checkWritable();
    final diskMax = _precheckDiskState();
    _commitFlush(diskMax);
  }

  void flush() {
    _checkWritable();
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

  Future<void> flushAsync() => _runAsyncCommit(_flushAsyncOnce);

  /// Runs [action] after every previously queued async commit has finished.
  ///
  /// Dart is single-threaded, so the only way a commit can interleave with
  /// another one is at an `await` point. Chaining them removes that window
  /// entirely (two `flushAsync` calls used to write the same `.tmp`
  /// alternately), and it never blocks the event loop.
  Future<T> _runAsyncCommit<T>(Future<T> Function() action) {
    final Future<void> previous = _asyncCommitChain ?? Future<void>.value();
    final gate = Completer<void>();
    _asyncCommitChain = gate.future;
    return previous
        .catchError((Object _) {
          // 前一次提交失败不影响后续提交；它已经把自己的错误交给了它的调用方。
        })
        .then((_) => action())
        .whenComplete(() {
          if (!gate.isCompleted) gate.complete();
        });
  }

  Future<void> _flushAsyncOnce() async {
    _checkWritable();
    if (!_isDirty && await File(_path).exists()) return;

    if (_options.engineMode == StorageEngineMode.appendOnlyLog) {
      final serial = _mutationSerial;
      if (await File(_path).exists()) {
        final raf = await File(_path).open(mode: FileMode.writeOnlyAppend);
        try {
          await raf.flush();
        } finally {
          await raf.close();
        }
      }
      // 这段 await 期间可能有新帧追加：只有代际未变时才清脏，否则留给下一轮
      // （否则新帧永远等不到 fsync，掉电即丢）。
      if (_mutationSerial == serial) _isDirty = false;
      return;
    }

    // 陈旧快照被丢弃时脏标记保持为 true，这里再跑一轮把新状态写下去。
    // 上限避免持续写入导致的活锁；未写完的脏状态仍由下一次 flush/timer 收敛。
    var attempts = 0;
    while (_isDirty && attempts < 8) {
      attempts++;
      final diskMax = _precheckDiskState();
      await _commitFlushAsync(diskMax);
    }
  }

  void backup(String dest) {
    // M3-11：备份到主库自身（或它的 .tmp/.bak）会与写路径争抢同一个文件。
    final absDest = File(dest).absolute.path;
    final absSelf = File(_path).absolute.path;
    if (absDest == absSelf ||
        absDest == '$absSelf.tmp' ||
        absDest == '$absSelf.bak') {
      throw IoError(
          "Backup destination '$dest' collides with the database file");
    }
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

  Future<void> backupAsync(String dest) => _runAsyncCommit(() async {
        final outTx = <int>[];
        final data = _serializeSnapshotBytes(0, outTx);
        final tmpPath = '$dest.tmp';
        final tmpFile = File(tmpPath);
        final raf = await tmpFile.open(mode: FileMode.write);
        try {
          await raf.writeFrom(data);
          if (_options.syncOnWrite) {
            await raf.flush();
          }
        } finally {
          await raf.close();
        }
        replaceFileAtomicallyWithRetry(tmpPath, dest);
      });

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
    list.sort(
        (a, b) => BinaryHelper.compareBytes(utf8.encode(a), utf8.encode(b)));
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
    final item =
        store.items[store.findIndex(Uint8List.fromList(utf8.encode(key)))];
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
    final item = _namespaces[normNs]!.items[
        _namespaces[normNs]!.findIndex(Uint8List.fromList(utf8.encode(key)))];
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
    final item = _namespaces[normNs]!.items[
        _namespaces[normNs]!.findIndex(Uint8List.fromList(utf8.encode(key)))];
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
    final item = _namespaces[normNs]!.items[
        _namespaces[normNs]!.findIndex(Uint8List.fromList(utf8.encode(key)))];
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
    final prefixBytes =
        prefix != null ? Uint8List.fromList(utf8.encode(prefix)) : null;
    final res = <String>[];
    for (final item in store.items) {
      if (prefixBytes == null ||
          BinaryHelper.startsWith(item.key, prefixBytes)) {
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
    final prefixBytes =
        prefix != null ? Uint8List.fromList(utf8.encode(prefix)) : null;
    final res = <Entry>[];
    for (final item in store.items) {
      if (prefixBytes == null ||
          BinaryHelper.startsWith(item.key, prefixBytes)) {
        // API-SPEC §3.3.3：非法 UTF-8 键跳过（keys() 已按跳过处理）。
        if (!BinaryHelper.isValidUtf8(item.key)) continue;
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

    final startBytes =
        startKey != null ? Uint8List.fromList(utf8.encode(startKey)) : null;
    final endBytes =
        endKey != null ? Uint8List.fromList(utf8.encode(endKey)) : null;

    if (startBytes != null &&
        endBytes != null &&
        BinaryHelper.compareBytes(startBytes, endBytes) >= 0) {
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
      if (endBytes != null &&
          BinaryHelper.compareBytes(item.key, endBytes) >= 0) {
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

  void _putInternal(String normNs, String key, Uint8List keyBytes, int valType,
      Uint8List valBytes) {
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

    _applyMutation(entries, (clonedInTx, rollbackStores) {
      _recordRollback(normNs, rollbackStores);
      final store = _getOrCreateStoreForWrite(normNs, clonedInTx);
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

    _applyMutation(entries, (clonedInTx, rollbackStores) {
      _recordRollback(normNs, rollbackStores);
      final store = _getOrCreateStoreForWrite(normNs, clonedInTx);
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
    _applyMutation(entries, (clonedInTx, rollbackStores) {
      _recordRollback(normNs, rollbackStores);
      var writeStore = store;
      if (clonedInTx.add(normNs)) {
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

    _applyMutation(entries, (clonedInTx, rollbackStores) {
      _recordRollback(normNs, rollbackStores);
      _namespaces.remove(normNs);
      clonedInTx.remove(normNs);
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

    _applyMutation(entries, (clonedInTx, rollbackStores) {
      _recordRollback(normNs, rollbackStores);
      _namespaces[normNs] = NamespaceStore();
      clonedInTx.add(normNs);
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
    _applyMutation(rawEntries, (clonedInTx, rollbackStores) {
      for (final op in writer.ops) {
        _recordRollback(op.namespace, rollbackStores);
        switch (op.opType) {
          case Constants.opPut:
            final store = _getOrCreateStoreForWrite(op.namespace, clonedInTx);
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
                if (clonedInTx.add(op.namespace)) {
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
              clonedInTx.remove(op.namespace);
              events.add(
                KeyChangeEvent(KeyChangeOp.dropNamespace, op.namespace, ''),
              );
            }
            break;
          case Constants.batchOpClearStaging:
            final store = _namespaces[op.namespace];
            if (store != null && store.items.isNotEmpty) {
              _namespaces[op.namespace] = NamespaceStore();
              clonedInTx.add(op.namespace);
              events.add(KeyChangeEvent(KeyChangeOp.clear, op.namespace, ''));
            }
            break;
        }
      }
    });

    _dispatchEvents(events);
  }

  void _release() {
    refCount--;
    if (refCount <= 0) {
      if (identical(_registry[_canonicalPath], this)) {
        _registry.remove(_canonicalPath);
      }
      _closeUnderlying();
    }
  }

  void _closeUnderlying() {
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
      } catch (e) {
        // FIX-06: a failed final flush must be reported, not silently swallowed
        // (a full disk used to lose the pending writes with no signal at all).
        _options.onWarning?.call(
          'onFinalFlushFailed',
          'Final flush during close failed; pending writes may be lost: $e',
        );
      }
    }

    // D2: give a compaction that failed earlier one more chance, so a transient
    // failure does not outlive the handle.
    _retryPendingCompaction();

    // C9: release the connection lock (the lock file itself is kept as a marker)
    _connectionLock?.release();
    _connectionLock = null;
  }
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

/// 为特定命名空间提供免传 ns 参数的便利门面。
class NamespaceScope {
  final CkvDb db;
  final String namespace;

  NamespaceScope(this.db, this.namespace);

  String? operator [](String key) => getString(key);
  void operator []=(String key, String? value) {
    if (value == null) {
      delete(key);
    } else {
      putString(key, value);
    }
  }

  Uint8List? getBytes(String key) => db.getBytes(namespace, key);
  String? getString(String key) => db.getString(namespace, key);
  int? getInt64(String key) => db.getInt64(namespace, key);
  double? getFloat64(String key) => db.getFloat64(namespace, key);
  bool? getBool(String key) => db.getBool(namespace, key);
  bool containsKey(String key) => db.containsKey(namespace, key);

  void putBytes(String key, Uint8List value) =>
      db.putBytes(namespace, key, value);
  void putString(String key, String value) =>
      db.putString(namespace, key, value);
  void putInt64(String key, int value) => db.putInt64(namespace, key, value);
  void putFloat64(String key, double value) =>
      db.putFloat64(namespace, key, value);
  void putBool(String key, bool value) => db.putBool(namespace, key, value);

  int incrementInt64(String key, [int delta = 1]) =>
      db.incrementInt64(namespace, key, delta);
  bool delete(String key) => db.delete(namespace, key);
  int count() => db.count(namespace);
  List<String> keys([String? prefix]) => db.keys(namespace, prefix);
  List<Entry> iterPrefix([String? prefix]) => db.iterPrefix(namespace, prefix);
  List<Entry> scanRange([String? startKey, String? endKey]) =>
      db.scanRange(namespace, startKey, endKey);
  bool clear() => db.clearNamespace(namespace);
  bool drop() => db.dropNamespace(namespace);
}
