import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// C3：`batch { put(ns, k); clearNamespace(ns); }` 原先会在重启后让 `k` 复活。
///
/// 构建日志条目时，clearNamespace 是按**批处理运行之前**的内存状态展开的；
/// 同一批中更早 put 的键此时还不在该状态里，于是它的 opPut 已经进了日志，
/// 却没有对应的删除条目。内存侧随后被真的清空，因此分歧只在**重放日志**时暴露，
/// 也就是重开之后——而这正是既有 batch 测试从未做过的事。
///
/// 同一场景在两种引擎模式下都跑一遍：两种模式必须对同一写序列给出相同结果，
/// 只有追加日志模式会在此处分歧。
void main() {
  final tempBase = p.canonicalize(
    p.join(Directory.current.path, '..', '..', 'temp', 'dart_c3_regression'),
  );

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


  /// 打开、执行、必定关闭（该库没有 `use` 扩展，测试里统一用这个辅助）。
  T withDb<T>(String path, DbOptions opts, T Function(CkvDb db) body) {
    final db = CkvDb.open(path, opts);
    try {
      return body(db);
    } finally {
      db.close();
    }
  }

  DbOptions optionsFor(StorageEngineMode mode) => DbOptions(
        engineMode: mode,
        syncMode: SyncMode.immediate,
      );

  /// 同批内先 put 再 clearNamespace：内存中与重放后都必须不存在。
  void assertPutThenClearDoesNotResurrect(StorageEngineMode mode, String tag) {
    final dbPath = p.join(tempBase, 'c3_put_clear_$tag.db');
    final opts = optionsFor(mode);

    withDb(dbPath, opts, (db) {
      db.putString('ns', 'old', 'old_value');
      db.putString('other', 'keep', 'keep_value');
    });

    withDb(dbPath, opts, (db) {
      db.batch((b) {
        b.putString('ns', 'fresh', 'fresh_value');
        b.clearNamespace('ns');
        // 另一个命名空间不得被 clear 影响
        b.putString('other', 'keep2', 'keep2_value');
      });

      expect(db.getString('ns', 'fresh'), isNull,
          reason: 'memory: the key written before the clear must be gone');
      expect(db.getString('ns', 'old'), isNull);
    });

    // 重放磁盘日志：这里是原先键复活的位置
    withDb(dbPath, opts, (db) {
      expect(db.getString('ns', 'fresh'), isNull,
          reason: "replay: 'fresh' was resurrected (C3)");
      expect(db.getString('ns', 'old'), isNull);
      expect(db.getString('other', 'keep'), equals('keep_value'),
          reason: 'the clear must not touch other namespaces');
      expect(db.getString('other', 'keep2'), equals('keep2_value'));
    });
  }

  /// 镜像用例：clear 之后 put 的键必须存活，否则"修复"就成了丢弃真实写入。
  void assertClearThenPutSurvives(StorageEngineMode mode, String tag) {
    final dbPath = p.join(tempBase, 'c3_clear_put_$tag.db');
    final opts = optionsFor(mode);

    withDb(dbPath, opts, (db) {
      db.putString('ns', 'old', 'old_value');
    });

    withDb(dbPath, opts, (db) {
      db.batch((b) {
        b.clearNamespace('ns');
        b.putString('ns', 'fresh', 'fresh_value');
      });
    });

    withDb(dbPath, opts, (db) {
      expect(db.getString('ns', 'old'), isNull);
      expect(db.getString('ns', 'fresh'), equals('fresh_value'),
          reason: 'a put issued after the clear must survive the replay');
    });
  }

  test('batch put then clear must not resurrect in WAL mode', () {
    assertPutThenClearDoesNotResurrect(StorageEngineMode.appendOnlyLog, 'wal');
  });

  test('batch put then clear must not resurrect in snapshot mode', () {
    assertPutThenClearDoesNotResurrect(StorageEngineMode.snapshotAtomic, 'snap');
  });

  test('batch clear then put must survive replay in WAL mode', () {
    assertClearThenPutSurvives(StorageEngineMode.appendOnlyLog, 'wal');
  });

  test('batch clear then put must survive replay in snapshot mode', () {
    assertClearThenPutSurvives(StorageEngineMode.snapshotAtomic, 'snap');
  });
}
