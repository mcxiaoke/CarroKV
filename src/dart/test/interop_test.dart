import 'dart:io';
import 'dart:typed_data';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// 跨语言夹具（Dart 侧）：写出 `temp/interop/dart_data.db` 供 Rust / .NET / Go 读取，
/// 并在其他语言的夹具存在时反向读取。
///
/// 约定：所有语言写同一组键，`hello` 的值带语言名，二进制键用固定字节，类型键覆盖
/// i64 边界、NaN 与布尔。
void main() {
  final envInterop = Platform.environment['CKV_INTEROP_DIR'];
  final interopDir = (envInterop != null && envInterop.isNotEmpty)
      ? envInterop
      : p.join(Directory.systemTemp.path, 'ckv_interop');

  File fixture(String name) => File(p.join(interopDir, name));

  void cleanFixture(String name) {
    for (final suffix in ['', '.bak', '.tmp', '.lock']) {
      final f = File(p.join(interopDir, '$name$suffix'));
      if (f.existsSync()) f.deleteSync();
    }
  }

  test('writes dart fixture and reads the others when present', () {
    Directory(interopDir).createSync(recursive: true);
    cleanFixture('dart_data.db');

    final db = CkvDb.open(fixture('dart_data.db').path);
    try {
      db.putString('', 'hello', 'world from dart');
      db.putBytes('bin_ns', 'binary_key', Uint8List.fromList([0xDE, 0xAD, 0xBE, 0xEF]));
      db.putString('user_ns', 'name', 'Dave');
      db.putString('user_ns', 'city', 'Shenzhen');
      db.putString('', '😀', 'happy');

      db.putInt64('types', 'int_min', -9223372036854775808);
      db.putInt64('types', 'int_max', 9223372036854775807);
      db.putFloat64('types', 'float_pi', 3.141592653589793);
      db.putFloat64('types', 'float_nan', double.nan);
      db.putBool('types', 'bool_true', true);
      db.putBool('types', 'bool_false', false);
    } finally {
      db.close();
    }

    final rust = fixture('rust_data.db');
    if (rust.existsSync()) {
      final r = CkvDb.open(rust.path);
      try {
        expect(r.getString('', 'hello'), 'world from rust');
        expect(r.getString('user_ns', 'name'), 'Alice');
        expect(r.getString('user_ns', 'city'), 'Beijing');
        expect(r.getBytes('bin_ns', 'binary_key'), [0x01, 0x02, 0x03, 0xFF]);
        expect(r.getInt64('types', 'int_min'), -9223372036854775808);
        expect(r.getInt64('types', 'int_max'), 9223372036854775807);
        expect(r.getBool('types', 'bool_true'), isTrue);
        expect(r.getBool('types', 'bool_false'), isFalse);
      } finally {
        r.close();
      }
    }

    final kotlin = fixture('kotlin_data.db');
    if (kotlin.existsSync()) {
      final k = CkvDb.open(kotlin.path);
      try {
        expect(k.getString('', 'hello'), 'world from kotlin');
        expect(k.getString('user_ns', 'name'), 'Carol');
      } finally {
        k.close();
      }
    }
  });
}
