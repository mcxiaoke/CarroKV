import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'support/holder.dart';

/// Regression tests for the connection lock (C9, threshold T10).
///
/// Since C9 the lock is a *kernel* lock on `<db>.lock` held for the whole lifetime
/// of a connection:
///
///  * the lock file's contents are irrelevant — the old 32-byte record, its
///    clock-based staleness judgement and the whole "steal the stale lock" dance are
///    gone, so a leftover or malformed lock file must cost nothing at all;
///  * a live holder can never be mistaken for a dead one, and a crashed holder
///    releases the lock by dying, because the kernel owns it;
///  * the lock file is never deleted — unlinking it would let a second connection
///    lock a brand-new file while the first still holds the old one.
///
/// Writable connections take the lock exclusively, read-only ones share it.
///
/// The cross-process half of T10 (a holder in another process that is then killed)
/// is exercised by the Rust suite; here the same properties are checked through the
/// lock primitive plus `Open`'s refusal path.
void main() {
  final tempBase = p.canonicalize(
    p.join(Directory.current.path, '..', '..', 'temp', 'dart_lock_protocol'),
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

  DbOptions shortTimeout({bool readOnly = false}) =>
      DbOptions(lockTimeoutMs: 150, readOnly: readOnly);

  test('lock file contents are irrelevant', () {
    final dbPath = tempPath('garbage_lock.db');
    final lockPath = '$dbPath.lock';
    final garbage = List<int>.generate(40, (i) => (i * 7) & 0xFF);
    File(lockPath).writeAsBytesSync(garbage);

    final started = DateTime.now();
    final db = CkvDb.open(dbPath);
    db.putString('', 'k', 'v');
    db.close();

    expect(DateTime.now().difference(started).inMilliseconds, lessThan(2000),
        reason: 'a leftover lock file must not cost any waiting');
    expect(File(lockPath).existsSync(), isTrue,
        reason: 'the lock file must not be unlinked');
    expect(File(lockPath).readAsBytesSync(), garbage,
        reason: 'the lock file must not be rewritten');
  });

  test('exclusive lock excludes writers and readers', () async {
    final dbPath = tempPath('exclusive.db');
    final lockPath = '$dbPath.lock';

    // A real exclusive kernel lock held by *another process*.
    final holder = await holdLockInAnotherProcess(lockPath);

    try {
      expect(() => CkvDb.open(dbPath, shortTimeout()),
          throwsA(isA<LockedError>()),
          reason: 'a second writer must be rejected');
      expect(() => CkvDb.open(dbPath, shortTimeout(readOnly: true)),
          throwsA(isA<LockedError>()),
          reason: 'a reader must be excluded by a writer');
    } finally {
      await stopHolder(holder);
    }

    final db = CkvDb.open(dbPath);
    db.putString('', 'k', 'v');
    expect(db.getString('', 'k'), 'v');
    db.close();
  });

  test('shared lock allows readers but not writers', () async {
    final dbPath = tempPath('shared.db');
    {
      final db = CkvDb.open(dbPath);
      db.putString('', 'k', 'v');
      db.close();
    }

    final holder = await holdLockInAnotherProcess('$dbPath.lock', shared: true);
    try {
      final reader = CkvDb.open(dbPath, shortTimeout(readOnly: true));
      expect(reader.getString('', 'k'), 'v',
          reason: 'readers must coexist and see the committed data');
      reader.close();

      expect(() => CkvDb.open(dbPath, shortTimeout()),
          throwsA(isA<LockedError>()),
          reason: 'a writer must be excluded by a reader');
    } finally {
      await stopHolder(holder);
    }
  });

  test('the connection holds the lock until close', () async {
    final dbPath = tempPath('lifetime.db');
    final lockPath = '$dbPath.lock';

    final db = CkvDb.open(dbPath);
    db.putString('', 'k', 'v');

    // Windows: LockFileEx is per-handle, so a same-process probe is enough.
    // POSIX: fcntl locks are per-process and a second handle in this process is
    // never excluded; the two tests above hold the lock from a child process,
    // which is the real contract. Here we only need "released on close".
    if (Platform.isWindows) {
      final probe = File(lockPath).openSync(mode: FileMode.append);
      var blocked = false;
      try {
        probe.lockSync(FileLock.exclusive);
      } catch (_) {
        blocked = true;
      }
      expect(blocked, isTrue,
          reason: 'the connection must hold the lock for its whole lifetime');
      probe.closeSync();
    }

    db.close();

    // After close the lock is free again -- without any lock file being deleted.
    final probe2 = await holdLockInAnotherProcess(lockPath);
    await stopHolder(probe2);
    expect(File(lockPath).existsSync(), isTrue);
  });
}
