import 'dart:async';
import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:test/test.dart';

/// N-X6 / R1-P0-2 regression: the async commit path must not clobber a newer
/// generation, and two async commits must never interleave around their await
/// points.
///
/// Old behaviour: `flushAsync` serialized the current state, suspended while
/// writing `.tmp`, and only afterwards renamed it over `.db` and cleared
/// `_isDirty` - without re-checking whether anything had been written in the
/// meantime. A synchronous `put` landing inside that window was silently
/// rolled back on the next reopen (and the TxID could go backwards, which makes
/// the next open report `CorruptedError`).
void main() {
  group('async commit serialization (N-X6)', () {
    late Directory tempDir;

    setUp(() {
      tempDir = Directory.systemTemp.createTempSync('ckv_async_commit_');
    });

    tearDown(() {
      try {
        tempDir.deleteSync(recursive: true);
      } catch (_) {}
    });

    test('a write landing inside flushAsync is not lost', () async {
      final dbPath = '${tempDir.path}/interleave.db';
      final opts = DbOptions()..syncMode = SyncMode.deferred;
      final db = CkvDb.open(dbPath, opts);

      // 10 轮以覆盖不同的调度交错：任一轮丢写都会体现在最终重开的断言里。
      for (var round = 0; round < 10; round++) {
        db.putString('', 'k_${round}_a', 'a');
        final pending = db.flushAsync();
        // 让 flushAsync 先跑到 await 点（写 .tmp），再插入一次同步写。
        await Future<void>.delayed(Duration.zero);
        db.putString('', 'k_${round}_b', 'b');
        await pending;
      }

      db.close();

      final reopened = CkvDb.open(dbPath);
      for (var round = 0; round < 10; round++) {
        expect(reopened.getString('', 'k_${round}_a'), 'a',
            reason: 'round $round: the write that started the flush was lost');
        expect(reopened.getString('', 'k_${round}_b'), 'b',
            reason:
                'round $round: a write interleaved with flushAsync was lost');
      }
      reopened.close();
    });

    test('concurrent flushAsync calls serialize instead of sharing .tmp',
        () async {
      final dbPath = '${tempDir.path}/concurrent.db';
      final opts = DbOptions()..syncMode = SyncMode.deferred;
      final db = CkvDb.open(dbPath, opts);

      for (var i = 0; i < 20; i++) {
        db.putString('', 'k$i', 'v$i');
      }

      await Future.wait([db.flushAsync(), db.flushAsync(), db.flushAsync()]);
      db.close();

      final reopened = CkvDb.open(dbPath);
      for (var i = 0; i < 20; i++) {
        expect(reopened.getString('', 'k$i'), 'v$i');
      }
      reopened.close();
    });

    test('flushAsync after a failed flush still converges', () async {
      final dbPath = '${tempDir.path}/recover.db';
      final opts = DbOptions()..syncMode = SyncMode.deferred;
      final db = CkvDb.open(dbPath, opts);
      db.putString('', 'k1', 'v1');

      // 用目录占住 `.tmp` 路径，让第一次提交必然失败。
      final blocker = Directory('$dbPath.tmp')..createSync();
      await expectLater(db.flushAsync(), throwsA(anything));
      blocker.deleteSync(recursive: true);

      // 失败不清脏：下一轮必须把数据补上。
      await db.flushAsync();
      db.close();

      final reopened = CkvDb.open(dbPath);
      expect(reopened.getString('', 'k1'), 'v1');
      reopened.close();
    });

    test('backupAsync and flushAsync do not collide on their temp files',
        () async {
      final dbPath = '${tempDir.path}/backup.db';
      final backupPath = '${tempDir.path}/backup_copy.db';
      final opts = DbOptions()..syncMode = SyncMode.deferred;
      final db = CkvDb.open(dbPath, opts);
      db.putString('', 'k', 'v');

      await Future.wait([
        db.flushAsync(),
        db.backupAsync(backupPath),
      ]);

      db.close();
      final copy = CkvDb.open(backupPath);
      expect(copy.getString('', 'k'), 'v');
      copy.close();
    });
  });
}
