import 'dart:io';

import 'errors.dart';
import 'options.dart';

/// The cross-process **connection lock** (C9).
///
/// Since C9 the lock is a *kernel* lock on `<db>.lock` (`fcntl` on Unix,
/// `LockFileEx` on Windows) acquired by `open` and held for the whole lifetime of
/// the connection:
///
///  * a live holder can never be mistaken for a dead one, and a crashed holder
///    releases the lock simply by dying, because the kernel owns it. This is what
///    bbolt (`flock(db, !readOnly)` at `Open`) and LMDB (advisory `fcntl` lock,
///    released by process exit) do; neither has any liveness probing;
///  * the lock file's *contents* no longer take part in the protocol.
///
/// Writable connections take the lock exclusively, read-only connections share it:
/// readers exclude writers but not each other.
///
/// The lock file is created but **never deleted**: unlinking it would let a second
/// connection lock a brand-new file while the first still holds the old one — two
/// writers on the same database with no error at all.
class LockFile {
  final String lockPath;
  final RandomAccessFile _raf;
  bool _released = false;

  LockFile._(this.lockPath, this._raf);

  /// Acquires the lock, waiting up to `options.lockTimeoutMs` for another
  /// connection to release it. Timeouts throw [LockedError]; the lock is never
  /// stolen.
  static LockFile acquire(String dbPath, DbOptions options) {
    final lockPath = '$dbPath.lock';
    final timeout = Duration(milliseconds: options.lockTimeoutMs);
    final mode = options.readOnly ? FileLock.shared : FileLock.exclusive;
    final stopwatch = Stopwatch()..start();

    while (true) {
      try {
        // FileMode.append = read + write + create, without truncating: the lock
        // file's contents are irrelevant, so nothing has to be written to it.
        final raf = File(lockPath).openSync(mode: FileMode.append);
        try {
          raf.lockSync(mode);
          return LockFile._(lockPath, raf);
        } catch (_) {
          raf.closeSync();
          rethrow;
        }
      } catch (e) {
        if (stopwatch.elapsed >= timeout) {
          throw LockedError(
            'Failed to acquire the file lock on "$lockPath" within '
            '${options.lockTimeoutMs}ms: another connection is using this database: $e',
          );
        }
        sleep(const Duration(milliseconds: 50));
      }
    }
  }

  /// Releases the lock. Idempotent; the lock file itself is left in place.
  void release() {
    if (_released) return;
    _released = true;
    try {
      _raf.unlockSync();
    } catch (_) {}
    try {
      _raf.closeSync();
    } catch (_) {}
  }

  void dispose() => release();
}
