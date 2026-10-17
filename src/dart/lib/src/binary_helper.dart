import 'dart:convert';
import 'dart:typed_data';

import 'constants.dart';
import 'errors.dart';

class BinaryHelper {
  static int readUint16LE(List<int> bytes, int offset) {
    return (bytes[offset] & 0xFF) | ((bytes[offset + 1] & 0xFF) << 8);
  }

  static void writeUint16LE(List<int> bytes, int offset, int value) {
    bytes[offset] = value & 0xFF;
    bytes[offset + 1] = (value >> 8) & 0xFF;
  }

  static int readUint32LE(List<int> bytes, int offset) {
    return ((bytes[offset] & 0xFF) |
            ((bytes[offset + 1] & 0xFF) << 8) |
            ((bytes[offset + 2] & 0xFF) << 16) |
            ((bytes[offset + 3] & 0xFF) << 24)) &
        0xFFFFFFFF;
  }

  static void writeUint32LE(List<int> bytes, int offset, int value) {
    bytes[offset] = value & 0xFF;
    bytes[offset + 1] = (value >> 8) & 0xFF;
    bytes[offset + 2] = (value >> 16) & 0xFF;
    bytes[offset + 3] = (value >> 24) & 0xFF;
  }

  static int readUint64LE(List<int> bytes, int offset) {
    final bd = ByteData.sublistView(
      bytes is Uint8List ? bytes : Uint8List.fromList(bytes),
      offset,
      offset + 8,
    );
    return bd.getUint64(0, Endian.little);
  }

  static void writeUint64LE(List<int> bytes, int offset, int value) {
    final bd = ByteData(8);
    bd.setUint64(0, value, Endian.little);
    for (int i = 0; i < 8; i++) {
      bytes[offset + i] = bd.getUint8(i);
    }
  }

  static int decodeInt64(Uint8List bytes) {
    if (bytes.length != 8) {
      throw TypeMismatchError(
        'Expected 8 bytes for int64, got ${bytes.length}',
      );
    }
    final bd = ByteData.sublistView(bytes);
    return bd.getInt64(0, Endian.little);
  }

  static Uint8List encodeInt64(int value) {
    final bd = ByteData(8);
    bd.setInt64(0, value, Endian.little);
    return bd.buffer.asUint8List();
  }

  static double decodeFloat64(Uint8List bytes) {
    if (bytes.length != 8) {
      throw TypeMismatchError(
        'Expected 8 bytes for float64, got ${bytes.length}',
      );
    }
    final bd = ByteData.sublistView(bytes);
    return bd.getFloat64(0, Endian.little);
  }

  static Uint8List encodeFloat64(double value) {
    final bd = ByteData(8);
    if (value.isNaN) {
      bd.setUint64(0, 0x7FF8000000000000, Endian.little);
    } else {
      bd.setFloat64(0, value, Endian.little);
    }
    return bd.buffer.asUint8List();
  }

  static bool decodeBool(Uint8List bytes) {
    if (bytes.length != 1) {
      throw TypeMismatchError(
        'Expected 1 byte for bool, got ${bytes.length}',
      );
    }
    final b = bytes[0];
    if (b == 0) return false;
    if (b == 1) return true;
    throw TypeMismatchError('Invalid boolean byte value 0x${b.toRadixString(16)}');
  }

  static Uint8List encodeBool(bool value) {
    return Uint8List.fromList([value ? 1 : 0]);
  }

  /// Unsigned byte order comparison
  static int compareBytes(List<int> a, List<int> b) {
    final minLen = a.length < b.length ? a.length : b.length;
    for (int i = 0; i < minLen; i++) {
      final diff = (a[i] & 0xFF) - (b[i] & 0xFF);
      if (diff != 0) return diff;
    }
    return a.length - b.length;
  }

  static bool startsWith(List<int> source, List<int> prefix) {
    if (source.length < prefix.length) return false;
    for (int i = 0; i < prefix.length; i++) {
      if (source[i] != prefix[i]) return false;
    }
    return true;
  }

  static bool isValidUtf8(List<int> bytes) {
    try {
      utf8.decode(bytes);
      return true;
    } catch (_) {
      return false;
    }
  }

  /// Normalizes a namespace string per the spec: `null`, `""` and the reserved
  /// alias `"default"` all normalize to the empty (default) namespace.
  ///
  /// The 255-byte upper bound is enforced here, on **every** namespace entry
  /// point, because the on-disk `NsLen` header field is a uint16: a longer
  /// value is silently truncated by [writeUint16LE], the declared payload
  /// length then disagrees with the bytes actually written, and the single
  /// write makes the whole database undecodable (`CorruptedError` on reopen,
  /// with no `.bak` to fall back to). The limit is spec §3.2.2 hard-locked.
  ///
  /// The check uses the UTF-8 **byte** length, matching what the header
  /// records, not the Dart string length (which counts UTF-16 code units).
  static String normalizeNamespace(String? ns) {
    if (ns == null || ns.isEmpty) return '';
    if (ns == Constants.defaultNamespaceAlias) return '';

    final byteLen = utf8.encode(ns).length;
    if (byteLen > Constants.maxNsLen) {
      throw InvalidNamespaceError(
        'Namespace byte length $byteLen exceeds maximum allowed ${Constants.maxNsLen}',
      );
    }
    return ns;
  }
}
