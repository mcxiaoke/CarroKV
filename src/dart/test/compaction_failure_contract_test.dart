import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// D2：压实失败契约。
///
/// 自动压实运行时，本次变更已经 append + fsync 落盘；此时向调用方报错等于让它重试一个
/// 已经成功的写，而 `increment` 是 read-modify-write，重试会重复累加 delta。因此压实
/// 失败改为：`onCompactionFailed` 告警 + 置待重试 + 退避重试（`close()` 再兜底一次）；
/// 开启 `strictCompaction` 才向上抛出。
///
/// 失败用**真实文件系统条件**注入：`_commitFlush` 轮转前会写 `<db>.tmp`，在该路径放一个
/// **非空目录**即可让它打开失败，而对 `<db>` 的追加写入照常成功。
void main() {
  final tempBase = p.canonicalize(
    p.join(Directory.current.path, '..', '..', 'temp', 'dart_d2_regression'),
  );

  const tinyThreshold = 512;

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

  DbOptions options(void Function() onCompactionFailed) => DbOptions(
        engineMode: StorageEngineMode.appendOnlyLog,
        syncMode: SyncMode.immediate,
        compactionThresholdBytes: tinyThreshold,
        onWarning: (code, _) {
          if (code == 'onCompactionFailed') onCompactionFailed();
        },
      );

  String tmpPath(String dbPath) => '$dbPath.tmp';

  /// 目录必须非空：恢复流程会尝试清理残留 `.tmp`，空目录可能被删掉。
  void blockCompaction(String dbPath) {
    final tmp = Directory(tmpPath(dbPath))..createSync();
    File(p.join(tmp.path, 'keep')).writeAsStringSync('x');
  }

  void unblockCompaction(String dbPath) {
    try {
      File(p.join(tmpPath(dbPath), 'keep')).deleteSync();
    } catch (_) {}
    try {
      Directory(tmpPath(dbPath)).deleteSync();
    } catch (_) {}
  }

  void seed(String dbPath, DbOptions opts) {
    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'seed', 'seed_value');
    db.close();
  }

  /// D2-1：写仍返回成功、失败有告警、期间写入的数据重开后完整。
  test('compaction failure must not fail the write', () {
    final dbPath = p.join(tempBase, 't6a.db');
    var failures = 0;
    final opts = options(() => failures++);

    seed(dbPath, opts);
    blockCompaction(dbPath);

    final db = CkvDb.open(dbPath, opts);
    for (var i = 0; i < 30; i++) {
      try {
        db.putString('', 'k${i.toString().padLeft(3, '0')}', 'v');
      } catch (err) {
        fail('a failed compaction must not fail the write (D2): $err');
      }
    }
    expect(failures, greaterThanOrEqualTo(1),
        reason: 'the compaction failure must be reported through onCompactionFailed');
    expect(File('$dbPath.bak').existsSync(), isFalse,
        reason: 'the test must actually have blocked compaction');
    db.close();

    final reopened = CkvDb.open(dbPath, opts);
    expect(reopened.getString('', 'seed'), equals('seed_value'));
    for (var i = 0; i < 30; i++) {
      expect(reopened.getString('', 'k${i.toString().padLeft(3, '0')}'), equals('v'));
    }
    reopened.close();
  });

  /// D2-4：退避节流 —— 窗口内不重试，跨过窗口恰好一次。
  test('compaction backoff must throttle retries', () {
    final dbPath = p.join(tempBase, 't6b.db');
    var failures = 0;
    final opts = options(() => failures++);

    seed(dbPath, opts);
    blockCompaction(dbPath);

    final db = CkvDb.open(dbPath, opts);
    var written = 0;
    while (failures == 0) {
      db.putString('', 'a${written.toString().padLeft(3, '0')}', 'v');
      written++;
      expect(written, lessThan(500), reason: 'the compaction failure was never triggered');
    }
    final first = failures;
    expect(first, equals(1));

    // 首次失败后的退避窗口为 64 次写
    for (var i = 0; i < 30; i++) {
      db.putString('', 'b${written.toString().padLeft(3, '0')}', 'v');
      written++;
    }
    expect(failures, equals(first), reason: 'retries must be suppressed inside the window');

    for (var i = 0; i < 40; i++) {
      db.putString('', 'c${written.toString().padLeft(3, '0')}', 'v');
      written++;
    }
    expect(failures, equals(first + 1), reason: 'exactly one retry after the window');
    db.close();
  });

  /// D2-3：close() 兜底重试，使瞬时故障不跟随实例整个生命周期。
  test('close must retry a pending compaction', () {
    final dbPath = p.join(tempBase, 't6c.db');
    var failures = 0;
    final opts = options(() => failures++);

    seed(dbPath, opts);
    blockCompaction(dbPath);

    final db = CkvDb.open(dbPath, opts);
    for (var i = 0; i < 30; i++) {
      db.putString('', 'k${i.toString().padLeft(3, '0')}', 'v');
    }
    expect(failures, greaterThanOrEqualTo(1));
    expect(File('$dbPath.bak').existsSync(), isFalse);

    unblockCompaction(dbPath);
    db.close();

    expect(File('$dbPath.bak').existsSync(), isTrue,
        reason: 'close() must retry a pending compaction');
  });

  /// D2-5：strictCompaction 向上抛错；而报错的那次写依然已持久化——这正是默认不上抛的原因。
  test('strict compaction must surface the error', () {
    final dbPath = p.join(tempBase, 't6d.db');
    var failures = 0;
    final opts = DbOptions(
      engineMode: StorageEngineMode.appendOnlyLog,
      syncMode: SyncMode.immediate,
      compactionThresholdBytes: tinyThreshold,
      strictCompaction: true,
      onWarning: (code, _) {
        if (code == 'onCompactionFailed') failures++;
      },
    );

    seed(dbPath, opts);
    blockCompaction(dbPath);

    String? failingKey;
    final db = CkvDb.open(dbPath, opts);
    for (var i = 0; i < 30; i++) {
      final key = 'k${i.toString().padLeft(3, '0')}';
      try {
        db.putString('', key, 'v');
      } catch (_) {
        failingKey = key;
        break;
      }
    }
    expect(failingKey, isNotNull,
        reason: 'strictCompaction must surface the compaction failure');
    // The warning still fires in strict mode; the option only decides whether the
    // error additionally reaches the caller.
    expect(failures, greaterThanOrEqualTo(1));
    db.close();
    unblockCompaction(dbPath);

    // 错误只表示"压实失败"，不表示"写丢失"：帧早已 append + fsync。
    final reopened = CkvDb.open(dbPath, opts);
    expect(reopened.getString('', failingKey!), equals('v'));
    reopened.close();
  });
}
