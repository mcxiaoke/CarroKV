import 'dart:io';

import 'package:ckv/ckv.dart';
// 内部工具：仅在测试里用于重算头部 CRC（不对外导出）。
import 'package:ckv/src/binary_helper.dart';
import 'package:ckv/src/crc32.dart';
import 'package:test/test.dart';

/// M1-01：非零 HeaderFlags 必须拒绝打开（规范 KV-DB-DESIGN-SPEC:79），
/// 并且 D2 裁决的 Snapshot 释放后温和语义必须落地（API-SPEC §3.5.3）。
void main() {
  group('header flags (M1-01)', () {
    late Directory tempDir;

    setUp(() {
      tempDir = Directory.systemTemp.createTempSync('ckv_flags_');
    });

    tearDown(() {
      try {
        tempDir.deleteSync(recursive: true);
      } catch (_) {}
    });

    test('non-zero flags are rejected with UnsupportedVersionError', () {
      final dbPath = '${tempDir.path}/flags.db';
      final db = CkvDb.open(dbPath);
      db.putString('', 'k', 'v');
      db.close();

      final bytes = File(dbPath).readAsBytesSync();
      bytes[12] = 1;
      // 重算头部 CRC（模拟写入方有意设置未知必选位）；不重算会被判成 Corrupted。
      BinaryHelper.writeUint32LE(bytes, 28, Crc32.compute(bytes, 0, 28));
      File(dbPath).writeAsBytesSync(bytes);

      expect(() => CkvDb.open(dbPath), throwsA(isA<UnsupportedVersionError>()));
    });

    test('zero flags still open', () {
      final dbPath = '${tempDir.path}/ok.db';
      final db = CkvDb.open(dbPath);
      db.putString('', 'k', 'v');
      db.close();

      final reopened = CkvDb.open(dbPath);
      expect(reopened.getString('', 'k'), 'v');
      reopened.close();
    });
  });

  group('snapshot after dispose is lenient (D2)', () {
    late Directory tempDir;

    setUp(() {
      tempDir = Directory.systemTemp.createTempSync('ckv_snap_');
    });

    tearDown(() {
      try {
        tempDir.deleteSync(recursive: true);
      } catch (_) {}
    });

    test('reads return empty instead of throwing', () {
      final dbPath = '${tempDir.path}/snap.db';
      final db = CkvDb.open(dbPath);
      db.putString('', 'k', 'v');

      final snap = db.snapshot();
      expect(snap.getString('', 'k'), 'v');
      snap.dispose();

      // API-SPEC §3.5.3：不得抛崩溃性异常，一律温和返回空/不存在。
      expect(snap.getString('', 'k'), isNull);
      expect(snap.getBytes('', 'k'), isNull);
      expect(snap.containsKey('', 'k'), isFalse);
      expect(snap.count(''), 0);
      expect(snap.keys(''), isEmpty);
      expect(snap.iterPrefix(''), isEmpty);
      expect(snap.scanRange(''), isEmpty);
      db.close();
    });
  });
}
