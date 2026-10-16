import 'dart:typed_data';

class Entry {
  final String namespace;
  final String key;
  final int valueType;
  final Uint8List value;

  Entry(this.namespace, this.key, this.valueType, this.value);

  @override
  String toString() =>
      'Entry(ns: "$namespace", key: "$key", type: $valueType, len: ${value.length})';
}

enum KeyChangeOp {
  put,
  delete,
  dropNamespace,
  clear,
}

class KeyChangeEvent {
  final KeyChangeOp op;
  final String namespace;
  final String key;
  final int valueType;

  KeyChangeEvent(this.op, this.namespace, this.key, [this.valueType = 0]);

  @override
  String toString() =>
      'KeyChangeEvent($op, ns: "$namespace", key: "$key", type: $valueType)';
}

typedef KeyChangeCallback = void Function(KeyChangeEvent event);

abstract class WatcherHandle {
  void unwatch();
  void dispose();
}

class DbStats {
  final int fileSize;
  final int namespaceCount;
  final int totalEntries;
  final int currentMaxTxId;

  DbStats({
    required this.fileSize,
    required this.namespaceCount,
    required this.totalEntries,
    required this.currentMaxTxId,
  });

  @override
  String toString() =>
      'DbStats(fileSize: $fileSize, namespaces: $namespaceCount, entries: $totalEntries, maxTxId: $currentMaxTxId)';
}
