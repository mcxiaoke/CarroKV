import 'dart:io';
import 'dart:typed_data';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// Fuzz-lite：对真实数据库文件做确定性变异。
///
/// 不是覆盖率引导的 fuzzer。固定、可复现的变异集覆盖黄金向量 + 一个生成的 AOL 文件，
/// 断言两件事：
///   1. 不崩溃、不挂死——被变异过的文件要么被干净拒绝，要么能完整读出；
///   2. 结构性损坏必须被检测，不能被当成空库静默接受。
///
/// 与 Rust / Go / .NET / Kotlin 的同源套件一致。
void main() {
  final repoRoot = p.canonicalize(p.join(Directory.current.path, '..', '..'));
  final workBase = p.join(repoRoot, 'temp', 'dart_fuzz_lite');
  var seq = 0;

  List<({String name, Uint8List bytes})> seeds() {
    final out = <({String name, Uint8List bytes})>[];
    for (final name in [
      'empty_v3.db',
      'single_put_v3.db',
      'multi_types_v3.db',
      'unicode_order_v3.db',
    ]) {
      final f = File(p.join(repoRoot, 'testdata', name));
      if (f.existsSync()) out.add((name: name, bytes: f.readAsBytesSync()));
    }

    final dir = Directory(p.join(workBase, 'seed_${seq++}'))..createSync(recursive: true);
    final path = p.join(dir.path, 'generated.db');
    try {
      final db = CkvDb.open(
        path,
        DbOptions(
          engineMode: StorageEngineMode.appendOnlyLog,
          syncMode: SyncMode.manual,
        ),
      );
      try {
        db.putString('ns', 'alpha', '1');
        db.putString('ns', 'beta', '22');
        db.putInt64('ns2', 'gamma', -9223372036854775808);
      } finally {
        db.close();
      }
      out.add((name: 'generated_aol', bytes: File(path).readAsBytesSync()));
    } finally {
      dir.deleteSync(recursive: true);
    }
    return out;
  }

  bool accepted(Object err) =>
      err is CorruptedError ||
      err is UnsupportedVersionError ||
      err is InvalidUtf8Error ||
      err is KeyTooLargeError ||
      err is FileSystemException;

  /// 返回 'opened' / 'rejected'；非预期异常直接抛出。
  String exercise(Directory dir, int tag, Uint8List data) {
    final path = p.join(dir.path, 'case_$tag.db');
    File(path).writeAsBytesSync(data);
    CkvDb? db;
    try {
      db = CkvDb.open(path);
      for (final ns in db.namespaces()) {
        db.count(ns);
        for (final key in db.keys(ns)) {
          db.getBytes(ns, key);
        }
        db.scanRange(ns);
      }
      return 'opened';
    } catch (err) {
      if (accepted(err)) return 'rejected';
      rethrow;
    } finally {
      db?.close();
    }
  }

  test('mutated files never panic', () {
    Directory(workBase).createSync(recursive: true);
    final dir = Directory(p.join(workBase, 'cases_${seq++}'))..createSync(recursive: true);
    var tag = 0;
    var cases = 0;
    var rejected = 0;

    void check(String seed, String what, Uint8List data) {
      tag++;
      cases++;
      try {
        if (exercise(dir, tag, data) == 'rejected') rejected++;
      } catch (err) {
        fail('$seed $what: unexpected ${err.runtimeType}: $err');
      }
    }

    try {
      for (final seed in seeds()) {
        final original = seed.bytes;
        final n = original.length;

        final cuts = <int>[0, 1, 15, 31, 32, 33];
        if (n > 1) cuts.add(n - 1);
        for (final cut in cuts) {
          if (cut > n) continue;
          check(seed.name, 'truncated to $cut bytes',
              Uint8List.sublistView(original, 0, cut));
        }

        for (var offset = 0; offset < n; offset++) {
          for (final mask in [0xFF, 0x01, 0x80]) {
            final mutated = Uint8List.fromList(original);
            mutated[offset] = mutated[offset] ^ mask;
            check(seed.name, 'flip at $offset', mutated);
          }
        }

        for (final extra in [
          <int>[0x00],
          <int>[0xFF],
          <int>[0x00, 0x01, 0x02, 0x03],
        ]) {
          check(seed.name, 'append ${extra.length} bytes',
              Uint8List.fromList([...original, ...extra]));
        }

        // 结构性损坏必须被检测。
        tag++;
        cases++;
        final shortPath = p.join(dir.path, 'case_$tag.db');
        File(shortPath).writeAsBytesSync(Uint8List.sublistView(original, 0, n < 31 ? n : 31));
        var shortOpened = true;
        try {
          CkvDb.open(shortPath).close();
        } on Object catch (err) {
          if (!accepted(err)) rethrow;
          shortOpened = false;
        }
        expect(shortOpened, isFalse,
            reason: '${seed.name} truncated below the header was accepted');

        if (n > 0) {
          tag++;
          cases++;
          final magic = Uint8List.fromList(original);
          magic[0] = magic[0] ^ 0xFF;
          final magicPath = p.join(dir.path, 'case_$tag.db');
          File(magicPath).writeAsBytesSync(magic);
          var magicOpened = true;
          try {
            CkvDb.open(magicPath).close();
          } on Object catch (err) {
            if (!accepted(err)) rethrow;
            magicOpened = false;
          }
          expect(magicOpened, isFalse,
              reason: '${seed.name} with corrupted magic was accepted');
        }
      }
    } finally {
      dir.deleteSync(recursive: true);
    }

    expect(cases, greaterThan(1000), reason: 'mutation set unexpectedly small');
    expect(rejected, greaterThan(cases ~/ 10),
        reason: 'only $rejected of $cases mutations were rejected - '
            'corruption detection looks broken');
  });
}
