import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:ckv/src/db.dart' show replaceFileAtomicallyWithRetry;
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// Regression tests for the snapshot rotation's atomicity (`C5`).
///
/// Rotating the live database onto `.bak` renames over the destination, which
/// already replaces an existing destination on both POSIX and Windows. Removing the
/// destination first is therefore both unnecessary and harmful: it destroys the
/// previous generation *before* the rename is known to succeed, and a failed
/// rotation then leaves the directory with no usable generation at all (the old
/// fallback swallowed the error as well, so the write reported success).
///
/// The primitive's own invariant is asserted directly; a rotation of a live
/// database cannot be made to fail through the public API.
void main() {
  final tempBase = p.canonicalize(
    p.join(Directory.current.path, '..', '..', 'temp', 'dart_rotation_atomicity'),
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

  test('a failed rotation must not destroy the destination', () {
    final src = tempPath('absent.tmp'); // deliberately missing: rename must fail
    final dst = tempPath('gen.bak');
    final previous = List<int>.generate(64, (i) => i);
    File(dst).writeAsBytesSync(previous);

    expect(
      () => replaceFileAtomicallyWithRetry(src, dst),
      throwsA(isA<FileSystemException>()),
      reason: 'renaming a missing source must fail',
    );

    expect(
      File(dst).existsSync() ? File(dst).readAsBytesSync() : null,
      previous,
      reason: 'the destination (previous generation) must not be removed by a '
          'failed rotation (C5)',
    );
    expect(File('$dst.stage').existsSync(), isFalse,
        reason: 'a failed rotation must not leave a staged copy behind here');
  });

  test('a rotation replaces an existing destination', () {
    final src = tempPath('gen.tmp');
    final dst = tempPath('gen.bak');
    File(dst).writeAsBytesSync(List<int>.filled(4096, 0xAA));
    final newGeneration = List<int>.generate(32, (i) => i);
    File(src).writeAsBytesSync(newGeneration);

    replaceFileAtomicallyWithRetry(src, dst);

    expect(File(dst).readAsBytesSync(), newGeneration);
    expect(File(src).existsSync(), isFalse,
        reason: 'the source must be consumed by the rotation');
  });

  /// End-to-end half: `.bak` holds the previous generation in full, and is a
  /// complete, openable database rather than a half-written file (T17).
  test('the rotation leaves a complete previous generation behind', () {
    final dbPath = tempPath('rotate.db');
    final bakPath = '$dbPath.bak';

    final db1 = CkvDb.open(dbPath);
    db1.putString('', 'k', 'v1');
    db1.close();
    final gen1Len = File(dbPath).lengthSync();

    final db2 = CkvDb.open(dbPath);
    db2.putString('', 'k', 'v2');
    db2.close();

    expect(File(bakPath).existsSync(), isTrue,
        reason: 'the rotation must produce `.bak`');
    expect(File(bakPath).lengthSync(), gen1Len,
        reason: '`.bak` must hold the previous generation in full');

    // `.bak` must be openable on its own, i.e. never a half-written file.
    final copy = tempPath('gen1_copy.db');
    File(bakPath).copySync(copy);
    final restored = CkvDb.open(copy);
    expect(restored.getString('', 'k'), 'v1');
    restored.close();

    for (var i = 3; i < 8; i++) {
      final db = CkvDb.open(dbPath);
      db.putString('', 'k', 'v$i');
      db.close();
      expect(File(bakPath).existsSync(), isTrue,
          reason: 'rotation $i left no `.bak` behind');
    }
  });
}
