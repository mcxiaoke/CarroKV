import 'dart:io';
import 'dart:typed_data';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'support/holder.dart';

/// Regression tests for the *open* path's remaining silent-failure modes,
/// mirroring the Rust / Go / .NET suites so every engine stays locked to the same
/// behaviour.
///
/// Covered defects:
///   * C10 - tail healing during open used to run without the inter-process file
///     lock, so a second process could truncate frames another process had just
///     appended. The repair now holds the lock, which is proven here by making the
///     open *fail* (LockedError) while a live lock is held and by asserting that
///     the damaged file was left byte-for-byte untouched.
///   * D1-7 / D1-8 - a missing primary next to a `.bak` used to be "repaired" by
///     promoting that (older) generation, and when the `.bak` itself was
///     unreadable the open silently succeeded as an *empty* database. Both are
///     refused now unless the caller explicitly opts into `.bak` recovery.
const int _fileHeaderLen = 32;

/// Fewer bytes than the 28-byte frame header: the file ends *inside* the last
/// frame.
final Uint8List _tornTail = Uint8List.fromList(
  const [0x4B, 0x56, 0x46, 0x01, 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22],
);

void main() {
  final tempBase = p.canonicalize(
    p.join(Directory.current.path, '..', '..', 'temp', 'dart_open_repair_lock'),
  );
  var seq = 0;

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

  String tempPath(String name) => p.join(tempBase, '${seq++}_$name');

  DbOptions walOptions({int lockTimeoutMs = 10000}) => DbOptions(
        engineMode: StorageEngineMode.appendOnlyLog,
        syncMode: SyncMode.immediate,
        compactionThresholdBytes: 0x7FFFFFFFFFFFFFFF,
        lockTimeoutMs: lockTimeoutMs,
      );

  void appendTornTail(String path) {
    final raf = File(path).openSync(mode: FileMode.append);
    try {
      raf.writeFromSync(_tornTail);
      raf.flushSync();
    } finally {
      raf.closeSync();
    }
  }

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

  /// Writes two generations in Snapshot mode so `.bak` holds the first one.
  void writeTwoGenerations(String dbPath) {
    for (final value in ['v1', 'v2']) {
      final db = CkvDb.open(dbPath);
      db.putString('', 'version', value);
      db.close();
    }
    expect(File('$dbPath.bak').existsSync(), isTrue,
        reason: 'this scenario needs a `.bak` generation');
  }

  /// C10 / T11: repairing a torn tail at open must hold the inter-process lock.
  ///
  /// With a live lock held by "another process", the open must fail with
  /// LockedError and - crucially - must **not** have truncated the log. An
  /// unlocked repair would silently succeed here and discard whatever a
  /// concurrent appender had just written at the tail.
  test('t11 tail repair requires the file lock', () async {
    final dbPath = tempPath('t11.db');
    final opts = walOptions();

    final db = CkvDb.open(dbPath, opts);
    db.putString('', 'a', '1');
    db.putString('', 'b', '2');
    db.putString('', 'c', '3');
    db.close();

    final healthyLen = File(dbPath).lengthSync();
    appendTornTail(dbPath);
    final damagedLen = File(dbPath).lengthSync();
    expect(damagedLen, healthyLen + _tornTail.length);

    // "Another process holds the lock": a child process takes a real exclusive
    // kernel lock. A same-process handle would not do on POSIX, where fcntl
    // record locks are per-process.
    final lockPath = '$dbPath.lock';
    final holder = await holdLockInAnotherProcess(lockPath);

    try {
      expect(
        () => CkvDb.open(dbPath, walOptions(lockTimeoutMs: 100)),
        throwsA(isA<LockedError>()),
        reason: 'tail repair must not proceed without the file lock (C10)',
      );
      expect(File(dbPath).lengthSync(), damagedLen,
          reason: 'refused open must not have truncated the log');
    } finally {
      await stopHolder(holder);
    }

    File(lockPath).deleteSync();

    final healed = CkvDb.open(dbPath, opts);
    expect(healed.getString('', 'a'), '1');
    expect(healed.getString('', 'b'), '2');
    expect(healed.getString('', 'c'), '3');
    healed.close();
    expect(File(dbPath).lengthSync(), healthyLen,
        reason: 'the healed log must end at the last complete frame');
  });

  /// A missing primary next to a *healthy* `.bak` is not a fresh database, so the
  /// default open refuses it, leaves both files untouched, and names the copy an
  /// operator can restore from.
  test('missing primary with healthy backup is refused by default', () {
    final dbPath = tempPath('d17.db');
    writeTwoGenerations(dbPath);
    final bakPath = '$dbPath.bak';
    final bakBefore = File(bakPath).readAsBytesSync();

    File(dbPath).deleteSync();

    expect(
      () => CkvDb.open(dbPath),
      throwsA(isA<CorruptedError>()),
      reason:
          'a missing primary with a `.bak` must not be silently promoted (D1-7)',
    );
    expect(File(dbPath).existsSync(), isFalse,
        reason: 'a refused open must not create a database');
    expect(File(bakPath).readAsBytesSync(), bakBefore,
        reason: 'the offline copy must be left untouched');
  });

  /// The explicit opt-in restores the legacy promotion, with the existing warning
  /// code (onRecoveredFromBackup).
  test(
      'missing primary with healthy backup is promoted when explicitly enabled',
      () {
    final dbPath = tempPath('d17_opt_in.db');
    writeTwoGenerations(dbPath);

    File(dbPath).deleteSync();

    final warnings = <String>[];
    final db = CkvDb.open(
      dbPath,
      DbOptions(
          autoRecoverFromBackup: true,
          onWarning: (code, _) {
            warnings.add(code);
          }),
    );
    expect(File(dbPath).existsSync(), isTrue,
        reason: 'the `.bak` must have been promoted');
    expect(db.getString('', 'version'), 'v1');
    db.close();
    expect(warnings, contains('onRecoveredFromBackup'));
  });

  /// D1-8: a missing primary with an *unreadable* `.bak` used to open as an empty
  /// database, hiding the loss of both generations. It must now be refused.
  test('missing primary with damaged backup is not opened as an empty database',
      () {
    final dbPath = tempPath('d18.db');
    writeTwoGenerations(dbPath);
    final bakPath = '$dbPath.bak';

    flipByteAt(bakPath, _fileHeaderLen + 12);
    File(dbPath).deleteSync();

    expect(
      () => CkvDb.open(dbPath),
      throwsA(isA<CorruptedError>()),
      reason:
          'an unreadable `.bak` next to a missing primary must be refused (D1-8)',
    );
    expect(File(dbPath).existsSync(), isFalse,
        reason: 'a refused open must not leave a fresh empty database behind');
  });

  /// Negative control: with no `.bak` at all the same path is a genuinely new
  /// database and must keep opening as one.
  test('missing primary without backup still opens as a new database', () {
    final dbPath = tempPath('fresh.db');
    expect(File('$dbPath.bak').existsSync(), isFalse);

    final db = CkvDb.open(dbPath);
    db.putString('', 'k', 'v');
    expect(db.getString('', 'k'), 'v');
    db.close();
  });
}
