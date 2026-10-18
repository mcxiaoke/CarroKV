import 'dart:convert';
import 'dart:typed_data';
import 'binary_helper.dart';
import 'constants.dart';
import 'errors.dart';

class Entry {
  final String namespace;
  final String key;
  final int valueType;
  final Uint8List value;

  Entry(this.namespace, this.key, this.valueType, this.value);

  String asString() {
    if (valueType != Constants.valTypeString) {
      throw TypeMismatchError(
        "Expected string type (${Constants.valTypeString}), got $valueType for key '$key'",
      );
    }
    try {
      return utf8.decode(value);
    } catch (e) {
      throw InvalidUtf8Error("Invalid UTF-8 sequence in key '$key'");
    }
  }

  int asInt64() {
    if (valueType != Constants.valTypeInt64) {
      throw TypeMismatchError(
        "Expected int64 type (${Constants.valTypeInt64}), got $valueType for key '$key'",
      );
    }
    return BinaryHelper.decodeInt64(value);
  }

  double asFloat64() {
    if (valueType != Constants.valTypeFloat64) {
      throw TypeMismatchError(
        "Expected float64 type (${Constants.valTypeFloat64}), got $valueType for key '$key'",
      );
    }
    return BinaryHelper.decodeFloat64(value);
  }

  bool asBool() {
    if (valueType != Constants.valTypeBool) {
      throw TypeMismatchError(
        "Expected bool type (${Constants.valTypeBool}), got $valueType for key '$key'",
      );
    }
    return BinaryHelper.decodeBool(value);
  }

  Uint8List asBytes() => Uint8List.fromList(value);

  String? tryAsString() {
    if (valueType != Constants.valTypeString) return null;
    try {
      return utf8.decode(value);
    } catch (_) {
      return null;
    }
  }

  int? tryAsInt64() {
    if (valueType != Constants.valTypeInt64) return null;
    try {
      return BinaryHelper.decodeInt64(value);
    } catch (_) {
      return null;
    }
  }

  double? tryAsFloat64() {
    if (valueType != Constants.valTypeFloat64) return null;
    try {
      return BinaryHelper.decodeFloat64(value);
    } catch (_) {
      return null;
    }
  }

  bool? tryAsBool() {
    if (valueType != Constants.valTypeBool) return null;
    try {
      return BinaryHelper.decodeBool(value);
    } catch (_) {
      return null;
    }
  }

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
