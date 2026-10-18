import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// 批次一「数据安全与止血」回归测试（Dart）。
///
/// 覆盖：
///  - P0-1 `normalizeNamespace` 必须校验 255 字节上限。
///        旧实现零校验，而写入侧 `RawOpEntry.encodeTo` 用 uint16 写 `NsLen`，
///        `writeUint16LE` 直接取低 16 位 —— 70000 字节的命名空间会把 `NsLen` 截断为
///        4464，条目头声明长度与实际载荷不符，**单次 `putString` 即永久摧毁整库**，
///        且全新库没有 `.bak` 可回退。
///  - P0-2 `"default"` 保留别名必须归一化到 `""`。未实现时 Dart 写出的 `"default"` 分区
///        在其余四门语言眼里就是默认分区 —— 跨语言互通直接断裂、双向数据损坏。
///  - P1-3 加载期校验 `NsLen <= 255`。
///  - P1-19 `.tmp` 晋升失败必须让 `open()` 失败，且不得删除已校验通过的 `.tmp`。
///  - P1-23 `deferred` 模式下 `open()` 失败不得留下 `Timer` —— 它会让进程永不退出。
///  - 条目 11 `readOnly` 选项冲突抛 `ConflictingOptionsError`。
void main() {
  late Directory tempDir;

  setUp(() {
    tempDir = Directory.systemTemp.createTempSync('ckv_batch_one_');
  });

  tearDown(() {
    try {
      tempDir.deleteSync(recursive: true);
    } catch (_) {}
  });

  String dbPath(String name) => p.join(tempDir.path, name);

  // ---------------- P0-1：命名空间长度校验 ----------------

  group('P0-1 namespace length limit', () {
    test('a 300-byte namespace is rejected before it can corrupt the file', () {
      final path = dbPath('too_long.db');
      final db = CkvDb.open(path);
      expect(
        () => db.putString('n' * 300, 'k', 'v'),
        throwsA(isA<InvalidNamespaceError>()),
      );
      expect(db.keys(''), isEmpty);

      // 关键：库本身必须完好，重开仍可用。
      db.close();
      final reopened = CkvDb.open(path);
      expect(reopened.keys(''), isEmpty);
      reopened.putString('ok', 'k', 'v');
      expect(reopened.getString('ok', 'k'), 'v');
      reopened.close();
    });

    test('a 70000-byte namespace is rejected (this used to destroy the database)', () {
      final path = dbPath('huge_ns.db');
      final db = CkvDb.open(path);
      expect(
        () => db.putString('n' * 70000, 'k', 'v'),
        throwsA(isA<InvalidNamespaceError>()),
      );
      // 不抛异常就是致命 bug：旧实现会写入一个 NsLen 被截断的条目，整库不可读。
      db.close();

      // 全新库单次写入后仍必须能打开（此前这里会得到 CorruptedError，且没有 .bak）。
      final reopened = CkvDb.open(path);
      expect(reopened.namespaces(), isEmpty);
      reopened.close();
    });

    test('the limit counts UTF-8 bytes, not Dart string length', () {
      final db = CkvDb.open(dbPath('byte_len.db'));
      // 'é' = 2 UTF-8 bytes, 1 UTF-16 code unit。128 个正好 256 字节 > 255。
      expect(
        () => db.putString('é' * 128, 'k', 'v'),
        throwsA(isA<InvalidNamespaceError>()),
      );
      // 127 个 = 254 字节，必须接受。
      db.putString('é' * 127, 'k', 'v');
      expect(db.getString('é' * 127, 'k'), 'v');
      db.close();
    });

    test('exactly 255 bytes is accepted', () {
      final db = CkvDb.open(dbPath('edge.db'));
      db.putString('n' * 255, 'k', 'v');
      expect(db.getString('n' * 255, 'k'), 'v');
      db.close();
    });

    test('every namespace entry point is covered by the single funnel', () {
      final db = CkvDb.open(dbPath('all_entries.db'));
      final longNs = 'n' * 300;
      expect(
        () => db.putBytes(longNs, 'k', Uint8List.fromList([1])),
        throwsA(isA<InvalidNamespaceError>()),
      );
      expect(
        () => db.putInt64(longNs, 'k', 1),
        throwsA(isA<InvalidNamespaceError>()),
      );
      expect(
        () => db.incrementInt64(longNs, 'k', 1),
        throwsA(isA<InvalidNamespaceError>()),
      );
      expect(
        () => db.delete(longNs, 'k'),
        throwsA(isA<InvalidNamespaceError>()),
      );
      expect(
        () => db.dropNamespace(longNs),
        throwsA(isA<InvalidNamespaceError>()),
      );
      expect(
        () => db.clearNamespace(longNs),
        throwsA(isA<InvalidNamespaceError>()),
      );
      expect(
        () => db.batch((w) => w.putString(longNs, 'k', 'v')),
        throwsA(isA<InvalidNamespaceError>()),
      );
      db.close();
    });
  });

  // ---------------- P0-2："default" 保留别名 ----------------

  group('P0-2 "default" reserved alias', () {
    test('"default" and "" are the same namespace', () {
      final path = dbPath('alias.db');
      final db = CkvDb.open(path);

      db.putString('', 'shared', '1');
      expect(db.keys('default'), equals(['shared']));
      expect(db.getString('default', 'shared'), '1');

      db.putString('default', 'via_alias', '2');
      expect(db.keys(''), containsAll(<String>['shared', 'via_alias']));
      // 物理上不得存在名为 "default" 的分区。
      expect(db.namespaces().contains('default'), isFalse);

      db.close();

      // 跨语言互通：Dart 写出的文件，其余四门语言必须把 "default" 归一成 ""。
      final reopened = CkvDb.open(path);
      expect(reopened.keys(''), containsAll(<String>['shared', 'via_alias']));
      expect(reopened.keys('default'), equals(reopened.keys('')));
      reopened.close();
    });

    test('the on-disk header stores the normalized (empty) namespace', () {
      final path = dbPath('alias_disk.db');
      final db = CkvDb.open(path);
      db.putString('default', 'k', 'v');
      db.close();

      final reopened = CkvDb.open(path);
      expect(reopened.namespaces(), equals(['']));
      expect(reopened.keys('default'), equals(['k']));
      reopened.close();
    });
  });

  // ---------------- P1-3：加载期 NsLen 越界 ----------------

  test('a file declaring NsLen over 255 is rejected at load time', () {
    final path = dbPath('nslen.db');
    File(path).writeAsBytesSync(_buildDbWithOverlongNamespace());

    expect(
      () => CkvDb.open(path),
      throwsA(
        isA<CorruptedError>().having(
          (e) => e.message,
          'message',
          contains('Namespace length 300'),
        ),
      ),
    );
  });

  // ---------------- P1-19：.tmp 晋升失败 ----------------

  test('open fails and keeps a validated .tmp when promotion is impossible', () {
    final path = dbPath('promote.db');
    final tmpPath = '$path.tmp';

    final db = CkvDb.open(path);
    db.putString('ns', 'k', 'v');
    db.close();

    File(tmpPath).writeAsBytesSync(File(path).readAsBytesSync());
    File(path).deleteSync();

    // 制造"晋升必然失败"：`.db` 不存在（因此一定走晋升分支），但 rename 失败。
    // RandomAccessStream 在 Windows 上不带 FILE_SHARE_DELETE，持有一个打开的句柄即可
    // 让 File.renameSync 失败；Unix 上则把父目录改成不可写。
    final isWindows = Platform.isWindows;
    RandomAccessFile? blocker;
    if (isWindows) {
      blocker = File(tmpPath).openSync();
    } else {
      final st = File(tmpPath).statSync();
      if (st.mode & 0x1 == 0) {
        markTestSkipped('cannot block rename on this platform without root');
      }
      Process.runSync('chmod', ['a-w', tmpPath]);
    }

    try {
      expect(
        () => CkvDb.open(path),
        throwsA(
          isA<CorruptedError>().having(
            (e) => e.message,
            'message',
            contains('.tmp'),
          ),
        ),
      );
    } finally {
      if (blocker != null) {
        blocker.closeSync();
      } else {
        Process.runSync('chmod', ['u+w', tmpPath]);
      }
    }

    // 关键断言：已经校验通过的 .tmp 必须原样保留 —— 旧实现的 catch-all 会删掉它，
    // 也就是销毁唯一的好数据。
    expect(File(tmpPath).existsSync(), isTrue,
        reason: 'a validated .tmp must never be deleted on promotion failure');
    expect(File(tmpPath).lengthSync(), greaterThan(0));

    // 解除占用后可以正常自愈，数据没丢。
    final recovered = CkvDb.open(path);
    expect(recovered.getString('ns', 'k'), 'v');
    recovered.close();
  });

  // ---------------- P1-23：deferred 模式 open 失败不得挂死进程 ----------------

  test('a failing deferred open leaves no timer behind (process can exit)', () {
    // Timer.periodic 会让 Dart VM 的事件循环保持存活。旧实现在构造函数里就起定时器，
    // 于是 deferred 模式下任何 open() 失败都会让 CLI 工具的进程永不退出。
    // 这里用一个隔离进程验证：脚本跑完必须能自行退出。
    const probeSource = r'''
import 'dart:io';
import 'package:ckv/ckv.dart';

void main() {
  // 一个必然损坏的文件：open() 会抛 CorruptedError。
  final dir = Directory.systemTemp.createTempSync('ckv_probe_');
  final p = '${dir.path}/broken.db';
  File(p).writeAsBytesSync([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
  try {
    CkvDb.open(p, DbOptions(syncMode: SyncMode.deferred));
  } catch (_) {
    // 预期路径
  }
  print('DONE');
}
''';

    // 探针脚本必须放在包内，dart run 才能沿目录树找到 .dart_tool/package_config.json
    // 并解析 `package:ckv/...`；放在系统临时目录里会解析失败。
    final inPackage = File(p.join(Directory.current.path, '.deferred_open_probe.dart'));
    try {
      inPackage.writeAsStringSync(probeSource);
      final result = Process.runSync(
        Platform.resolvedExecutable,
        ['run', inPackage.path],
        workingDirectory: Directory.current.path,
      );

      expect(result.exitCode, 0,
          reason: 'a deferred open() failure must not hang the process.\n'
              'stdout: ${result.stdout}\nstderr: ${result.stderr}');
      expect(result.stdout.toString(), contains('DONE'));
    } finally {
      try {
        inPackage.deleteSync();
      } catch (_) {}
    }
  });

  // ---------------- 条目 11：ReadOnly 冲突必须抛错 ----------------

  test('reentrant open with a readOnly conflict throws ConflictingOptionsError', () {
    final path = dbPath('ro.db');
    final db = CkvDb.open(path);
    db.putString('', 'k', 'v');

    // 以只读意图二次打开：绝不能静默返回读写实例（静默提权）。
    expect(
      () => CkvDb.open(path, DbOptions(readOnly: true)),
      throwsA(isA<ConflictingOptionsError>()),
    );

    // 反方向同样拒绝。
    db.close();
    final ro = CkvDb.open(path, DbOptions(readOnly: true));
    expect(ro.getString('', 'k'), 'v');
    expect(
      () => CkvDb.open(path, DbOptions(readOnly: false)),
      throwsA(isA<ConflictingOptionsError>()),
    );
    ro.close();
  });

  test('non-permission conflicts still warn and return the same instance', () {
    final path = dbPath('engine.db');
    final db = CkvDb.open(path, DbOptions(engineMode: StorageEngineMode.snapshotAtomic));
    db.putString('', 'k', 'v');

    final warnings = <String>[];
    final same = CkvDb.open(
      path,
      DbOptions(
        engineMode: StorageEngineMode.appendOnlyLog,
        onWarning: (code, msg) => warnings.add('$code: $msg'),
      ),
    );
    expect(identical(db, same), isFalse);
    expect(identical(db.sharedInstance, same.sharedInstance), isTrue);
    expect(warnings, hasLength(1));
    expect(warnings[0], startsWith('onOptionsConflict'));
    expect(warnings[0], contains('engineMode'));
    same.close();
    db.close();
  });
}

/// 构造一个条目头声明 `NsLen = 300`（规范上限 255）的合法 CRC 文件。
Uint8List _buildDbWithOverlongNamespace() {
  Uint8List u16(int v) => Uint8List.fromList([v & 0xFF, (v >> 8) & 0xFF]);
  Uint8List u32(int v) => Uint8List.fromList(
      [v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF, (v >> 24) & 0xFF]);

  final ns = utf8.encode('n' * 300);
  final key = utf8.encode('k');
  final value = utf8.encode('v');

  final entry = <int>[];
  entry.addAll([0x01, 0x01]); // opPut, valTypeString
  entry.addAll(u16(300)); // 越界的 NsLen
  entry.addAll(u16(key.length));
  entry.addAll(u32(value.length));
  entry.addAll(ns);
  entry.addAll(key);
  entry.addAll(value);
  final payload = Uint8List.fromList(entry);

  final header = Uint8List(32);
  header.setRange(0, 8, utf8.encode('UKVDB003'));
  header.setRange(8, 10, u16(3));
  header.setRange(10, 12, u16(3));
  header.setRange(12, 16, u32(0));
  header.setRange(28, 32, u32(_crc32(Uint8List.sublistView(header, 0, 28))));

  final tail20 = <int>[];
  tail20.addAll([1, 0, 0, 0, 0, 0, 0, 0]); // TxID = 1
  tail20.addAll(u32(1)); // opCount
  tail20.addAll(u32(payload.length));
  tail20.addAll(u32(_crc32(payload)));

  final frame = <int>[];
  frame.addAll(u32(0x4D52464B)); // 'KFRM'
  frame.addAll(u32(_crc32(Uint8List.fromList(tail20))));
  frame.addAll(tail20);
  frame.addAll(payload);

  return Uint8List.fromList([...header, ...frame]);
}

int _crc32(Uint8List data) {
  var crc = 0xFFFFFFFF;
  for (final b in data) {
    crc ^= b;
    for (var i = 0; i < 8; i++) {
      crc = (crc & 1) != 0 ? ((crc >> 1) ^ 0xEDB88320) : (crc >> 1);
    }
  }
  return (crc ^ 0xFFFFFFFF) & 0xFFFFFFFF;
}