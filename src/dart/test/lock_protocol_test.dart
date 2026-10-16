import 'dart:io';

import 'package:ckv/ckv.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

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

  test('exclusive lock excludes writers and readers', () {
    final dbPath = tempPath('exclusive.db');
    final lockPath = '$dbPath.lock';
    File(lockPath).writeAsBytesSync(const [0]);

    // A real exclusive kernel lock held by "another connection".
    final holder = File(lockPath).openSync(mode: FileMode.append);
    holder.lockSync(FileLock.exclusive);

    try {
      expect(() => CkvDb.open(dbPath, shortTimeout()),
          throwsA(isA<LockedError>()),
          reason: 'a second writer must be rejected');
      expect(() => CkvDb.open(dbPath, shortTimeout(readOnly: true)),
          throwsA(isA<LockedError>()),
          reason: 'a reader must be excluded by a writer');
    } finally {
      holder.unlockSync();
      holder.closeSync();
    }

    final db = CkvDb.open(dbPath);
    db.putString('', 'k', 'v');
    expect(db.getString('', 'k'), 'v');
    db.close();
  });

  test('shared lock allows readers but not writers', () {
    final dbPath = tempPath('shared.db');
    {
      final db = CkvDb.open(dbPath);
      db.putString('', 'k', 'v');
      db.close();
    }

    final lockPath = '$dbPath.lock';
    File(lockPath).writeAsBytesSync(const [0]);
    final holder = File(lockPath).openSync(mode: FileMode.append);
    holder.lockSync(FileLock.shared);

    try {
      final reader = CkvDb.open(dbPath, shortTimeout(readOnly: true));
      expect(reader.getString('', 'k'), 'v',
          reason: 'readers must coexist and see the committed data');
      reader.close();

      expect(() => CkvDb.open(dbPath, shortTimeout()),
          throwsA(isA<LockedError>()),
          reason: 'a writer must be excluded by a reader');
    } finally {
      holder.unlockSync();
      holder.closeSync();
    }
  });

  test('the connection holds the lock until close', () {
    final dbPath = tempPath('lifetime.db');
    final lockPath = '$dbPath.lock';

    final db = CkvDb.open(dbPath);
    db.putString('', 'k', 'v');

    // While the instance is alive a competing exclusive lock cannot be taken.
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

    db.close();

    // After close the lock is free again -- without any lock file being deleted.
    final probe2 = File(lockPath).openSync(mode: FileMode.append);
    probe2.lockSync(FileLock.exclusive);
    probe2.unlockSync();
    probe2.closeSync();
    expect(File(lockPath).existsSync(), isTrue);
  });
}