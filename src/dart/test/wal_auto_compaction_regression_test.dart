import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:ckv/ckv.dart';
import 'package:ckv/src/constants.dart';
import 'package:ckv/src/format.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// Regression tests for the auto-compaction write path, log-damage
/// classification and the `.bak` recovery contract.
///
/// They mirror the Rust, Go and .NET suites so that every engine is locked to the
/// same behaviour. These paths previously had zero coverage: the existing WAL
/// tests use the default compaction threshold (never reached) or call `compact()`
/// by hand.
///
/// Covered defects:
///   * C1 - compaction ran before the mutation reached the in-memory state, so the
///     write that crossed the threshold was appended and then overwritten by a
///     snapshot that did not contain it.
///   * C2 - compaction nested the `.db` lock while the caller still held it, so
///     compaction could never succeed and crossing writes blocked for the lock
///     timeout (on POSIX the reentrant lock also let the inner release delete the
///     outer lock file).
///   * C4 - damage in the middle of the log was treated as a torn tail and
///     silently truncated, discarding every intact record after it.
const int _fileHeaderLen = 32;
const int _tinyThreshold = 512;
const int _noCompaction = 0x7FFFFFFFFFFFFFFF;

void main() {
  final tempBase = p.canonicalize(
    p.join(Directory.current.path, '..', '..', 'temp', 'dart_wal_regression'),
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

  DbOptions walOptions(int threshold) => DbOptions(
        engineMode: StorageEngineMode.appendOnlyLog,
        syncMode: SyncMode.immediate,
        compactionThresholdBytes: threshold,
      );

  void flipByteAt(String path, int offset) {
    final raf = File(path).openSync(mode: FileMode.append);
    try {
      raf.setPositionSync(offset);
      final original = raf.readByteSync();
      expect(original, isNonNegative, reason: 'offset must be inside the file');
      raf.setPositionSync(offset);
      raf.writeByteSync(original ^ 0xFF);
      raf.flushSync();
    } finally {
      raf.closeSync();
    }
  }

  /// Byte length of the frame produced by exactly one `putString`, derived
  /// empirically so the tests do not depend on the on-disk frame layout.
  ///
  /// Each call uses a fresh probe file: reusing one path would append a second
  /// frame on the next call and silently double the measured length.
  var probeSeq = 0;
  int probeFrameLen() {
    final probe = p.join(tempBase, 'probe_frame_len_${probeSeq++}.db');
    final db = CkvDb.open(probe, walOptions(_noCompaction));
    db.putString('', 'a', '1');
    db.close();

    final total = File(probe).lengthSync();
    expect(total, greaterThan(_fileHeaderLen),
        reason: 'probe database must contain one frame');
    return total - _fileHeaderLen;
  }

  /// C1: the write that crosses the compaction threshold must survive a reopen,
  /// together with every other write.
  test('auto compaction must not lose writes', () {
    final dbPath = p.join(tempBase, 't1.db');
    final opts = walOptions(_tinyThreshold);
    const keys = 300;

    final db = CkvDb.open(dbPath, opts);
    for (var i = 0; i < keys; i++) {
      final key = 'key${i.toString().padLeft(4, '0')}';
      db.putString('', key, 'value$key');
    }
    db.close();

    // Guard against a vacuous pass: rotation during compaction produces `.bak`.
    expect(File('$dbPath.bak').existsSync(), isTrue,
        reason: 'auto compaction never ran (threshold $_tinyThreshold)');

    final reopened = CkvDb.open(dbPath, opts);
    final broken = <String>[];
    for (var i = 0; i < keys; i++) {
      final key = 'key${i.toString().padLeft(4, '0')}';
      final want = 'value$key';
      final got = reopened.getString('', key);
      if (got != want) broken.add('$key: want $want, got $got');
    }
    reopened.close();

    expect(broken, isEmpty,
        reason: '${broken.length} of $keys keys did not survive auto compaction: '
            '${broken.take(5).join(', ')}');
  });

  /// C2: threshold-triggered compaction must actually complete, reclaim space and
  /// must not stall each write for a lock timeout.
  test('auto compaction must run without lock stalls', () {
    final dbPath = p.join(tempBase, 't2.db');
    final opts = walOptions(_tinyThreshold);
    final value = 'x' * 64;

    final db = CkvDb.open(dbPath, opts);
    var peak = 0;
    var worst = Duration.zero;

    for (var i = 0; i < 200; i++) {
      final sw = Stopwatch()..start();
      db.putString('', 'hot', value);
      sw.stop();
      if (sw.elapsed > worst) worst = sw.elapsed;
      final len = File(dbPath).lengthSync();
      if (len > peak) peak = len;
    }
    db.close();

    // 200 writes of a 64-byte value append far more than 8x the threshold.
    expect(peak, lessThanOrEqualTo(8 * _tinyThreshold),
        reason: 'file grew to $peak bytes: compaction never reclaimed space');
    expect(File('$dbPath.bak').existsSync(), isTrue,
        reason: 'compaction never produced a `.bak` rotation');
    expect(File('$dbPath.tmp').existsSync(), isFalse,
        reason: 'compaction left a `.tmp` file behind');
    // A nested lock acquisition blocks for the lock timeout (5 s by default).
    expect(worst, lessThan(const Duration(seconds: 2)),
        reason: 'a single write took $worst: compaction is blocking the write path');
  });

  /// C4: damage to a record that is followed by intact records cannot be a torn
  /// write on an append-only file, so opening must report corruption.
  test('mid-log damage must be reported as corruption', () {
    final dbPath = p.join(tempBase, 't4.db');
    final opts = walOptions(_noCompaction);
    final frameLen = probeFrameLen();

    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'a', '1');
    db.putString('', 'b', '2');
    db.putString('', 'c', '3');
    db.close();

    final healthyLen = _fileHeaderLen + 3 * frameLen;
    // Flip one byte inside the second frame; the third frame stays intact.
    flipByteAt(dbPath, _fileHeaderLen + frameLen + 8);

    expect(() => CkvDb.open(dbPath, opts), throwsA(isA<CorruptedError>()));
    // A refused open must leave the log untouched for offline recovery.
    expect(File(dbPath).lengthSync(), equals(healthyLen));
  });

  /// C4 negative control: a genuine torn write (the file ends inside the last
  /// frame) must keep healing.
  test('torn tail must still heal', () {
    final dbPath = p.join(tempBase, 't5.db');
    final opts = walOptions(_noCompaction);
    final frameLen = probeFrameLen();

    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'a', '1');
    db.putString('', 'b', '2');
    db.putString('', 'c', '3');
    db.close();

    final healthyLen = File(dbPath).lengthSync();
    final raf = File(dbPath).openSync(mode: FileMode.append);
    try {
      raf.truncateSync(healthyLen - frameLen ~/ 2);
      raf.flushSync();
    } finally {
      raf.closeSync();
    }

    final reopened = CkvDb.open(dbPath, opts);
    expect(reopened.getString('', 'a'), equals('1'));
    expect(reopened.getString('', 'b'), equals('2'));
    expect(reopened.getString('', 'c'), isNull,
        reason: 'the torn frame must be discarded as a whole');
    reopened.close();

    expect(File(dbPath).lengthSync(), equals(_fileHeaderLen + 2 * frameLen));
  });

  /// Writes two generations so `.bak` holds the first one, then flips a byte in
  /// the file header of the primary to make it fail validation.
  void writeSecondGeneration(String dbPath, DbOptions opts) {
    for (final value in ['v1', 'v2']) {
      final db = CkvDb.open(dbPath, opts);
      db.putString('', 'version', value);
      db.close();
    }
    flipByteAt(dbPath, 14); // inside the 32-byte file header
  }

  /// D1: a corrupt primary file must be reported as corruption by default.
  test('corrupt primary is rejected by default', () {
    final dbPath = p.join(tempBase, 'd1.db');
    final bakPath = '$dbPath.bak';

    final warnings = <String>[];
    final opts = DbOptions(onWarning: (code, _) => warnings.add(code));

    writeSecondGeneration(dbPath, opts);
    expect(File(bakPath).existsSync(), isTrue,
        reason: 'this test needs an existing previous generation');

    final primaryLen = File(dbPath).lengthSync();
    final bakLen = File(bakPath).lengthSync();

    expect(
      () => CkvDb.open(dbPath, opts),
      throwsA(
        isA<CorruptedError>().having(
          (e) => e.toString(),
          'message',
          contains(bakPath),
        ),
      ),
      reason: 'the error must point the operator at the recovery copy',
    );

    expect(warnings, isNot(contains('onRecoveredFromBackup')),
        reason: 'no automatic recovery may occur while it is disabled');
    expect(File(dbPath).lengthSync(), equals(primaryLen));
    expect(File(bakPath).lengthSync(), equals(bakLen));
  });

  /// D1 escape hatch: with `autoRecoverFromBackup` enabled the legacy behaviour is
  /// restored deliberately, including the `onRecoveredFromBackup` warning.
  test('corrupt primary heals when explicitly enabled', () {
    final dbPath = p.join(tempBase, 'd1_optin.db');

    final warnings = <String>[];
    final opts = DbOptions(
      autoRecoverFromBackup: true,
      onWarning: (code, _) => warnings.add(code),
    );

    writeSecondGeneration(dbPath, opts);

    final db = CkvDb.open(dbPath, opts);
    // The previous generation held "v1"; the damaged generation held "v1","v2".
    expect(db.getString('', 'version'), equals('v1'));
    db.close();

    expect(warnings, contains('onRecoveredFromBackup'));
  });

  /// G1: the recovery source must never be tail-truncated while it is loaded.
  ///
  /// WAL mode is used deliberately: that is the mode in which the *live* log may
  /// be healed by truncation, so without the fix the recovery source inherits that
  /// behaviour and a torn `.bak` is silently cut down and promoted over the primary.
  test('recovery source is not truncated', () {
    final dbPath = p.join(tempBase, 'g1.db');
    final bakPath = '$dbPath.bak';

    final opts = DbOptions(
      engineMode: StorageEngineMode.appendOnlyLog,
      syncMode: SyncMode.immediate,
      autoRecoverFromBackup: true,
    );

    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'version', 'v1');
    db.compact(); // generation 1 is rotated into `.bak`
    db.putString('', 'version', 'v2'); // generation 2 lands in the log
    db.close();

    expect(File(bakPath).existsSync(), isTrue,
        reason: 'compaction must have produced a `.bak`');

    // Tear the tail of the recovery source.
    final tornLen = File(bakPath).lengthSync() - 4;
    final bakRaf = File(bakPath).openSync(mode: FileMode.append);
    try {
      bakRaf.truncateSync(tornLen);
      bakRaf.flushSync();
    } finally {
      bakRaf.closeSync();
    }

    // Corrupt the primary so the recovery path is taken.
    final primaryLen = File(dbPath).lengthSync();
    flipByteAt(dbPath, primaryLen - 2);

    expect(
      () => CkvDb.open(dbPath, opts),
      throwsA(isA<CorruptedError>()),
      reason: 'a torn recovery source cannot be used and must not be silently repaired',
    );
    expect(File(bakPath).lengthSync(), equals(tornLen),
        reason: '`.bak` must not be truncated while being loaded (G1)');
    expect(File(dbPath).lengthSync(), equals(primaryLen));
  });

  /// D1-9: a read-only open of a corrupt database must fail without writing, and
  /// must ignore `autoRecoverFromBackup`.
  test('read-only never writes on corruption', () {
    final dbPath = p.join(tempBase, 'ro.db');
    final bakPath = '$dbPath.bak';

    writeSecondGeneration(dbPath, DbOptions());

    final primaryLen = File(dbPath).lengthSync();
    final bakLen = File(bakPath).lengthSync();

    expect(
      () => CkvDb.open(dbPath, DbOptions(readOnly: true)),
      throwsA(isA<CorruptedError>()),
      reason: 'read-only open of a corrupt database must fail instead of self-healing',
    );
    expect(
      () => CkvDb.open(
        dbPath,
        DbOptions(readOnly: true, autoRecoverFromBackup: true),
      ),
      throwsA(isA<CorruptedError>()),
      reason: 'read-only mode must ignore autoRecoverFromBackup',
    );

    expect(File(dbPath).lengthSync(), equals(primaryLen),
        reason: 'read-only must not write to the primary file');
    expect(File(bakPath).lengthSync(), equals(bakLen),
        reason: 'read-only must not write to `.bak`');
  });

  /// C12 / T12: corrupt entries (reserved value types or fixed-length types with
  /// wrong length) must be rejected during recovery.
  test('t12 recovery rejects invalid value type or length', () {
    final cases = [
      {'name': 'reserved_vtype', 'vType': 0x05, 'val': utf8.encode('val')},
      {'name': 'bad_int64_len', 'vType': Constants.valTypeInt64, 'val': [1, 2, 3, 4, 5, 6, 7]},
      {'name': 'bad_bool_len', 'vType': Constants.valTypeBool, 'val': [1, 0, 1]},
    ];

    for (final c in cases) {
      final dbPath = p.join(tempBase, '${c['name']}.db');
      final file = File(dbPath);
      final raf = file.openSync(mode: FileMode.write);
      try {
        raf.writeFromSync(FileHeader.createBytes());
        final entry = RawOpEntry(
          opCode: Constants.opPut,
          valueType: c['vType'] as int,
          namespace: Uint8List.fromList(utf8.encode('ns')),
          key: Uint8List.fromList(utf8.encode('k')),
          value: Uint8List.fromList(c['val'] as List<int>),
        );
        BatchFrameWriter.writeFrame(raf, 1, [entry]);
        raf.flushSync();
      } finally {
        raf.closeSync();
      }

      expect(
        () => CkvDb.open(dbPath),
        throwsA(isA<CorruptedError>()),
        reason: 'case ${c['name']} must be rejected by recovery validation (C12)',
      );
    }
  });

  test('t17 reentrant open warns on options conflict', () {
    final tempDir = Directory.systemTemp.createTempSync('ckv_c17_');
    try {
      final dbPath = p.join(tempDir.path, 'c17_conflict.db');
      final db1 = CkvDb.open(dbPath, DbOptions(readOnly: false));
      db1.putString('', 'k', 'v');

      final warnings = <String>[];
      final opts2 = DbOptions(
        readOnly: true,
        syncMode: SyncMode.manual,
        onWarning: (code, msg) => warnings.add('$code: $msg'),
      );

      final db2 = CkvDb.open(dbPath, opts2);
      expect(identical(db1, db2), isTrue);
      expect(warnings.length, equals(1));
      expect(warnings[0].startsWith('onOptionsConflict'), isTrue);
      expect(warnings[0].contains('readOnly'), isTrue);

      db1.close();
    } finally {
      try {
        tempDir.deleteSync(recursive: true);
      } catch (_) {}
    }
  });
}

