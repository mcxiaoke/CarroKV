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

      // P1-12: 读操作在 close 之后必须全部拒绝，严禁静默读内存脏数据
      expect(() => db.getString('', 'before'), throwsA(isA<LockedError>()));
      expect(() => db.getBytes('', 'before'), throwsA(isA<LockedError>()));
      expect(() => db.containsKey('', 'before'), throwsA(isA<LockedError>()));
      expect(() => db.count(''), throwsA(isA<LockedError>()));
      expect(() => db.namespaces(), throwsA(isA<LockedError>()));
      expect(() => db.keys(''), throwsA(isA<LockedError>()));
      expect(() => db.iterPrefix(''), throwsA(isA<LockedError>()));
      expect(() => db.scanRange(''), throwsA(isA<LockedError>()));
      expect(() => db.createSnapshot(), throwsA(isA<LockedError>()));

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

  // ADR-20261017: 多句柄独立性与引用计数隔离
  test('shared instance close from one holder does not close other holders', () {
    final dir = Directory.systemTemp.createTempSync('ckv_shared_');
    final path = p.join(dir.path, 'shared.db');
    try {
      final h1 = CkvDb.open(path);
      final h2 = CkvDb.open(path);

      expect(identical(h1, h2), isFalse, reason: 'Each open must return a distinct handle wrapper');

      h1.putString('ns', 'k', 'v1');
      expect(h2.getString('ns', 'k'), 'v1');

      // h1 关闭
      h1.close();

      // h1 自身所有操作全部报错
      expect(() => h1.getString('ns', 'k'), throwsA(isA<LockedError>()));
      expect(() => h1.putString('ns', 'k', 'v2'), throwsA(isA<LockedError>()));

      // h2 保持有效并可正常读写
      expect(h2.getString('ns', 'k'), 'v1');
      h2.putString('ns', 'k2', 'v2');
      expect(h2.getString('ns', 'k2'), 'v2');

      // h2 关闭后底座才真正释放
      h2.close();
      expect(() => h2.getString('ns', 'k2'), throwsA(isA<LockedError>()));
    } finally {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    }
  });

  // §7.4 静态 backupFile 测试
  test('static backupFile creates standalone database', () {
    final dir = Directory.systemTemp.createTempSync('ckv_backup_');
    final src = p.join(dir.path, 'src.db');
    final dst = p.join(dir.path, 'dst.db');
    try {
      final db = CkvDb.open(src);
      db.putString('ns', 'hello', 'world');
      db.close();

      CkvDb.backupFile(src, dst);

      final reopened = CkvDb.open(dst);
      expect(reopened.getString('ns', 'hello'), 'world');
      reopened.close();
    } finally {
      try {
        dir.deleteSync(recursive: true);
      } catch (_) {}
    }
  });
}
