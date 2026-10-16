import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'binary_helper.dart';
import 'constants.dart';
import 'crc32.dart';
import 'errors.dart';
import 'options.dart';
import 'storage.dart';

class FileHeader {
  static Uint8List createBytes() {
    final bytes = Uint8List(Constants.fileHeaderLen);
    bytes.setRange(0, 8, Constants.fileMagic);
    BinaryHelper.writeUint16LE(bytes, 8, Constants.formatReadVer);
    BinaryHelper.writeUint16LE(bytes, 10, Constants.formatWriteVer);
    BinaryHelper.writeUint32LE(bytes, 12, 0); // flags
    // 16..28 reserved 0x00
    final crc = Crc32.compute(bytes, 0, 28);
    BinaryHelper.writeUint32LE(bytes, 28, crc);
    return bytes;
  }

  static void validateDatabaseBytes(Uint8List bytes) {
    if (bytes.length < Constants.fileHeaderLen) {
      throw CorruptedError(
        'File size ${bytes.length} is less than header length 32',
      );
    }
    for (int i = 0; i < 8; i++) {
      if (bytes[i] != Constants.fileMagic[i]) {
        throw CorruptedError('Invalid file magic');
      }
    }
    final readVer = BinaryHelper.readUint16LE(bytes, 8);
    if (readVer > Constants.formatReadVer) {
      throw UnsupportedVersionError(
        'Unsupported format read version $readVer (expected <= ${Constants.formatReadVer})',
      );
    }
    final storedCrc = BinaryHelper.readUint32LE(bytes, 28);
    final calcCrc = Crc32.compute(bytes, 0, 28);
    if (storedCrc != calcCrc) {
      throw CorruptedError(
        'Header CRC mismatch: stored 0x${storedCrc.toRadixString(16)}, calculated 0x${calcCrc.toRadixString(16)}',
      );
    }
  }
}

class RawOpEntry {
  final int opCode;
  final int valueType;
  final Uint8List namespace;
  final Uint8List key;
  final Uint8List value;

  RawOpEntry({
    required this.opCode,
    required this.valueType,
    required this.namespace,
    required this.key,
    required this.value,
  });

  int encodedLen() =>
      Constants.entryFixedHeaderLen +
      namespace.length +
      key.length +
      value.length;

  void encodeTo(BytesBuilder bb) {
    final hdr = Uint8List(Constants.entryFixedHeaderLen);
    hdr[0] = opCode;
    hdr[1] = valueType;
    BinaryHelper.writeUint16LE(hdr, 2, namespace.length);
    BinaryHelper.writeUint16LE(hdr, 4, key.length);
    BinaryHelper.writeUint32LE(hdr, 6, value.length);

    bb.add(hdr);
    if (namespace.isNotEmpty) bb.add(namespace);
    bb.add(key);
    bb.add(value);
  }
}

class BatchFrameWriter {
  static void writeFrame(
    RandomAccessFile raf,
    int txId,
    List<RawOpEntry> entries,
  ) {
    final payloadBb = BytesBuilder(copy: false);
    for (final e in entries) {
      e.encodeTo(payloadBb);
    }
    final payloadBytes = payloadBb.takeBytes();
    final payloadLen = payloadBytes.length;
    final payloadCrc = Crc32.compute(payloadBytes);

    final frameHeader = Uint8List(Constants.frameHeaderLen);
    BinaryHelper.writeUint32LE(frameHeader, 0, Constants.frameMagic);

    final tail20 = Uint8List(20);
    BinaryHelper.writeUint64LE(tail20, 0, txId);
    BinaryHelper.writeUint32LE(tail20, 8, entries.length);
    BinaryHelper.writeUint32LE(tail20, 12, payloadLen);
    BinaryHelper.writeUint32LE(tail20, 16, payloadCrc);

    final hdrCrc = Crc32.compute(tail20);
    BinaryHelper.writeUint32LE(frameHeader, 4, hdrCrc);
    frameHeader.setRange(8, 28, tail20);

    raf.writeFromSync(frameHeader);
    raf.writeFromSync(payloadBytes);
  }
}

class ParsedResult {
  final Map<String, NamespaceStore> namespaces;
  final int maxTxId;
  final int effectiveLength;

  ParsedResult(this.namespaces, this.maxTxId, this.effectiveLength);
}

class _LoadEntry {
  final int seq;
  final int opType;
  final int valType;
  final String ns;
  final Uint8List key;
  final Uint8List val;

  _LoadEntry(this.seq, this.opType, this.valType, this.ns, this.key, this.val);
}

class FrameLoader {
  static int scanMaxTxId(RandomAccessFile raf, int fileSize) {
    if (fileSize <= Constants.fileHeaderLen) return 0;
    raf.setPositionSync(Constants.fileHeaderLen);
    int offset = Constants.fileHeaderLen;
    int lastTxId = 0;
    final hdr = Uint8List(Constants.frameHeaderLen);

    while (offset < fileSize) {
      if (fileSize - offset < Constants.frameHeaderLen) {
        throw CorruptedError('Truncated frame header in fast scan');
      }
      raf.readIntoSync(hdr);
      final magic = BinaryHelper.readUint32LE(hdr, 0);
      if (magic != Constants.frameMagic) {
        throw CorruptedError('Invalid frame magic in fast scan');
      }
      final txId = BinaryHelper.readUint64LE(hdr, 8);
      final payloadLen = BinaryHelper.readUint32LE(hdr, 20);
      lastTxId = txId;
      offset += Constants.frameHeaderLen + payloadLen;
      raf.setPositionSync(offset);
    }
    return lastTxId;
  }

  static ParsedResult loadAllWithRecovery(
    Uint8List bytes, {
    bool allowTailTruncation = false,
    WarningCallback? onWarning,
  }) {
    FileHeader.validateDatabaseBytes(bytes);

    final parsed = <_LoadEntry>[];
    int maxTxId = 0;
    int lastTxId = 0;
    int offset = Constants.fileHeaderLen;
    bool allPuts = true;
    int seq = 0;

    while (offset < bytes.length) {
      if (offset + Constants.frameHeaderLen > bytes.length) {
        if (allowTailTruncation) {
          onWarning?.call(
            'onTailTruncated',
            'Truncated partial frame header at tail (offset $offset)',
          );
          break;
        }
        throw CorruptedError('Incomplete frame header at offset $offset');
      }

      final frameMagic = BinaryHelper.readUint32LE(bytes, offset);
      final storedHdrCrc = BinaryHelper.readUint32LE(bytes, offset + 4);
      final calcHdrCrc = Crc32.compute(bytes, offset + 8, 20);
      final txId = BinaryHelper.readUint64LE(bytes, offset + 8);
      final opCount = BinaryHelper.readUint32LE(bytes, offset + 16);
      final payloadLen = BinaryHelper.readUint32LE(bytes, offset + 20);
      final storedPayloadCrc = BinaryHelper.readUint32LE(bytes, offset + 24);

      if (frameMagic != Constants.frameMagic ||
          storedHdrCrc != calcHdrCrc ||
          txId <= lastTxId ||
          opCount == 0 ||
          opCount > Constants.maxOpsPerFrame ||
          payloadLen > Constants.maxPayloadLen) {
        if (allowTailTruncation) {
          onWarning?.call(
            'onTailTruncated',
            'Truncated invalid frame header at tail (offset $offset)',
          );
          break;
        }
        if (frameMagic != Constants.frameMagic) {
          throw CorruptedError(
            'Invalid frame magic 0x${frameMagic.toRadixString(16)} at offset $offset',
          );
        }
        if (storedHdrCrc != calcHdrCrc) {
          throw CorruptedError('Frame header CRC mismatch at offset $offset');
        }
        if (txId <= lastTxId) {
          throw CorruptedError(
            'Non-monotonic TxID $txId (last was $lastTxId) at offset $offset',
          );
        }
        if (opCount == 0 || opCount > Constants.maxOpsPerFrame) {
          throw CorruptedError('Invalid OpCount $opCount at offset $offset');
        }
        throw CorruptedError(
          'Payload length $payloadLen exceeds limit ${Constants.maxPayloadLen} at offset $offset',
        );
      }

      final payloadOffset = offset + Constants.frameHeaderLen;
      if (payloadOffset + payloadLen > bytes.length) {
        if (allowTailTruncation) {
          onWarning?.call(
            'onTailTruncated',
            'Truncated incomplete payload at tail (offset $payloadOffset)',
          );
          break;
        }
        throw CorruptedError(
          'Incomplete frame payload at offset $payloadOffset',
        );
      }

      final calcPayloadCrc = Crc32.compute(bytes, payloadOffset, payloadLen);
      if (storedPayloadCrc != calcPayloadCrc) {
        if (allowTailTruncation) {
          onWarning?.call(
            'onTailTruncated',
            'Truncated payload CRC mismatch at tail (offset $payloadOffset)',
          );
          break;
        }
        throw CorruptedError(
          'Frame payload CRC mismatch at offset $payloadOffset',
        );
      }

      // Parse payload entries
      int pIdx = payloadOffset;
      final pEnd = payloadOffset + payloadLen;
      bool decodeErr = false;
      final frameEntries = <_LoadEntry>[];

      for (int op = 0; op < opCount; op++) {
        if (pIdx + Constants.entryFixedHeaderLen > pEnd) {
          decodeErr = true;
          break;
        }

        final opType = bytes[pIdx];
        final valType = bytes[pIdx + 1];
        final nsLen = BinaryHelper.readUint16LE(bytes, pIdx + 2);
        final keyLen = BinaryHelper.readUint16LE(bytes, pIdx + 4);
        final valLen = BinaryHelper.readUint32LE(bytes, pIdx + 6);
        pIdx += Constants.entryFixedHeaderLen;

        if (pIdx + nsLen + keyLen + valLen > pEnd) {
          decodeErr = true;
          break;
        }

        if (keyLen > Constants.maxKeyLen) {
          throw KeyTooLargeError('Key length $keyLen exceeds maximum 1024');
        }
        if (valLen > Constants.maxValLen) {
          throw KeyTooLargeError('Value length $valLen exceeds maximum 16MB');
        }

        final ns = nsLen == 0
            ? ''
            : utf8.decode(bytes.sublist(pIdx, pIdx + nsLen));
        pIdx += nsLen;

        final keyBytes = Uint8List.fromList(bytes.sublist(pIdx, pIdx + keyLen));
        pIdx += keyLen;

        final valBytes = Uint8List.fromList(bytes.sublist(pIdx, pIdx + valLen));
        pIdx += valLen;

        if (opType != Constants.opPut) {
          allPuts = false;
        }
        frameEntries.add(_LoadEntry(seq++, opType, valType, ns, keyBytes, valBytes));
      }

      if (decodeErr || pIdx != pEnd) {
        if (allowTailTruncation) {
          onWarning?.call(
            'onTailTruncated',
            'Truncated decoding error in entries at offset $offset',
          );
          break;
        }
        throw CorruptedError('Malformed entry payload at offset $offset');
      }

      parsed.addAll(frameEntries);
      lastTxId = txId;
      maxTxId = txId;
      offset += Constants.frameHeaderLen + payloadLen;
    }

    // Replay into namespace stores
    final namespaces = <String, NamespaceStore>{};

    if (allPuts) {
      // Fast path: group by namespace, stable sort, overwrite same keys
      final groups = <String, List<_LoadEntry>>{};
      for (final e in parsed) {
        groups.putIfAbsent(e.ns, () => []).add(e);
      }

      for (final entry in groups.entries) {
        final list = entry.value;
        list.sort((a, b) {
          final c = BinaryHelper.compareBytes(a.key, b.key);
          return c != 0 ? c : a.seq.compareTo(b.seq);
        });

        final store = NamespaceStore();
        for (int i = 0; i < list.length; i++) {
          if (i > 0 &&
              BinaryHelper.compareBytes(list[i].key, list[i - 1].key) == 0) {
            store.items[store.items.length - 1] =
                KeyItem(list[i].key, list[i].valType, list[i].val);
          } else {
            store.items.add(KeyItem(list[i].key, list[i].valType, list[i].val));
          }
        }
        namespaces[entry.key] = store;
      }
    } else {
      // Slow path: sequential replay
      for (final e in parsed) {
        switch (e.opType) {
          case Constants.opPut:
            final store = namespaces.putIfAbsent(e.ns, () => NamespaceStore());
            store.put(e.key, e.valType, e.val);
            break;
          case Constants.opDelete:
            final store = namespaces[e.ns];
            store?.delete(e.key);
            break;
          case Constants.opDropNamespace:
            namespaces.remove(e.ns);
            break;
        }
      }
    }

    return ParsedResult(namespaces, maxTxId, offset);
  }
}
