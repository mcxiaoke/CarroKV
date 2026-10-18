import 'dart:convert';
import 'dart:typed_data';

import 'binary_helper.dart';
import 'constants.dart';
import 'errors.dart';
import 'types.dart';

class KeyItem {
  final Uint8List key;
  final int valueType;
  final Uint8List value;

  KeyItem(this.key, this.valueType, this.value);
}

class NamespaceStore {
  final List<KeyItem> items;

  /// Σ(key.length + value.length) over [items], maintained by [put]/[delete].
  /// The dead-ratio compaction trigger relies on it staying O(1) per mutation;
  /// mutating [items] directly would silently desynchronise the ledger.
  int liveBytes = 0;

  NamespaceStore([List<KeyItem>? items]) : items = items ?? <KeyItem>[] {
    for (final item in this.items) {
      liveBytes += item.key.length + item.value.length;
    }
  }

  /// Rebuilds the O(1) ledger from [items].
  ///
  /// Bulk-load paths that append to [items] directly (the all-PUT fast path in
  /// the frame loader) bypass [put], so the ledger must be recomputed once
  /// afterwards. Leaving it at 0 makes every reopened AOL database look like
  /// 100% garbage and the compaction trigger fires on every write (spec §4.5).
  void recalculateLiveBytes() {
    var total = 0;
    for (final item in items) {
      total += item.key.length + item.value.length;
    }
    liveBytes = total;
  }

  NamespaceStore clone() {
    return NamespaceStore(List<KeyItem>.from(items));
  }

  int findIndex(Uint8List key) {
    int low = 0;
    int high = items.length - 1;

    while (low <= high) {
      final mid = (low + high) >>> 1;
      final cmp = BinaryHelper.compareBytes(items[mid].key, key);
      if (cmp < 0) {
        low = mid + 1;
      } else if (cmp > 0) {
        high = mid - 1;
      } else {
        return mid;
      }
    }
    return ~low; // Bitwise NOT: insertion point
  }

  void put(Uint8List key, int valType, Uint8List val) {
    final idx = findIndex(key);
    final item = KeyItem(key, valType, val);
    if (idx >= 0) {
      liveBytes += val.length - items[idx].value.length;
      items[idx] = item;
    } else {
      final insertIdx = ~idx;
      items.insert(insertIdx, item);
      liveBytes += key.length + val.length;
    }
  }

  bool delete(Uint8List key) {
    final idx = findIndex(key);
    if (idx >= 0) {
      final old = items.removeAt(idx);
      liveBytes -= old.key.length + old.value.length;
      return true;
    }
    return false;
  }
}

class Snapshot {
  final int maxTxId;
  final Map<String, NamespaceStore> _namespaces;
  final void Function()? _onDispose;
  bool _isDisposed = false;

  Snapshot(this.maxTxId, this._namespaces, [this._onDispose]);

  /// 默认命名空间索引器语法糖
  String? operator [](String key) => getString(null, key);

  /// 获取绑定到特定命名空间的免传 ns 快照门面
  SnapshotNamespaceScope ns([String? namespace]) =>
      SnapshotNamespaceScope(this, namespace ?? '');

  /// 获取默认命名空间的免传 ns 快照门面
  SnapshotNamespaceScope get defaultNs => ns('');

  void _checkDisposed() {
    if (_isDisposed) {
      throw DbError('Snapshot is already disposed');
    }
  }

  Uint8List? getBytes(String? ns, String key) {
    _checkDisposed();
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
    _checkDisposed();
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs];
    if (store == null) return false;
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    return store.findIndex(keyBytes) >= 0;
  }

  int count(String? ns) {
    _checkDisposed();
    final normNs = BinaryHelper.normalizeNamespace(ns);
    return _namespaces[normNs]?.items.length ?? 0;
  }

  List<String> namespaces() {
    _checkDisposed();
    final list = _namespaces.keys.toList();
    list.sort(
        (a, b) => BinaryHelper.compareBytes(utf8.encode(a), utf8.encode(b)));
    return list;
  }

  List<String> keys(String? ns, [String? prefix]) {
    _checkDisposed();
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
    _checkDisposed();
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final store = _namespaces[normNs];
    if (store == null) return [];
    final prefixBytes =
        prefix != null ? Uint8List.fromList(utf8.encode(prefix)) : null;
    final res = <Entry>[];
    for (final item in store.items) {
      if (prefixBytes == null ||
          BinaryHelper.startsWith(item.key, prefixBytes)) {
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
    _checkDisposed();
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

  void dispose() {
    if (_isDisposed) return;
    _isDisposed = true;
    _onDispose?.call();
  }
}

class BatchOp {
  final int opType;
  final String namespace;
  final Uint8List key;
  final int valType;
  final Uint8List val;

  BatchOp(this.opType, this.namespace, this.key, this.valType, this.val);
}

class BatchWriter {
  final List<BatchOp> ops = [];

  /// Fallback readers for keys this batch has not touched. Supplied by the
  /// owning database in `CkvDb.batch`; a standalone writer sees only staging.
  final Uint8List? Function(String ns, String key)? _readBytes;
  final String? Function(String ns, String key)? _readString;

  BatchWriter(
      {Uint8List? Function(String ns, String key)? readBytes,
      String? Function(String ns, String key)? readString})
      : _readBytes = readBytes,
        _readString = readString;

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

  /// 获取绑定到特定命名空间的免传 ns 批处理门面
  BatchNamespaceScope ns([String? namespace]) =>
      BatchNamespaceScope(this, namespace ?? '');

  /// 获取默认命名空间的免传 ns 批处理门面
  BatchNamespaceScope get defaultNs => ns('');

  static void validateLengths(List<int> key, List<int> val) {
    if (key.isEmpty && val.isEmpty) return;
    if (key.length > Constants.maxKeyLen) {
      throw KeyTooLargeError(
        'Key length ${key.length} exceeds maximum ${Constants.maxKeyLen}',
      );
    }
    if (val.length > Constants.maxValLen) {
      throw KeyTooLargeError(
        'Value length ${val.length} exceeds maximum ${Constants.maxValLen}',
      );
    }
  }

  BatchWriter putBytes(String? ns, String key, Uint8List val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final copyVal = Uint8List.fromList(val);
    validateLengths(keyBytes, copyVal);
    ops.add(BatchOp(
        Constants.opPut, normNs, keyBytes, Constants.valTypeBytes, copyVal));
    return this;
  }

  BatchWriter putString(String? ns, String key, String val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final valBytes = Uint8List.fromList(utf8.encode(val));
    validateLengths(keyBytes, valBytes);
    ops.add(BatchOp(
        Constants.opPut, normNs, keyBytes, Constants.valTypeString, valBytes));
    return this;
  }

  BatchWriter putInt64(String? ns, String key, int val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final valBytes = BinaryHelper.encodeInt64(val);
    validateLengths(keyBytes, valBytes);
    ops.add(BatchOp(
        Constants.opPut, normNs, keyBytes, Constants.valTypeInt64, valBytes));
    return this;
  }

  BatchWriter putFloat64(String? ns, String key, double val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final valBytes = BinaryHelper.encodeFloat64(val);
    validateLengths(keyBytes, valBytes);
    ops.add(BatchOp(
        Constants.opPut, normNs, keyBytes, Constants.valTypeFloat64, valBytes));
    return this;
  }

  BatchWriter putBool(String? ns, String key, bool val) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    final valBytes = BinaryHelper.encodeBool(val);
    validateLengths(keyBytes, valBytes);
    ops.add(BatchOp(
        Constants.opPut, normNs, keyBytes, Constants.valTypeBool, valBytes));
    return this;
  }

  BatchWriter delete(String? ns, String key) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));
    ops.add(BatchOp(Constants.opDelete, normNs, keyBytes,
        Constants.valTypeBytes, Uint8List(0)));
    return this;
  }

  BatchWriter dropNamespace(String? ns) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    ops.add(BatchOp(Constants.opDropNamespace, normNs, Uint8List(0),
        Constants.valTypeBytes, Uint8List(0)));
    return this;
  }

  BatchWriter clearNamespace(String? ns) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    ops.add(BatchOp(Constants.batchOpClearStaging, normNs, Uint8List(0),
        Constants.valTypeBytes, Uint8List(0)));
    return this;
  }

  /// Reads a key as this batch sees it: the most recent staged operation wins,
  /// a staged tombstone (delete / dropNamespace / clearNamespace) reports
  /// "absent" even when the database still holds the key, and only a key
  /// untouched by this batch falls through to the committed state.
  ///
  /// The fallback read takes the database read lock, so it observes writes
  /// committed by other code during the batch. Take a snapshot first if you need
  /// a frozen base. Staging is scanned back to front.
  Uint8List? getBytes(String? ns, String key) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));

    for (var i = ops.length - 1; i >= 0; i--) {
      final op = ops[i];
      if (op.namespace != normNs) continue;
      switch (op.opType) {
        case Constants.opPut:
          if (BinaryHelper.compareBytes(op.key, keyBytes) == 0) {
            return Uint8List.fromList(op.val);
          }
          break;
        case Constants.opDelete:
          if (BinaryHelper.compareBytes(op.key, keyBytes) == 0) return null;
          break;
        case Constants.opDropNamespace:
        case Constants.batchOpClearStaging:
          return null;
      }
    }

    return _readBytes?.call(normNs, key);
  }

  /// String flavour of [getBytes]. Mirrors `CkvDb.getString`: the value must have
  /// been written as a string ([TypeMismatchError] otherwise) and must be valid
  /// UTF-8 ([InvalidUtf8Error] otherwise).
  String? getString(String? ns, String key) {
    final normNs = BinaryHelper.normalizeNamespace(ns);
    final keyBytes = Uint8List.fromList(utf8.encode(key));

    for (var i = ops.length - 1; i >= 0; i--) {
      final op = ops[i];
      if (op.namespace != normNs) continue;
      switch (op.opType) {
        case Constants.opPut:
          if (BinaryHelper.compareBytes(op.key, keyBytes) == 0) {
            if (op.valType != Constants.valTypeString) {
              throw TypeMismatchError(
                "Key '$key' has value_type ${op.valType}, expected string (0x01)",
              );
            }
            try {
              return utf8.decode(op.val);
            } on FormatException {
              throw InvalidUtf8Error("Invalid UTF-8 sequence in key '$key'");
            }
          }
          break;
        case Constants.opDelete:
          if (BinaryHelper.compareBytes(op.key, keyBytes) == 0) return null;
          break;
        case Constants.opDropNamespace:
        case Constants.batchOpClearStaging:
          return null;
      }
    }

    return _readString?.call(normNs, key);
  }
}

/// 为只读快照上的特定命名空间提供免传 ns 参数的便利门面。
class SnapshotNamespaceScope {
  final Snapshot snapshot;
  final String namespace;

  SnapshotNamespaceScope(this.snapshot, this.namespace);

  String? operator [](String key) => getString(key);

  Uint8List? getBytes(String key) => snapshot.getBytes(namespace, key);
  String? getString(String key) => snapshot.getString(namespace, key);
  int? getInt64(String key) => snapshot.getInt64(namespace, key);
  double? getFloat64(String key) => snapshot.getFloat64(namespace, key);
  bool? getBool(String key) => snapshot.getBool(namespace, key);
  bool containsKey(String key) => snapshot.containsKey(namespace, key);

  int count() => snapshot.count(namespace);
  List<String> keys([String? prefix]) => snapshot.keys(namespace, prefix);
  List<Entry> iterPrefix([String? prefix]) =>
      snapshot.iterPrefix(namespace, prefix);
  List<Entry> scanRange([String? startKey, String? endKey]) =>
      snapshot.scanRange(namespace, startKey, endKey);
}

/// 为批次写入器上的特定命名空间提供免传 ns 参数的便利门面。
class BatchNamespaceScope {
  final BatchWriter batch;
  final String namespace;

  BatchNamespaceScope(this.batch, this.namespace);

  String? operator [](String key) => getString(key);
  void operator []=(String key, String? value) {
    if (value == null) {
      delete(key);
    } else {
      putString(key, value);
    }
  }

  void putBytes(String key, Uint8List value) =>
      batch.putBytes(namespace, key, value);
  void putString(String key, String value) =>
      batch.putString(namespace, key, value);
  void putInt64(String key, int value) => batch.putInt64(namespace, key, value);
  void putFloat64(String key, double value) =>
      batch.putFloat64(namespace, key, value);
  void putBool(String key, bool value) => batch.putBool(namespace, key, value);

  void delete(String key) => batch.delete(namespace, key);
  void clear() => batch.clearNamespace(namespace);
  void drop() => batch.dropNamespace(namespace);

  Uint8List? getBytes(String key) => batch.getBytes(namespace, key);
  String? getString(String key) => batch.getString(namespace, key);
}
