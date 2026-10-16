import 'dart:convert';
import 'package:ckv/src/crc32.dart';
import 'package:test/test.dart';

void main() {
  test('Standard IEEE 802.3 CRC32 vector', () {
    final bytes = utf8.encode('123456789');
    final crc = Crc32.compute(bytes);
    expect(crc, equals(0xCBF43926));
  });
}
