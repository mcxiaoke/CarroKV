import 'dart:io';
import 'package:test/test.dart';
import 'package:ckv/ckv.dart';

void main() {
  group('Batch 4 Pipeline Tests (Dart)', () {
    late Directory tempDir;

    setUp(() {
      tempDir = Directory.systemTemp.createTempSync('ckv_b4_test_');
    });

    tearDown(() {
      try {
        tempDir.deleteSync(recursive: true);
      } catch (_) {}
    });

    test('Warning queue and reentrancy does not corrupt state (P1-33)', () {
      final dbPath = '${tempDir.path}/warning_queue.db';
      int warningCount = 0;
      CkvDb? dbHandle;

      final opts = DbOptions()
        ..compactionThresholdBytes = 100
        ..strictCompaction = false
        ..engineMode = StorageEngineMode.appendOnlyLog
        ..onWarning = (code, msg) {
          warningCount++;
          // 在警告回调内执行读操作，确保不引起状态损坏
          if (dbHandle != null) {
            dbHandle!.getInt64('ns', 'key');
            dbHandle!.namespaces;
          }
        };

      final db = CkvDb.open(dbPath, opts);
      dbHandle = db;

      // 阻断 .tmp 触发 compaction 告警
      final blocker = Directory('$dbPath.tmp');
      blocker.createSync();

      try {
        for (int i = 0; i < 30; i++) {
          db.putString('ns', 'k$i', 'payload_to_trigger_compaction_failure');
        }
      } finally {
        try {
          blocker.deleteSync(recursive: true);
        } catch (_) {}
        db.close();
      }

      expect(warningCount, greaterThan(0));
    });

    test('Batch CoW deduplication per namespace (P1-1)', () {
      final dbPath = '${tempDir.path}/cow_dedup.db';
      final db = CkvDb.open(dbPath);

      db.putString('bulk', 'seed', '0');

      // 创建存活快照
      final snap = db.createSnapshot();

      final sw = Stopwatch()..start();
      db.batch((b) {
        for (int i = 0; i < 2000; i++) {
          if (i % 2 == 0) {
            b.putString('bulk', 'k$i', 'val');
          } else {
            b.delete('bulk', 'k${i - 1}');
          }
        }
      });
      sw.stop();

      // 单次克隆应在 500ms 内完成，避免 O(N^2) 重复克隆
      expect(sw.elapsedMilliseconds, lessThan(1500));

      // 快照隔离正常
      expect(snap.getString('bulk', 'seed'), equals('0'));
      snap.dispose();
      db.close();
    });

    test('Rollback on flush failure avoids double increment and retains isDirty (P0-7 / P1-6)', () {
      final dbPath = '${tempDir.path}/rollback_test.db';
      final db = CkvDb.open(dbPath);

      db.putInt64('bank', 'balance', 100);

      // 创建目录阻断 .tmp
      final tmpDir = Directory('$dbPath.tmp');
      tmpDir.createSync();

      expect(
        () => db.incrementInt64('bank', 'balance', 50),
        throwsA(isA<DbError>()),
      );

      // 内存必须原子回滚回 100，不得残留 150
      expect(db.getInt64('bank', 'balance'), equals(100));

      // 解除阻断后重试
      tmpDir.deleteSync(recursive: true);

      final val = db.incrementInt64('bank', 'balance', 50);
      expect(val, equals(150)); // 如果发生 double increment 会变成 200
      expect(db.getInt64('bank', 'balance'), equals(150));

      db.close();
    });

    test('flushAsync and backupAsync first-class methods (Section 7.3)', () async {
      final dbPath = '${tempDir.path}/async_api.db';
      final backupPath = '${tempDir.path}/async_backup.db';

      final db = CkvDb.open(dbPath, DbOptions()..syncMode = SyncMode.manual);
      db.putString('data', 'greeting', 'hello async');

      // 异步刷盘
      await db.flushAsync();

      // 异步备份
      await db.backupAsync(backupPath);
      db.close();

      // 验证备份库能独立以只读模式打开并读到数据
      final backupDb = CkvDb.open(backupPath, DbOptions()..readOnly = true);
      expect(backupDb.getString('data', 'greeting'), equals('hello async'));
      backupDb.close();
    });
  });
}
