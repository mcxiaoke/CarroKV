import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:test/test.dart';

/// N-X8 regression: a failed `strictCompaction` must not roll back a mutation
/// that is already appended to the log.
///
/// `_applyMutation` used to run `_maybeCompactAfterWrite` inside the same `try`
/// as `applyMem`, so a compaction failure rolled the in-memory state back while
/// the frame stayed in the log: the session read the old value, a reopen read
/// the new one, and the next flush could overwrite the log with the stale
/// memory and lose the write for good.
void main() {
  group('strictCompaction keeps durable writes (N-X8)', () {
    late Directory tempDir;

    setUp(() {
      tempDir = Directory.systemTemp.createTempSync('ckv_compaction_');
    });

    tearDown(() {
      try {
        tempDir.deleteSync(recursive: true);
      } catch (_) {}
    });

    test('a failed compaction does not undo the append', () async {
      final dbPath = '${tempDir.path}/strict.db';
      final opts = DbOptions()
        ..syncMode = SyncMode.immediate
        ..engineMode = StorageEngineMode.appendOnlyLog
        ..compactionThresholdBytes = 64
        ..strictCompaction = true;

      final db = CkvDb.open(dbPath, opts);

      // 用目录占住 `.tmp`：压实必然失败，且 strictCompaction 会把错误抛给调用方。
      final blocker = Directory('$dbPath.tmp')..createSync();
      try {
        expect(() => db.putString('', 'k', 'v'), throwsA(anything));
      } finally {
        blocker.deleteSync(recursive: true);
      }

      // 帧已经进日志，内存必须保留这次写（旧实现会把它回滚掉）。
      expect(db.getString('', 'k'), 'v',
          reason: 'a durable append was rolled back out of memory');

      db.flush();
      db.close();

      final reopened = CkvDb.open(dbPath);
      expect(reopened.getString('', 'k'), 'v');
      reopened.close();
    });

    test('non-strict compaction failure is silent and keeps the value', () {
      final dbPath = '${tempDir.path}/nonstrict.db';
      final opts = DbOptions()
        ..syncMode = SyncMode.immediate
        ..engineMode = StorageEngineMode.appendOnlyLog
        ..compactionThresholdBytes = 64
        ..strictCompaction = false;

      final db = CkvDb.open(dbPath, opts);
      final blocker = Directory('$dbPath.tmp')..createSync();
      try {
        db.putString('', 'k', 'v');
        expect(db.getString('', 'k'), 'v');
      } finally {
        blocker.deleteSync(recursive: true);
      }
      db.close();
    });
  });
}
