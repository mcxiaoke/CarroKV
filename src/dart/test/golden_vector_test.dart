import 'dart:io';
import 'dart:typed_data';
import 'package:crypto/crypto.dart';
import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  final testdataDir = p.canonicalize(p.join(Directory.current.path, '..', '..', 'testdata'));
  final tempBase = p.canonicalize(p.join(Directory.current.path, '..', '..', 'temp', 'dart_golden_test'));

  setUpAll(() {
    final d = Directory(tempBase);
    if (d.existsSync()) d.deleteSync(recursive: true);
    d.createSync(recursive: true);
  });

  tearDownAll(() {
    final d = Directory(tempBase);
    if (d.existsSync()) {
      try {
        d.deleteSync(recursive: true);
      } catch (_) {}
    }
  });

  String sha256Hex(List<int> bytes) => sha256.convert(bytes).toString();

  test('golden_empty_v3 vector compatibility & deterministic regeneration', () {
    final filePath = p.join(testdataDir, 'empty_v3.db');
    expect(File(filePath).existsSync(), isTrue);

    final data = File(filePath).readAsBytesSync();
    expect(data.length, equals(32));
    expect(sha256Hex(data), equals('06cbf30c0cd3b3c512bc42b8d221b7e51e675a269ebbc69a62007a20e02be328'));

    // 1) Read external vector
    final copyPath = p.join(tempBase, 'empty_copy.db');
    File(copyPath).writeAsBytesSync(data);
    final db = CkvDb.open(copyPath);
    expect(db.count(''), equals(0));
    expect(db.namespaces(), equals([]));
    expect(db.stats().totalEntries, equals(0));
    expect(db.stats().currentMaxTxId, equals(0));
    expect(db.fileSize(), equals(32));
    db.flush();
    db.close();

    // 2) Regenerate empty file locally
    final regenPath = p.join(tempBase, 'empty_regen.db');
    final dbRegen = CkvDb.open(regenPath);
    dbRegen.flush();
    dbRegen.close();
    expect(File(regenPath).readAsBytesSync(), equals(data));
  });

  test('golden_single_put_v3 vector compatibility & deterministic regeneration', () {
    final filePath = p.join(testdataDir, 'single_put_v3.db');
    expect(File(filePath).existsSync(), isTrue);

    final data = File(filePath).readAsBytesSync();
    expect(data.length, equals(80));
    expect(sha256Hex(data), equals('f4b9d73bece38e54376d3c412e1b5fa4353811d33ba33cbd073df54aecbb224a'));

    // 1) Read external vector
    final copyPath = p.join(tempBase, 'single_copy.db');
    File(copyPath).writeAsBytesSync(data);
    final db = CkvDb.open(copyPath);
    expect(db.getString('', 'hello'), equals('world'));
    expect(db.count(''), equals(1));
    expect(db.namespaces(), equals(['']));
    db.close();

    // 2) Regenerate locally
    final regenPath = p.join(tempBase, 'single_regen.db');
    final dbRegen = CkvDb.open(regenPath);
    dbRegen.putString('', 'hello', 'world');
    dbRegen.close();
    expect(File(regenPath).readAsBytesSync(), equals(data));
  });

  test('golden_multi_types_v3 vector compatibility & deterministic regeneration', () {
    final filePath = p.join(testdataDir, 'multi_types_v3.db');
    expect(File(filePath).existsSync(), isTrue);

    final data = File(filePath).readAsBytesSync();
    expect(data.length, equals(267));
    expect(sha256Hex(data), equals('f13029ba36d1d9701a6f5770b4624d6386dcd7432f8245057a4ada35f033a664'));

    // 1) Read external vector
    final copyPath = p.join(tempBase, 'multi_copy.db');
    File(copyPath).writeAsBytesSync(data);
    final db = CkvDb.open(copyPath);

    expect(db.getBytes('', 'bytes'), equals(Uint8List.fromList([0x00, 0x01, 0xFE, 0xFF])));
    expect(db.getString('', 'str'), equals('你好'));
    expect(db.getInt64('', 'i64'), equals(-1234567890123456789));
    expect(db.getFloat64('', 'f64'), equals(3.141592653589793));
    final nanVal = db.getFloat64('', 'f64nan');
    expect(nanVal, isNotNull);
    expect(nanVal!.isNaN, isTrue);
    expect(db.getBool('', 'bool_t'), isTrue);
    expect(db.getBool('', 'bool_f'), isFalse);
    expect(db.getBool('cfg', 'enabled'), isTrue);
    expect(db.getInt64('cfg', 'limit'), equals(1000));
    expect(db.getString('users', 'u1'), equals('alice'));
    expect(db.namespaces(), equals(['', 'cfg', 'users']));
    expect(db.stats().totalEntries, equals(10));
    db.close();

    // 2) Regenerate locally
    final regenPath = p.join(tempBase, 'multi_regen.db');
    final dbRegen = CkvDb.open(regenPath);
    dbRegen.batch((b) {
      b.putBytes('', 'bytes', Uint8List.fromList([0x00, 0x01, 0xFE, 0xFF]));
      b.putString('', 'str', '你好');
      b.putInt64('', 'i64', -1234567890123456789);
      b.putFloat64('', 'f64', 3.141592653589793);
      b.putFloat64('', 'f64nan', double.nan);
      b.putBool('', 'bool_t', true);
      b.putBool('', 'bool_f', false);
      b.putBool('cfg', 'enabled', true);
      b.putInt64('cfg', 'limit', 1000);
      b.putString('users', 'u1', 'alice');
    });
    dbRegen.close();
    expect(File(regenPath).readAsBytesSync(), equals(data));
  });

  test('golden_unicode_order_v3 vector compatibility & deterministic regeneration', () {
    final filePath = p.join(testdataDir, 'unicode_order_v3.db');
    expect(File(filePath).existsSync(), isTrue);

    final data = File(filePath).readAsBytesSync();
    expect(data.length, equals(159));
    expect(sha256Hex(data), equals('e3862d04be45b555bb902e271f702ecd44f67aec097008299228cc5594492e6a'));

    // 1) Read external vector
    final copyPath = p.join(tempBase, 'uni_copy.db');
    File(copyPath).writeAsBytesSync(data);
    final db = CkvDb.open(copyPath);

    expect(db.namespaces(), equals(['', 'cf', 'é', '中文']));
    expect(db.keys(''), equals(['a', 'z', '\uE000', '\u{1F600}']));
    expect(db.getString('é', 'k'), equals('5'));
    expect(db.getString('中文', 'k'), equals('6'));
    expect(db.getString('cf', 'k'), equals('7'));
    db.close();

    // 2) Regenerate locally
    final regenPath = p.join(tempBase, 'uni_regen.db');
    final dbRegen = CkvDb.open(regenPath);
    dbRegen.batch((b) {
      b.putString('', 'a', '1');
      b.putString('', 'z', '2');
      b.putString('', '\uE000', '3');
      b.putString('', '\u{1F600}', '4');
      b.putString('é', 'k', '5');
      b.putString('中文', 'k', '6');
      b.putString('cf', 'k', '7');
    });
    dbRegen.close();
    expect(File(regenPath).readAsBytesSync(), equals(data));
  });
}
