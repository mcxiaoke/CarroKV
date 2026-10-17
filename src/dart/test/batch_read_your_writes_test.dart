import 'dart:io';
import 'dart:typed_data';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// Read-your-own-writes inside `batch`, mirroring the Rust / Go / .NET / Kotlin
/// suites: staged writes win, staged tombstones hide committed values, and only
/// untouched keys fall through to the committed state.
void main() {
  final tempBase = p.canonicalize(
    p.join(Directory.current.path, '..', '..', 'temp', 'dart_ryow'),
  );
  var seq = 0;

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

  String tempPath(String name) => p.join(tempBase, '${seq++}_$name');

  test('staged writes win and tombstones hide', () {
    final db = CkvDb.open(tempPath('ryow.db'), DbOptions());
    try {
      db.putString('', 'a', 'committed');
      db.putString('', 'b', 'committed-b');

      db.batch((b) {
        expect(b.getString('', 'a'), 'committed');
        expect(b.getString('', 'b'), 'committed-b');

        b.putString('', 'a', 'staged');
        expect(b.getString('', 'a'), 'staged');

        b.putString('', 'a', 'staged-2');
        expect(b.getString('', 'a'), 'staged-2');

        b.delete('', 'b');
        expect(b.getString('', 'b'), isNull);
        expect(b.getBytes('', 'b'), isNull);

        b.putInt64('', 'n', 42);
        expect(b.getBytes('', 'n')!.length, 8);
        expect(() => b.getString('', 'n'), throwsA(isA<TypeMismatchError>()));
      });

      expect(db.getString('', 'a'), 'staged-2');
      expect(db.getString('', 'b'), isNull);
      expect(db.getInt64('', 'n'), 42);
    } finally {
      db.close();
    }
  });

  test('clear and drop hide staged writes', () {
    final db = CkvDb.open(tempPath('ryow_ns.db'), DbOptions());
    try {
      db.putString('users', 'u1', 'alice');

      db.batch((b) {
        b.putString('users', 'u2', 'bob');
        expect(b.getString('users', 'u2'), 'bob');

        b.clearNamespace('users');
        expect(b.getString('users', 'u1'), isNull);
        expect(b.getString('users', 'u2'), isNull);

        b.putString('users', 'u3', 'carol');
        expect(b.getString('users', 'u3'), 'carol');

        b.putString('other', 'k', 'v');
        expect(b.getString('other', 'k'), 'v');

        b.dropNamespace('users');
        expect(b.getString('users', 'u3'), isNull);
      });

      expect(db.count('users'), 0);
      expect(db.getString('other', 'k'), 'v');
    } finally {
      db.close();
    }
  });

  test('read your own writes in WAL mode', () {
    final opts = DbOptions(engineMode: StorageEngineMode.appendOnlyLog);
    final db = CkvDb.open(tempPath('ryow_wal.db'), opts);
    try {
      db.putString('', 'counter', '1');
      db.batch((b) {
        expect(b.getString('', 'counter'), '1');
        b.putString('', 'counter', '2');
        expect(b.getString('', 'counter'), '2');
      });
      expect(db.getString('', 'counter'), '2');
    } finally {
      db.close();
    }
  });

  test('bytes view reads staged binary value', () {
    final db = CkvDb.open(tempPath('ryow_bytes.db'), DbOptions());
    try {
      final payload = Uint8List.fromList([0, 1, 2, 0x7F]);
      db.batch((b) {
        b.putBytes('', 'bin', payload);
        expect(b.getBytes('', 'bin'), payload);
      });
      expect(db.getBytes('', 'bin'), payload);
    } finally {
      db.close();
    }
  });
}
