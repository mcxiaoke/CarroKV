import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:ckv/src/binary_helper.dart';
import 'package:ckv/src/crc32.dart';
import 'package:test/test.dart';

/// API-SPEC §3.3.3：非法 UTF-8 键在扫描接口里必须跳过，不得返回 U+FFFD 伪键。
/// 写 API 的 key 是 String，非法键只能来自损坏/外部构造的文件。
void main() {
  late Directory tempDir;

  setUp(() => tempDir = Directory.systemTemp.createTempSync('ckv_utf8_'));
  tearDown(() {
    try {
      tempDir.deleteSync(recursive: true);
    } catch (_) {}
  });

  test('scans skip keys that are not valid utf8', () {
    final dbPath = '${tempDir.path}/bad_key.db';
    final db = CkvDb.open(dbPath);
    db.putString('', 'k', 'v');
    db.close();

    final b = File(dbPath).readAsBytesSync();
    b[70] = 0xFF; // 32B 头 + 28B 帧头 + 10B 条目定长头；ns 空、key 长 1
    BinaryHelper.writeUint32LE(b, 56, Crc32.compute(b, 60, b.length - 60));
    BinaryHelper.writeUint32LE(b, 36, Crc32.compute(b, 40, 20));
    File(dbPath).writeAsBytesSync(b);

    final reopened = CkvDb.open(dbPath);
    expect(reopened.keys(''), isEmpty,
        reason: 'keys() must not surface an invalid UTF-8 key');
    expect(reopened.iterPrefix(''), isEmpty,
        reason: 'iterPrefix() must skip it instead of returning U+FFFD');
    // §3.3.3 只约束 keys / iterPrefix；scanRange 按字节序枚举，v1.0 冻结语义是
    // 条目仍在列表里（见 api_v1_freeze_test），这里只锁住条数。
    expect(reopened.scanRange(''), hasLength(1),
        reason: 'byte-order scan keeps the entry');
    expect(reopened.count(''), 1,
        reason: 'the entry itself is still stored; only decoding is skipped');

    // 快照是另一套实现，同一份数据必须给出同一结论。
    final snap = reopened.snapshot();
    expect(snap.keys(''), isEmpty);
    expect(snap.iterPrefix(''), isEmpty);
    expect(snap.scanRange(''), hasLength(1));
    snap.dispose();

    reopened.close();
  });
}
