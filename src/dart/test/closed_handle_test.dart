import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// FIX-22 regression: a handle kept past `close()` must not write.
///
/// `close()` releases the inter-process connection lock, so a write that still
/// succeeded afterwards would land in a database another process may already
/// hold - exactly the two-writer situation the lock exists to prevent. Rust
/// (.closed), .NET (_isDisposed) and Kotlin (isDisposed) already refuse; Go and
/// Dart accepted the write before this fix.
void main() {
  test('writes after close are refused and the file is untouched', () {
    final dir = Directory.systemTemp.createTempSync('ckv_closed_');
    final path = p.join(dir.path, 'closed.db');
    try {
      final db = CkvDb.open(path);
      db.putString('', 'before', '1');
      db.close();

      final before = File(path).lengthSync();

      expect(
        () => db.putString('', 'after', '2'),
        throwsA(isA<LockedError>()),
      );
      expect(() => db.delete('', 'before'), throwsA(isA<LockedError>()));
      expect(() => db.flush(), throwsA(isA<LockedError>()));
      expect(() => db.compact(), throwsA(isA<LockedError>()));

      expect(File(path).lengthSync(), before,
          reason: 'a refused write must not touch the file');

      // close() is idempotent.
      db.close();

      final reopened = CkvDb.open(path);
      expect(reopened.getString('', 'after'), isNull);
      expect(reopened.getString('', 'before'), '1');
      reopened.close();
    } finally {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    }
  });
}
