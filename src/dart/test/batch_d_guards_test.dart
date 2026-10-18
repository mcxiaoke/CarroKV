import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:ckv/src/binary_helper.dart';
import 'package:ckv/src/crc32.dart';
import 'package:test/test.dart';

/// 批次 D 回归（Dart）：
///  - M3-11 `backup` 不得写到源库自身或其 .tmp/.bak
///  - M1-04 未知 OpCode 必须拒绝打开（旧实现静默丢弃该条目）
void main() {
  group('backup destination guard (M3-11)', () {
    late Directory tempDir;

    setUp(() => tempDir = Directory.systemTemp.createTempSync('ckv_bk_'));
    tearDown(() {
      try {
        tempDir.deleteSync(recursive: true);
      } catch (_) {}
    });

    test('rejects the database file and its temp siblings', () {
      final dbPath = '${tempDir.path}/self.db';
      final db = CkvDb.open(dbPath);
      db.putString('', 'k', 'v');

      expect(() => db.backup(dbPath), throwsA(isA<IoError>()));
      expect(() => db.backup('$dbPath.tmp'), throwsA(isA<IoError>()));
      expect(() => db.backup('$dbPath.bak'), throwsA(isA<IoError>()));
      db.close();
    });
  });

  group('unknown op code (M1-04)', () {
    late Directory tempDir;

    setUp(() => tempDir = Directory.systemTemp.createTempSync('ckv_op_'));
    tearDown(() {
      try {
        tempDir.deleteSync(recursive: true);
      } catch (_) {}
    });

    test('a frame carrying an unknown op code is rejected', () {
      final dbPath = '${tempDir.path}/op.db';
      final db = CkvDb.open(dbPath);
      db.putString('', 'k', 'v');
      db.close();

      final bytes = File(dbPath).readAsBytesSync();
      // 布局：32B 文件头 + 28B 帧头 + 10B 条目定长头 → 第 60 字节是 opCode。
      bytes[60] = 0x7F;
      // 重算 payload CRC（帧头 24..28）与帧头 CRC（帧头 4..8，覆盖 8..28）。
      BinaryHelper.writeUint32LE(
          bytes, 32 + 24, Crc32.compute(bytes, 60, bytes.length - 60));
      BinaryHelper.writeUint32LE(bytes, 32 + 4, Crc32.compute(bytes, 40, 20));
      File(dbPath).writeAsBytesSync(bytes);

      expect(() => CkvDb.open(dbPath), throwsA(isA<CorruptedError>()));
    });
  });
}
